using GuildHub.Core.Domain.Enums;

namespace GuildHub.Core.Metadata;

public sealed record ClassInfo(
    GameClass Class,
    ClassRole Role,
    string DisplayName,
    string FileName,
    string EmojiName,
    string FallbackEmoji,
    string HexColor,
    string SoftBgHex,
    string BorderHex);

public sealed record UiIconInfo(
    UiIcon Icon,
    string DisplayName,
    string FileName,
    string EmojiName,
    string FallbackEmoji);

public static class ClassMetadata
{
    public static readonly IReadOnlyList<ClassInfo> All =
    [
        new(GameClass.ToVan, ClassRole.Healer, "Tố Vấn", "tovan.png", "gh_tovan", "🌸", "#EC4899", "#FCE7F3", "#F9A8D4"),
        new(GameClass.ThietY, ClassRole.Tank, "Thiết Y", "thiety.png", "gh_thiety", "🛡️", "#D97706", "#FEF3C7", "#FCD34D"),
        new(GameClass.ThanTuong, ClassRole.Dps, "Thần Tương", "thantuong.png", "gh_thantuong", "🔥", "#2563EB", "#DBEAFE", "#93C5FD"),
        new(GameClass.CuuLinh, ClassRole.Dps, "Cửu Linh", "cuulinh.png", "gh_cuulinh", "🔮", "#7C3AED", "#EDE9FE", "#C4B5FD"),
        new(GameClass.HuyetHa, ClassRole.Dps, "Huyết Hà", "huyetha.png", "gh_huyetha", "🩸", "#DC2626", "#FEE2E2", "#FCA5A5"),
        new(GameClass.ToaiMong, ClassRole.Dps, "Toái Mộng", "toaimong.png", "gh_toaimong", "🗡️", "#0284C7", "#E0F2FE", "#7DD3FC"),
        new(GameClass.LongNgam, ClassRole.Dps, "Long Ngâm", "longngam.png", "gh_longngam", "🐉", "#059669", "#D1FAE5", "#6EE7B7"),
        new(GameClass.HuyenCo, ClassRole.Dps, "Huyền Cơ", "huyenco.png", "gh_huyenco", "⚙️", "#B45309", "#FEF9C3", "#FDE047")
    ];

    private static readonly Dictionary<GameClass, ClassInfo> ByClass = All.ToDictionary(c => c.Class);

    public static ClassInfo Get(GameClass gameClass) => ByClass[gameClass];

    public static bool TryGet(GameClass gameClass, out ClassInfo info) => ByClass.TryGetValue(gameClass, out info!);

    public static string GetStatusLabel(SignupStatus status) => status switch
    {
        SignupStatus.Confirmed => "✅ Tham gia",
        SignupStatus.Late => "🕒 Online trễ (Late)",
        SignupStatus.Bench => "🪑 Dự bị (Bench)",
        SignupStatus.Absent => "🚫 Vắng (Absence)",
        SignupStatus.NotParticipating => "⛔ Không tham gia",
        _ => status.ToString()
    };
}

public static class UiIconMetadata
{
    public static readonly IReadOnlyList<UiIconInfo> All =
    [
        new(UiIcon.Participants, "Tham gia", "thamgia.png", "gh_ui_thamgia", "👥"),
        new(UiIcon.Calendar, "Lịch", "lich.png", "gh_ui_lich", "📅"),
        new(UiIcon.Clock, "Đồng hồ / Đi trễ", "dongho.png", "gh_ui_dongho", "🕒"),
        new(UiIcon.Lock, "Đóng form", "khoa.png", "gh_ui_khoa", "🔒"),
        new(UiIcon.Shutdown, "Tắt / Hạn đóng", "shut-down-line.png", "gh_ui_shutdown", "⏱️"),
        new(UiIcon.Tank, "Tank", "tanker.png", "gh_ui_tanker", "🛡️"),
        new(UiIcon.Dps, "DPS", "dps.png", "gh_ui_dps", "⚔️"),
        new(UiIcon.Healer, "Healer", "heal.png", "gh_ui_heal", "💚"),
        new(UiIcon.Bench, "Dự bị", "dubi.png", "gh_ui_dubi", "🪑"),
        new(UiIcon.Absent, "Vắng mặt", "vangmat.png", "gh_ui_vangmat", "🚫"),
        new(UiIcon.NotParticipating, "Không tham gia / Hủy đăng ký", "khongthamgia.png", "gh_ui_khongthamgia", "❌"),
        new(UiIcon.Edit, "Chỉnh sửa", "edit.png", "gh_ui_edit", "✏️"),
        new(UiIcon.Excel, "Xuất Excel", "excel.png", "gh_ui_excel", "📊")
    ];

    private static readonly Dictionary<UiIcon, UiIconInfo> ByIcon = All.ToDictionary(i => i.Icon);

    public static UiIconInfo Get(UiIcon icon) => ByIcon[icon];
}
