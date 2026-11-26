using GuildHub.Core.Domain.Enums;

namespace GuildHub.Core.Domain.Entities;

public sealed class EventSignup
{
    public long Id { get; set; }
    public long WarEventId { get; set; }
    public WarEvent WarEvent { get; set; } = null!;

    public ulong DiscordUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public GameClass? GameClass { get; set; }
    public SignupStatus Status { get; set; } = SignupStatus.Confirmed;
    public int SignupOrder { get; set; }
    public string? Note { get; set; }
    public bool OptionsEnabled { get; set; } = true;

    public DateTimeOffset SignedUpAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
