namespace GuildHub.Web.Configuration;

public sealed class WebDiscordOptions
{
    public const string SectionName = "DiscordWeb";

    /// <summary>
    /// Discord Application ID (OAuth2 Client ID).
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Discord OAuth2 Client Secret.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Discord Bot Token dùng để đối chiếu server có bot và đồng bộ thành viên / role.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Đường dẫn callback OAuth2 (cần khai báo trùng khớp trong Discord Developer Portal -> OAuth2 -> Redirects).
    /// </summary>
    public string CallbackPath { get; set; } = "/auth/discord-callback";

    /// <summary>
    /// Tùy chọn: URL công khai của Web (VD: https://guild.yourdomain.com). Nếu để trống sẽ tự lấy từ Request.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Thư mục chứa 21 file PNG icon dùng chung với Bot.
    /// </summary>
    public string IconDirectory { get; set; } = "Icon";

    /// <summary>
    /// Danh sách Discord User ID có quyền SuperAdmin (phân tách bằng dấu phẩy), được phép truy cập toàn bộ server mà Bot đang tham gia.
    /// </summary>
    public string? SuperAdminUserIds { get; set; }

    public bool AutoMigrateDatabase { get; set; } = true;

    public bool IsSuperAdmin(ulong userId)
    {
        if (string.IsNullOrWhiteSpace(SuperAdminUserIds))
        {
            return false;
        }

        var parts = SuperAdminUserIds.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (ulong.TryParse(part, out var id) && id == userId)
            {
                return true;
            }
        }

        return false;
    }
}
