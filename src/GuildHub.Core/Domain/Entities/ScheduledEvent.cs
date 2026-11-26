using GuildHub.Core.Domain.Enums;

namespace GuildHub.Core.Domain.Entities;

public sealed class ScheduledEvent
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public EventType EventType { get; set; } = EventType.GuildWar;

    public DateTimeOffset ScheduledTimeUtc { get; set; }
    public DateTimeOffset? CloseTimeUtc { get; set; }

    public bool AutoCreateForm { get; set; }
    public DateTimeOffset? AutoCreateAtUtc { get; set; }
    public bool TriggerFormNow { get; set; }

    public ulong? TargetChannelId { get; set; }
    public string? TargetChannelName { get; set; }
    public ulong? MentionRoleId { get; set; }
    public string? MentionRoleName { get; set; }

    public long? CreatedWarEventId { get; set; }
    public WarEvent? CreatedWarEvent { get; set; }
    public DateTimeOffset? FormCreatedAtUtc { get; set; }

    public string? LastFormError { get; set; }
    public DateTimeOffset? LastFormAttemptAtUtc { get; set; }

    public bool IsCancelled { get; set; }
    public ulong CreatedByDiscordId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
