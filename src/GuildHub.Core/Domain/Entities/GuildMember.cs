using GuildHub.Core.Domain.Enums;

namespace GuildHub.Core.Domain.Entities;

public sealed class GuildMember
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>
    /// Discord User ID (null nếu là thành viên thêm thủ công / ngoại binh chưa có Discord ID).
    /// </summary>
    public ulong? DiscordUserId { get; set; }

    public string DiscordUsername { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? IngameNickname { get; set; }
    public string? AvatarUrl { get; set; }

    public GameClass? GameClass { get; set; }
    public bool HasGuildRole { get; set; } = true;
    public bool IsRemoved { get; set; }
    public bool IsExternal { get; set; }

    public DateTimeOffset? RemovedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string EffectiveName => !string.IsNullOrWhiteSpace(IngameNickname) ? IngameNickname! : DisplayName;
}
