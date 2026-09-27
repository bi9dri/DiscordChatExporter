using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal partial class ExportAssetDownloader(
    string workingDirPath,
    bool reuse,
    bool convertImagesToWebp = false
)
{
    private static readonly AsyncKeyedLocker<string> Locker = new();

    // File paths of the previously downloaded assets
    private readonly Dictionary<string, string> _previousPathsByUrl = new(StringComparer.Ordinal);

    public async ValueTask<string> DownloadAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        var downloadUrl = convertImagesToWebp ? GetWebpVariantUrl(url) : url;

        // The extension must match what the CDN actually serves, which may differ from
        // the original URL after the WebP rewrite (e.g. .gif becomes an animated .webp)
        var fileName = GetFileNameFromUrl(
            url,
            extensionOverride: convertImagesToWebp ? GetWebpExtension(url) : null
        );
        var filePath = Path.Combine(workingDirPath, fileName);

        using var _ = await Locker.LockAsync(filePath, cancellationToken);

        if (_previousPathsByUrl.TryGetValue(url, out var cachedFilePath))
            return cachedFilePath;

        // Reuse existing files if we're allowed to
        if (reuse && File.Exists(filePath))
            return _previousPathsByUrl[url] = filePath;

        // Check for a file cached by the legacy naming scheme (5-char hash) and rename it
        // to the new naming scheme to preserve backwards compatibility with existing exports.
        // This will catch both the 5-char lowercase hash and the 5-char uppercase hash variants.
        // ponytail: the legacy names never used a WebP extension override, so don't attempt
        // legacy reuse when converting
        if (reuse && !convertImagesToWebp)
        {
            var legacyFileNames = GetLegacyFileNamesFromUrl(url);
            foreach (var legacyFileName in legacyFileNames)
            {
                var legacyFilePath = Path.Combine(workingDirPath, legacyFileName);
                if (File.Exists(legacyFilePath))
                {
                    // Overwrite in case the destination file was created concurrently between our
                    // earlier existence check and this move operation
                    try
                    {
                        File.Move(legacyFilePath, filePath, true);
                        return _previousPathsByUrl[url] = filePath;
                    }
                    catch (IOException)
                    {
                        // The legacy file was moved or deleted concurrently or something else happened.
                        // Upgrading old files is not crucial, so we can just move on.
                    }
                }
            }
        }

        Directory.CreateDirectory(workingDirPath);

        await Http.ResiliencePipeline.ExecuteAsync(
            async innerCancellationToken =>
            {
                // Download the file
                using var response = await Http.Client.GetAsync(
                    downloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    innerCancellationToken
                );

                response.EnsureSuccessStatusCode();

                await using var output = File.Create(filePath);
                await response.Content.CopyToAsync(output, innerCancellationToken);
            },
            cancellationToken
        );

        return _previousPathsByUrl[url] = filePath;
    }
}

internal partial class ExportAssetDownloader
{
    internal static string NormalizeUrl(string url)
    {
        // Remove signature parameters from Discord CDN/media URLs to normalize them
        var uri = new Uri(url);

        if (
            !string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "media.discordapp.net", StringComparison.OrdinalIgnoreCase)
        )
        {
            return url;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        query.Remove("ex");
        query.Remove("is");
        query.Remove("hm");

        return uri.GetLeftPart(UriPartial.Path) + query;
    }

    private static string GetFileNameFromUrl(string url, string urlHash, string? extensionOverride)
    {
        // Try to extract the file name from URL
        var fileName = new Uri(url, UriKind.RelativeOrAbsolute).TryGetFileName();

        // If it's not there, just use the URL hash as the file name
        if (string.IsNullOrWhiteSpace(fileName))
            return urlHash;

        // Otherwise, use the original file name but inject the hash in the middle
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var fileExtension = extensionOverride ?? Path.GetExtension(fileName);

        // Probably not a file extension, just a dot in a long file name
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/812
        if (fileExtension.Length > 41)
        {
            fileNameWithoutExtension = fileName;
            fileExtension = "";
        }

        return Path.EscapeFileName(
            fileNameWithoutExtension.Truncate(42) + '-' + urlHash + fileExtension
        );
    }

    private static string GetFileNameFromUrl(string url) =>
        GetFileNameFromUrl(
            url,
            // 16 chars = 64 bits, reaches 1% collision probability at ~609 million files
            SHA256
                .HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)))
                .Pipe(Convert.ToHexStringLower)
                .Truncate(16),
            null
        );

    private static string GetFileNameFromUrl(string url, string? extensionOverride) =>
        GetFileNameFromUrl(
            url,
            SHA256
                .HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)))
                .Pipe(Convert.ToHexStringLower)
                .Truncate(16),
            extensionOverride
        );

    // Legacy naming used a 5-char hash, kept for backwards compatibility with existing exports
    private static IReadOnlyList<string> GetLegacyFileNamesFromUrl(string url)
    {
        var hashData = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)));

        return
        [
            // Lowercase variant (introduced in 2.46.1)
            GetFileNameFromUrl(url, Convert.ToHexStringLower(hashData).Truncate(5), null),
            // Uppercase variant (original)
            GetFileNameFromUrl(url, Convert.ToHexString(hashData).Truncate(5), null),
        ];
    }

    // Discord's media proxy can transcode images to WebP on the fly, which is what the
    // official client does as well. External images (Twitter, YouTube thumbnails, etc.)
    // are not served by Discord, so they're kept as-is.
    private static bool CanConvertToWebp(Uri uri) => IsDiscordAssetHost(uri.Host);

    // Animated assets are served as static WebP unless explicitly requested otherwise,
    // so the query needs an extra parameter that has no effect on static images.
    private static bool IsAnimated(Uri uri) =>
        // Animated user assets are flagged in their hash, animated attachments are not
        Path.GetFileNameWithoutExtension(uri.AbsolutePath)
            .StartsWith("a_", StringComparison.Ordinal)
        // Animated attachments: .gif (Discord re-serves them as animated WebP)
        || string.Equals(
            Path.GetExtension(uri.AbsolutePath),
            ".gif",
            StringComparison.OrdinalIgnoreCase
        );

    private static string GetWebpVariantUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !CanConvertToWebp(uri))
            return url;

        var builder = new UriBuilder(uri) { Host = "media.discordapp.net" };

        var query = HttpUtility.ParseQueryString(builder.Query);
        query["format"] = "webp";

        if (IsAnimated(uri))
            query["animated"] = "true";

        // Non-null: the assignments above guarantee at least one parameter
        builder.Query = query.ToString()!;

        return builder.Uri.AbsoluteUri;
    }

    // Animated assets keep their original extension: Discord serves them as .webp, but
    // relying on the extension matching the bytes is only safe for the static case, and
    // a wrong extension only affects the subresource MIME sniff, not rendering.
    private static string? GetWebpExtension(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && CanConvertToWebp(uri)
        && !IsAnimated(uri)
            ? ".webp"
            : null;

    private static bool IsDiscordAssetHost(string host) =>
        host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);
}
