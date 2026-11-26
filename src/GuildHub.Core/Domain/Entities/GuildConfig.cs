namespace GuildHub.Core.Domain.Entities;

public sealed class GuildConfig
{
    public ulong GuildId { get; set; }
    public string GuildName { get; set; } = string.Empty;
    public string? IconUrl { get; set; }

    /// <summary>
    /// Role Discord dùng để xác định thành viên chính thức của bang (VD: @HYPER - Celestium).
    /// </summary>
    public ulong? MemberRoleId { get; set; }
    public string? MemberRoleName { get; set; }

    public DateTimeOffset? LastSyncedAtUtc { get; set; }
    public int LastSyncedMemberCount { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
