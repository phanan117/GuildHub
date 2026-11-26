using GuildHub.Core.Domain.Enums;

namespace GuildHub.Core.Domain.Entities;

public sealed class WarEvent
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public EventType EventType { get; set; } = EventType.GuildWar;

    public DateTimeOffset? EventTimeUtc { get; set; }
    public DateTimeOffset? CloseTimeUtc { get; set; }
    public ulong? MentionRoleId { get; set; }

    public ulong LeaderDiscordId { get; set; }
    public bool IsClosed { get; set; }
    public bool OnlineReminderSent { get; set; }
    public int NextSignupOrder { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<EventSignup> Signups { get; set; } = new List<EventSignup>();

    public bool IsEffectivelyClosed(DateTimeOffset nowUtc) =>
        IsClosed || (CloseTimeUtc.HasValue && CloseTimeUtc.Value <= nowUtc);
}
