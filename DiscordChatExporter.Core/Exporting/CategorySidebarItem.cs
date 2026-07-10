using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting;

// Sidebar entry for a non-empty channel or thread in a category export
internal record CategorySidebarItem(Snowflake Id, string Name, Snowflake? ParentId, bool IsThread);
