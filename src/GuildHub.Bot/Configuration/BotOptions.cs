namespace GuildHub.Bot.Configuration;

public sealed class BotOptions
{
    public const string SectionName = "DiscordBot";

    /// <summary>
    /// Token của Discord Bot (lấy từ Discord Developer Portal).
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Tùy chọn: ID server để đăng ký slash command ngay lập tức (nhanh hơn global sync).
    /// Để null hoặc 0 nếu muốn đăng ký Global cho mọi server.
    /// </summary>
    public ulong? DevGuildId { get; set; }

    /// <summary>
    /// Đường dẫn tới thư mục chứa các file .png icon hệ phái.
    /// </summary>
    public string IconDirectory { get; set; } = "Icon";

    /// <summary>
    /// Tự động chạy EF Core Migration khi khởi động bot.
    /// </summary>
    public bool AutoMigrateDatabase { get; set; } = true;

    /// <summary>
    /// Tự động upload/đồng bộ các file PNG trong thư mục Icon thành Discord Application Emojis.
    /// </summary>
    public bool AutoSyncIcons { get; set; } = true;
}
