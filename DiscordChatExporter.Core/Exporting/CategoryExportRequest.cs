using System.Collections.Generic;
using DiscordChatExporter.Core.Discord.Data;

namespace DiscordChatExporter.Core.Exporting;

public partial record CategoryExportRequest(
    Guild Guild,
    Channel Category,
    // Text channels directly under the category, in position order
    IReadOnlyList<Channel> Channels,
    // Threads of those channels
    IReadOnlyList<Channel> Threads,
    string OutputFilePath,
    // Only HtmlDark and HtmlLight are supported
    ExportFormat Format
);
