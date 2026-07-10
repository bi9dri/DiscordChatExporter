using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using Gress;
using PowerKit.Extensions;
using Spectre.Console;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "exportcategory",
    Description = "Exports all text channels in a category into a single self-contained HTML file."
)]
public partial class ExportCategoryCommand : DiscordCommandBase
{
    [CommandOption("category", 'c', Description = "Category ID.")]
    public required Snowflake CategoryId { get; set; }

    [CommandOption(
        "output",
        'o',
        Description = "Output file or directory path. "
            + "If a directory is specified, the file name will be generated automatically "
            + "based on the server and category names."
    )]
    public string OutputPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        set => field = Path.GetFullPath(value);
    } = Directory.GetCurrentDirectory();

    [CommandOption(
        "format",
        'f',
        Description = "Export format. Only HTML formats (HtmlDark, HtmlLight) are supported."
    )]
    public ExportFormat ExportFormat { get; set; } = ExportFormat.HtmlDark;

    private string GetOutputFilePath(Guild guild, Channel category)
    {
        // Output is a directory
        if (
            Directory.Exists(OutputPath)
            || Path.EndsInDirectorySeparator(OutputPath)
            || string.IsNullOrWhiteSpace(Path.GetExtension(OutputPath))
        )
        {
            var fileName = Path.EscapeFileName(
                $"{guild.Name} - {category.Name} [{category.Id}].html"
            );

            return Path.Combine(OutputPath, fileName);
        }

        // Output is a file
        return OutputPath;
    }

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        await base.ExecuteAsync(console);

        var cancellationToken = console.RegisterCancellationHandler();

        if (ExportFormat is not (ExportFormat.HtmlDark or ExportFormat.HtmlLight))
        {
            throw new CommandException(
                "Only HTML formats (HtmlDark, HtmlLight) are supported by this command."
            );
        }

        await console.Output.WriteLineAsync("Resolving category...");

        var category = await Discord.GetChannelAsync(CategoryId, cancellationToken);
        if (!category.IsCategory)
        {
            throw new CommandException($"Channel '{CategoryId}' is not a category.");
        }

        var guild = await Discord.GetGuildAsync(category.GuildId, cancellationToken);

        var channels = (await Discord.GetGuildChannelsAsync(category.GuildId, cancellationToken))
            .Where(c => c.Parent?.Id == category.Id && c.Kind == ChannelKind.GuildTextChat)
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.Id)
            .ToArray();

        if (!channels.Any())
        {
            throw new CommandException(
                $"Category '{category.Name}' does not contain any text channels."
            );
        }

        await console.Output.WriteLineAsync("Fetching threads...");

        var threads = new List<Channel>();
        await console
            .CreateStatusTicker()
            .StartAsync(
                "...",
                async ctx =>
                {
                    await foreach (
                        var thread in Discord.GetChannelThreadsAsync(
                            channels,
                            includeArchived: true,
                            before: null,
                            after: null,
                            cancellationToken
                        )
                    )
                    {
                        // Empty threads are omitted from the export anyway,
                        // so don't bother collecting them.
                        if (thread.IsEmpty)
                            continue;

                        threads.Add(thread);

                        ctx.Status(Markup.Escape($"Fetched '{thread.GetHierarchicalName()}'."));
                    }
                }
            );

        await console.Output.WriteLineAsync($"Fetched {threads.Count} thread(s).");

        var outputFilePath = GetOutputFilePath(guild, category);

        await console.Output.WriteLineAsync(
            $"Exporting {channels.Length} channel(s) and {threads.Count} thread(s)..."
        );

        await console
            .CreateProgressTicker()
            .StartAsync(async ctx =>
            {
                await ctx.StartTaskAsync(
                    Markup.Escape(category.GetHierarchicalName()),
                    async progress =>
                    {
                        var request = new CategoryExportRequest(
                            guild,
                            category,
                            channels,
                            threads,
                            outputFilePath,
                            ExportFormat
                        );

                        await new CategoryExporter(Discord).ExportAsync(
                            request,
                            progress.ToPercentageBased(),
                            cancellationToken
                        );
                    }
                );
            });

        using (console.WithForegroundColor(ConsoleColor.White))
        {
            await console.Output.WriteLineAsync($"Successfully exported to '{outputFilePath}'.");
        }
    }
}
