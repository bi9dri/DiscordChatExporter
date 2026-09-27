using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using Gress;
using WebMarkupMin.Core;

namespace DiscordChatExporter.Core.Exporting;

// Exports all text channels and threads of a category into an HTML file
// accompanied by a sidecar directory of downloaded assets.
public partial class CategoryExporter(DiscordClient discord)
{
    private readonly HtmlMinifier _minifier = new();

    // Use <!--wmm:ignore--> to preserve blocks of code inside the templates
    private string Minify(string html) => _minifier.Minify(html, false).MinifiedContent;

    private async ValueTask WriteMessageGroupAsync(
        ExportContext context,
        IReadOnlyList<Message> messages,
        TextWriter writer,
        CancellationToken cancellationToken
    )
    {
        await writer.WriteLineAsync(
            Minify(
                await new MessageGroupTemplate
                {
                    Context = context,
                    Messages = messages,
                }.RenderAsync(cancellationToken)
            )
        );
    }

    private async ValueTask<long> ExportChannelSectionAsync(
        CategoryExportRequest request,
        Channel channel,
        IReadOnlyDictionary<Snowflake, Channel> threadsById,
        Dictionary<Snowflake, ThreadAnchor> threadAnchorsByThreadId,
        ExportAssetDownloader assetDownloader,
        TextWriter writer,
        IProgress<Percentage>? progress,
        CancellationToken cancellationToken
    )
    {
        if (channel.IsEmpty)
            return 0;

        var context = new ExportContext(
            discord,
            CreateSyntheticRequest(request, channel),
            assetDownloader
        );

        var messageGroup = new List<Message>();
        var messagesWritten = 0L;
        var isHeaderWritten = false;

        try
        {
            await context.PopulateChannelsAndRolesAsync(cancellationToken);

            await foreach (
                var message in discord.GetMessagesAsync(
                    channel.Id,
                    null,
                    null,
                    progress,
                    cancellationToken
                )
            )
            {
                // Record the positions where thread links should be inserted by the boot script
                if (!channel.IsThread)
                {
                    if (
                        message.Kind == MessageKind.ThreadCreated
                        && message.Reference?.ChannelId is { } createdThreadId
                        && threadsById.ContainsKey(createdThreadId)
                    )
                    {
                        threadAnchorsByThreadId.TryAdd(
                            createdThreadId,
                            new ThreadAnchor(message.Id, channel.Id)
                        );
                    }

                    // Threads started from a message share their ID with that message
                    if (threadsById.ContainsKey(message.Id))
                    {
                        threadAnchorsByThreadId.TryAdd(
                            message.Id,
                            new ThreadAnchor(message.Id, channel.Id)
                        );
                    }
                }

                // Resolve members for referenced users
                foreach (var user in message.GetReferencedUsers())
                    await context.PopulateMemberAsync(user, cancellationToken);

                // Delay the section header until the first message, so that channels
                // that turn out to be empty don't produce a section at all.
                if (!isHeaderWritten)
                {
                    await writer.WriteLineAsync(
                        Minify(
                            await new CategoryChannelPreambleTemplate
                            {
                                Context = context,
                                Channel = channel,
                            }.RenderAsync(cancellationToken)
                        )
                    );

                    isHeaderWritten = true;
                }

                if (
                    messageGroup.Count > 0
                    && !HtmlMessageWriter.CanJoinGroup(message, messageGroup[^1])
                )
                {
                    await WriteMessageGroupAsync(context, messageGroup, writer, cancellationToken);

                    messageGroup.Clear();
                }

                messageGroup.Add(message);
                messagesWritten++;
            }
        }
        // Skip the rest of the channel on non-fatal errors (e.g. missing access)
        catch (DiscordChatExporterException ex) when (!ex.IsFatal) { }

        // Flush the last message group
        if (messageGroup.Count > 0)
            await WriteMessageGroupAsync(context, messageGroup, writer, cancellationToken);

        // Close the 'chatlog' and 'chatlog-tab' elements opened by the section preamble
        if (isHeaderWritten)
            await writer.WriteLineAsync("</div></div>");

        return messagesWritten;
    }

    public async ValueTask ExportAsync(
        CategoryExportRequest request,
        IProgress<Percentage>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (request.Format is not (ExportFormat.HtmlDark or ExportFormat.HtmlLight))
        {
            throw new ArgumentException(
                $"Format '{request.Format}' is not supported for category exports.",
                nameof(request)
            );
        }

        var themeName = request.Format == ExportFormat.HtmlDark ? "Dark" : "Light";

        // A single downloader instance is shared across all sections and the shell, so that
        // assets appearing in multiple channels are only downloaded once.
        var assetDownloader = new ExportAssetDownloader(
            request.AssetsDirPath,
            reuse: false,
            convertImagesToWebp: true
        );

        // Each channel is immediately followed by its threads, oldest first
        var sections = new List<Channel>();
        foreach (var channel in request.Channels)
        {
            sections.Add(channel);
            sections.AddRange(
                request.Threads.Where(t => t.Parent?.Id == channel.Id).OrderBy(t => t.Id)
            );
        }

        var threadsById = request.Threads.ToDictionary(t => t.Id);

        var sidebarItems = new List<CategorySidebarItem>();
        var threadAnchorsByThreadId = new Dictionary<Snowflake, ThreadAnchor>();
        var totalMessagesWritten = 0L;

        var dirPath = Path.GetDirectoryName(request.OutputFilePath);
        if (!string.IsNullOrWhiteSpace(dirPath))
            Directory.CreateDirectory(dirPath);

        var tempFilePath = request.OutputFilePath + ".tmp";

        try
        {
            // Write the chatlog sections to a temporary file first, because the sidebar
            // content is not known until all channels have been exported.
            await using (var tempWriter = new StreamWriter(File.Create(tempFilePath)))
            {
                for (var i = 0; i < sections.Count; i++)
                {
                    var channel = sections[i];
                    var channelIndex = i;

                    var channelProgress = progress?.WithTransform<Percentage>(p =>
                        Percentage.FromFraction((channelIndex + p.Fraction) / sections.Count)
                    );

                    var messagesWritten = await ExportChannelSectionAsync(
                        request,
                        channel,
                        threadsById,
                        threadAnchorsByThreadId,
                        assetDownloader,
                        tempWriter,
                        channelProgress,
                        cancellationToken
                    );

                    // Empty channels are omitted from the sidebar
                    if (messagesWritten > 0)
                    {
                        sidebarItems.Add(
                            new CategorySidebarItem(
                                channel.Id,
                                channel.Name,
                                channel.IsThread ? channel.Parent?.Id : null,
                                channel.IsThread
                            )
                        );

                        totalMessagesWritten += messagesWritten;
                    }

                    progress?.Report(Percentage.FromFraction((i + 1.0) / sections.Count));
                }
            }

            // Only link threads that actually produced a section, from parents that did too
            var writtenChannelIds = sidebarItems.Select(i => i.Id).ToHashSet();
            var threadLinks = threadAnchorsByThreadId
                .Where(kvp =>
                    writtenChannelIds.Contains(kvp.Key)
                    && writtenChannelIds.Contains(kvp.Value.ParentChannelId)
                )
                .Select(kvp => new CategoryThreadLink(
                    kvp.Key,
                    kvp.Value.AnchorMessageId,
                    kvp.Value.ParentChannelId,
                    threadsById[kvp.Key].Name
                ))
                .ToArray();

            var shellContext = new ExportContext(
                discord,
                CreateSyntheticRequest(request, request.Category),
                assetDownloader
            );

            await using (var outputWriter = new StreamWriter(File.Create(request.OutputFilePath)))
            {
                await outputWriter.WriteLineAsync(
                    Minify(
                        await new CategoryPreambleTemplate
                        {
                            Context = shellContext,
                            ThemeName = themeName,
                            SidebarItems = sidebarItems,
                        }.RenderAsync(cancellationToken)
                    )
                );

                // Copy the chatlog sections from the temporary file
                await outputWriter.FlushAsync(cancellationToken);
                await using (var tempStream = File.OpenRead(tempFilePath))
                    await tempStream.CopyToAsync(outputWriter.BaseStream, cancellationToken);

                await outputWriter.WriteLineAsync(
                    Minify(
                        await new CategoryPostambleTemplate
                        {
                            Context = shellContext,
                            MessagesWrittenTotal = totalMessagesWritten,
                            ThreadLinks = threadLinks,
                        }.RenderAsync(cancellationToken)
                    )
                );
            }
        }
        finally
        {
            if (File.Exists(tempFilePath))
                File.Delete(tempFilePath);
        }

        progress?.Report(Percentage.FromValue(100));
    }
}

public partial class CategoryExporter
{
    private readonly record struct ThreadAnchor(
        Snowflake AnchorMessageId,
        Snowflake ParentChannelId
    );

    private static ExportRequest CreateSyntheticRequest(
        CategoryExportRequest request,
        Channel channel
    ) =>
        new(
            request.Guild,
            channel,
            request.OutputFilePath,
            assetsDirPath: null,
            request.Format,
            after: null,
            before: null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets: true,
            shouldReuseAssets: false,
            locale: null,
            isUtcNormalizationEnabled: false
        );
}
