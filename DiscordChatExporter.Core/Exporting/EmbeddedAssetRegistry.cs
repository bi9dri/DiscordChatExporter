using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal partial class EmbeddedAssetRegistry
{
    private readonly AsyncKeyedLocker<string> _locker = new();
    private readonly Lock _lock = new();

    // Resolved values (asset keys or original URLs for failed downloads) of the previously requested assets
    private readonly Dictionary<string, string> _resolvedValuesByUrl = new(StringComparer.Ordinal);

    // Entries registered since the last drain
    private readonly List<KeyValuePair<string, string>> _pendingEntries = [];

    public async ValueTask<string> ResolveAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedUrl = NormalizeUrl(url);

        using var _ = await _locker.LockAsync(normalizedUrl, cancellationToken);

        lock (_lock)
        {
            if (_resolvedValuesByUrl.TryGetValue(normalizedUrl, out var cachedValue))
                return cachedValue;
        }

        try
        {
            var dataUri = await DownloadAsDataUriAsync(url, cancellationToken);
            var urlHash = GetUrlHash(normalizedUrl);

            lock (_lock)
            {
                _pendingEntries.Add(new KeyValuePair<string, string>(urlHash, dataUri));
                return _resolvedValuesByUrl[normalizedUrl] = "asset://" + urlHash;
            }
        }
        // Try to catch only exceptions related to failed HTTP requests
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/332
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/372
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            lock (_lock)
            {
                return _resolvedValuesByUrl[normalizedUrl] = url;
            }
        }
    }

    // Resolves the URL to a complete data URI without registering it, for assets
    // that are only referenced once (e.g. fonts, stylesheets, and scripts in <head>).
    public async ValueTask<string> ResolveDirectAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await DownloadAsDataUriAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return url;
        }
    }

    // Returns the entries registered since the last drain and clears the pending list,
    // so that the caller can flush them to the output without keeping all assets in memory.
    public IReadOnlyList<KeyValuePair<string, string>> DrainPendingEntries()
    {
        lock (_lock)
        {
            if (_pendingEntries.Count == 0)
                return [];

            var entries = _pendingEntries.ToArray();
            _pendingEntries.Clear();

            return entries;
        }
    }
}

internal partial class EmbeddedAssetRegistry
{
    private static string NormalizeUrl(string url) => ExportAssetDownloader.NormalizeUrl(url);

    private static string GetUrlHash(string normalizedUrl) =>
        // 16 chars = 64 bits, reaches 1% collision probability at ~609 million files
        SHA256
            .HashData(Encoding.UTF8.GetBytes(normalizedUrl))
            .Pipe(Convert.ToHexStringLower)
            .Truncate(16);

    private static string? TryGetMimeTypeFromUrl(string url)
    {
        var fileExtension = Path.GetExtension(new Uri(url).AbsolutePath);

        return fileExtension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".pdf" => "application/pdf",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".json" => "application/json",
            _ => null,
        };
    }

    private static async ValueTask<string> DownloadAsDataUriAsync(
        string url,
        CancellationToken cancellationToken
    ) =>
        await Http.ResiliencePipeline.ExecuteAsync(
            async innerCancellationToken =>
            {
                // Download the file
                using var response = await Http.Client.GetAsync(url, innerCancellationToken);

                response.EnsureSuccessStatusCode();

                var data = await response.Content.ReadAsByteArrayAsync(innerCancellationToken);

                var mimeType =
                    response.Content.Headers.ContentType?.MediaType
                    ?? TryGetMimeTypeFromUrl(url)
                    ?? "application/octet-stream";

                return "data:" + mimeType + ";base64," + Convert.ToBase64String(data);
            },
            cancellationToken
        );
}
