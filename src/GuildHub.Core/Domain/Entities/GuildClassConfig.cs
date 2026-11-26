using GuildHub.Core.Domain.Enums;

namespace GuildHub.Core.Domain.Entities;

public sealed class GuildClassConfig
{
    public ulong GuildId { get; set; }
    public GameClass GameClass { get; set; }

    /// <summary>
    /// Tên hiển thị hệ phái tùy chỉnh cho server (mặc định theo ClassMetadata).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Vai trò hệ phái (Tank / DPS / Healer).
    /// </summary>
    public ClassRole Role { get; set; } = ClassRole.Dps;

    /// <summary>
    /// Mã màu đại diện cho hệ phái (VD: #EC4899) dùng trên biểu đồ & thẻ đội hình.
    /// </summary>
    public string HexColor { get; set; } = "#6366F1";

    /// <summary>
    /// Tên file icon hoặc đường dẫn tương đối trong thư mục Icon/ (VD: tovan.png hoặc custom/{guildId}/...).
    /// </summary>
    public string IconFileName { get; set; } = string.Empty;

    /// <summary>
    /// Role Discord tương ứng với hệ phái này (tự động gán khi Link / gỡ khi Hủy link).
    /// </summary>
    public ulong? DiscordRoleId { get; set; }
    public string? DiscordRoleName { get; set; }

    public int SortOrder { get; set; }
    public bool IsCustom { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
