using GuildHub.Core.Domain.Enums;

namespace GuildHub.Core.Domain.Entities;

public sealed class UiEmoji
{
    public UiIcon UiIcon { get; set; }
    public string EmojiName { get; set; } = string.Empty;
    public ulong EmojiId { get; set; }
    public string FileSha256 { get; set; } = string.Empty;
    public DateTimeOffset SyncedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
