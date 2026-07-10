using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting;

// Describes where the boot script should insert a link to a thread's tab
// within the parent channel's chatlog
internal record CategoryThreadLink(
    Snowflake ThreadId,
    Snowflake AnchorMessageId,
    Snowflake ParentChannelId,
    string ThreadName
);
