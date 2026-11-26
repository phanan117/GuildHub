using GuildHub.Core.Metadata;
using System.Text;
using Discord;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;

namespace GuildHub.Bot.Services;

public enum NoticeKind
{
    Success,
    Info,
    Warning,
    Error
}

public sealed class EventMessageBuilder(IconSyncService iconSyncService)
{
    private static readonly Color AccentPurple = new(0x7B, 0x3F, 0xE4);
    private static readonly Color ClosedGray = new(0x64, 0x74, 0x8B);
    private static readonly Color WarningAmber = new(0xF5, 0x9E, 0x0B);
    private static readonly Color ErrorRed = new(0xEF, 0x44, 0x44);
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    /// <summary>
    /// Dựng Embed chính của form đăng ký sử dụng toàn bộ icon custom đã đồng bộ từ thư mục Icon/:
    /// - Phần trên: Tiêu đề, mô tả, 3 cột thông tin (Leader / Tổng số / Giờ online & Giờ đóng form), 3 cột Tank/DPS/Healer.
    /// - Phần giữa: Các cột hệ phái xếp 3 cột/hàng.
    /// - Phần cuối (footer section): Liệt kê danh sách Đi trễ (Late), Dự bị (Bench), Vắng mặt (Absence).
    /// </summary>
    public Embed BuildEventEmbed(WarEvent ev)
    {
        var now = DateTimeOffset.UtcNow;
        var isClosed = ev.IsEffectivelyClosed(now);

        var participantsIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Participants);
        var calendarIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Calendar);
        var clockIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Clock);
        var lockIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Lock);
        var tankIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Tank);
        var dpsIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Dps);
        var healerIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Healer);

        // Danh sách hiển thị theo hệ phái ở phía trên: bao gồm Tham gia, Đi trễ và Dự bị.
        // Chỉ Vắng mặt (Absent) và Không tham gia (NotParticipating) mới không hiện ở danh sách phía trên.
        var activeRoster = ev.Signups
            .Where(s => s.GameClass.HasValue &&
                        s.Status is SignupStatus.Confirmed or SignupStatus.Late or SignupStatus.Bench)
            .OrderBy(s => s.SignupOrder)
            .ToList();

        var confirmedCount = activeRoster.Count(s => s.Status == SignupStatus.Confirmed);

        var lateList = ev.Signups
            .Where(s => s.Status == SignupStatus.Late)
            .OrderBy(s => s.SignupOrder)
            .ToList();

        var benchList = ev.Signups
            .Where(s => s.Status == SignupStatus.Bench)
            .OrderBy(s => s.SignupOrder)
            .ToList();

        var absentList = ev.Signups
            .Where(s => s.Status == SignupStatus.Absent)
            .OrderBy(s => s.SignupOrder)
            .ToList();

        var notParticipatingList = ev.Signups
            .Where(s => s.Status == SignupStatus.NotParticipating)
            .OrderBy(s => s.SignupOrder)
            .ToList();

        var tankCount = activeRoster.Count(s => ClassMetadata.Get(s.GameClass!.Value).Role == ClassRole.Tank);
        var dpsCount = activeRoster.Count(s => ClassMetadata.Get(s.GameClass!.Value).Role == ClassRole.Dps);
        var healerCount = activeRoster.Count(s => ClassMetadata.Get(s.GameClass!.Value).Role == ClassRole.Healer);

        var descBuilder = new StringBuilder();
        if (isClosed)
        {
            descBuilder.AppendLine($"{lockIcon} **ĐÃ ĐÓNG ĐĂNG KÝ**");
        }

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            descBuilder.AppendLine(ev.Description);
        }
        else if (ev.EventTimeUtc.HasValue)
        {
            var onlineVn = ev.EventTimeUtc.Value.ToOffset(VietnamOffset);
            descBuilder.AppendLine($"Tập trung online lúc **{onlineVn:HH:mm}**");
        }

        var embed = new EmbedBuilder()
            .WithColor(isClosed ? ClosedGray : AccentPurple)
            .WithTitle(ev.Title)
            .WithDescription(descBuilder.ToString().TrimEnd());

        // --- HÀNG 1 (3 CỘT THÔNG TIN GỌN 2 DÒNG/CỘT) ---
        var col1 = new StringBuilder();
        col1.AppendLine($"🏳️ <@{ev.LeaderDiscordId}>");
        if (ev.EventTimeUtc.HasValue)
        {
            var onlineUnix = ev.EventTimeUtc.Value.ToUnixTimeSeconds();
            col1.AppendLine($"{calendarIcon} <t:{onlineUnix}:D>");
        }

        var extraCount = lateList.Count + benchList.Count;
        var extraText = extraCount > 0 ? $" (+{extraCount})" : string.Empty;

        var col2 = new StringBuilder();
        col2.AppendLine($"{participantsIcon} **{confirmedCount}**{extraText}");
        if (ev.EventTimeUtc.HasValue)
        {
            var onlineUnix = ev.EventTimeUtc.Value.ToUnixTimeSeconds();
            col2.AppendLine($"{clockIcon} <t:{onlineUnix}:t>");
        }

        var col3 = new StringBuilder();
        if (ev.EventTimeUtc.HasValue)
        {
            var onlineUnix = ev.EventTimeUtc.Value.ToUnixTimeSeconds();
            col3.AppendLine($"⏳ <t:{onlineUnix}:R>");
        }

        if (ev.CloseTimeUtc.HasValue)
        {
            var closeUnix = ev.CloseTimeUtc.Value.ToUnixTimeSeconds();
            col3.AppendLine($"{lockIcon} Đóng: <t:{closeUnix}:t>");
        }
        else
        {
            col3.AppendLine(isClosed ? $"{lockIcon} Đã đóng" : "🟢 Đang mở");
        }

        embed.AddField("\u200B", col1.ToString().TrimEnd(), inline: true);
        embed.AddField("\u200B", col2.ToString().TrimEnd(), inline: true);
        embed.AddField("\u200B", col3.ToString().TrimEnd(), inline: true);

        // --- HÀNG 2 (3 CỘT TANK / DPS / HEALER GIỐNG ẢNH 3) ---
        embed.AddField("\u200B", $"{tankIcon} Tank **{tankCount}**", inline: true);
        embed.AddField("\u200B", $"{dpsIcon} DPS **{dpsCount}**", inline: true);
        embed.AddField("\u200B", $"{healerIcon} Healer **{healerCount}**", inline: true);

        // --- HÀNG 3+: DANH SÁCH THEO TỪNG HỆ PHÁI (LUÔN CHIA ĐỀU 3 CỘT/HÀNG, QUÁ 3 MỚI XUỐNG HÀNG) ---
        var classGroups = ClassMetadata.All
            .Select(meta => (Meta: meta, Members: activeRoster.Where(s => s.GameClass == meta.Class).ToList()))
            .Where(x => x.Members.Count > 0)
            .ToList();

        var widthSpacer = new string('\u2800', 36);
        var statusFields = BuildStatusSummaryFields(lateList, benchList, absentList, notParticipatingList);

        if (classGroups.Count == 0)
        {
            embed.AddField(
                "\u200B",
                "_Chưa có thành viên nào đăng ký. Hãy chọn hệ phái ở menu **Chọn class** bên dưới!_",
                inline: false);
        }
        else
        {
            var baseLength = embed.Length + statusFields.Sum(f => f.Length + 1) + widthSpacer.Length;
            var classFieldValues = BuildAllClassFields(classGroups, forceCompact: false);

            // Bảo vệ giới hạn tổng 6000 ký tự toàn Embed của Discord khi bang đăng ký 80-120+ người:
            // nếu tổng ký tự sát trần 5800, tự chuyển toàn bộ các cột sang chế độ gọn (chỉ giữ icon ở tiêu đề hệ phái)
            var projectedLength = baseLength + classFieldValues.Sum(v => v.Length + 1) + 12;
            if (projectedLength > 5800)
            {
                classFieldValues = BuildAllClassFields(classGroups, forceCompact: true);
            }

            // Giữ tối đa 15 field cho khu vực hệ phái (6 header + 15 class + tối đa 4 status = <= 25 field giới hạn Discord)
            var maxClassFields = Math.Min(classFieldValues.Count, 15);
            for (var i = 0; i < maxClassFields; i++)
            {
                embed.AddField("\u200B", classFieldValues[i], inline: true);
            }

            // Bù cột trống cho đủ bội số của 3 để Discord luôn chia đúng 3 cột đều nhau
            var remainder = maxClassFields % 3;
            if (remainder != 0 && embed.Fields.Count + (3 - remainder) <= 24)
            {
                var paddingCount = 3 - remainder;
                for (var i = 0; i < paddingCount; i++)
                {
                    embed.AddField("\u200B", "\u200B", inline: true);
                }
            }
        }

        // --- PHẦN CUỐI FORM: LIỆT KÊ ĐI TRỄ / DỰ BỊ / VẮNG MẶT / KHÔNG THAM GIA ---
        for (var i = 0; i < statusFields.Count && embed.Fields.Count < 25; i++)
        {
            embed.AddField(i == 0 ? widthSpacer : "\u200B", statusFields[i], inline: false);
        }

        return embed.Build();
    }

    /// <summary>
    /// Dựng Select Menu chọn hệ phái và hàng nút Dự bị / Online trễ / Vắng / Cài đặt dùng icon custom.
    /// </summary>
    public MessageComponent BuildEventComponents(WarEvent ev)
    {
        var isClosed = ev.IsEffectivelyClosed(DateTimeOffset.UtcNow);

        var selectMenu = new SelectMenuBuilder()
            .WithCustomId($"evt:class:{ev.Id}")
            .WithPlaceholder(isClosed ? "Form đã đóng đăng ký" : "Chọn class")
            .WithMinValues(1)
            .WithMaxValues(1)
            .WithDisabled(isClosed);

        foreach (var meta in ClassMetadata.All)
        {
            selectMenu.AddOption(
                label: meta.DisplayName,
                value: ((int)meta.Class).ToString(),
                emote: iconSyncService.GetEmote(meta.Class));
        }

        return new ComponentBuilder()
            .WithSelectMenu(selectMenu, row: 0)
            .WithButton(
                label: "Dự bị",
                customId: $"evt:status:{ev.Id}:bench",
                style: ButtonStyle.Secondary,
                emote: iconSyncService.GetUiEmote(UiIcon.Bench),
                disabled: isClosed,
                row: 1)
            .WithButton(
                label: "Online trễ",
                customId: $"evt:status:{ev.Id}:late",
                style: ButtonStyle.Secondary,
                emote: iconSyncService.GetUiEmote(UiIcon.Clock),
                disabled: isClosed,
                row: 1)
            .WithButton(
                label: "Vắng",
                customId: $"evt:status:{ev.Id}:absent",
                style: ButtonStyle.Secondary,
                emote: iconSyncService.GetUiEmote(UiIcon.Absent),
                disabled: isClosed,
                row: 1)
            .WithButton(
                label: "Không tham gia",
                customId: $"evt:not-participating:{ev.Id}",
                style: ButtonStyle.Secondary,
                emote: iconSyncService.GetUiEmote(UiIcon.NotParticipating),
                disabled: isClosed,
                row: 1)
            .WithButton(
                customId: $"evt:manage:{ev.Id}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("⚙️"),
                row: 1)
            .Build();
    }

    /// <summary>
    /// Dựng thẻ xác nhận Ephemeral bằng Discord Components V2 khớp bố cục Raid-Helper.
    /// </summary>
    public MessageComponent BuildConfirmationComponentsV2(
        WarEvent ev,
        EventSignup signup,
        string headline)
    {
        var isClosed = ev.IsEffectivelyClosed(DateTimeOffset.UtcNow);
        var canEdit = !isClosed && signup.OptionsEnabled;
        var calendarIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Calendar);

        var jumpUrl = ev.MessageId > 0
            ? $"https://discord.com/channels/{ev.GuildId}/{ev.ChannelId}/{ev.MessageId}"
            : null;

        var titleLink = jumpUrl is not null
            ? $"[**{EscapeMarkdown(ev.Title)}**]({jumpUrl})"
            : $"**{EscapeMarkdown(ev.Title)}**";

        // Container 1: Header xác nhận
        var headerContainer = new ContainerBuilder()
            .WithAccentColor(AccentPurple)
            .AddComponent(new TextDisplayBuilder($"### ✅ {headline}"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder($"{calendarIcon} {titleLink}\n🏳️ **Leader:** <@{ev.LeaderDiscordId}>"));

        // Container 2: Sign-Up Options
        var optionsToggleButton = new ButtonBuilder()
            .WithCustomId($"evt:toggle-opt:{ev.Id}")
            .WithLabel(signup.OptionsEnabled ? "Enabled" : "Disabled")
            .WithEmote(signup.OptionsEnabled ? new Emoji("☑️") : iconSyncService.GetUiEmote(UiIcon.Shutdown))
            .WithStyle(ButtonStyle.Secondary)
            .WithDisabled(isClosed);

        var editEmote = iconSyncService.GetUiEmote(UiIcon.Edit);

        var editNameButton = new ButtonBuilder()
            .WithCustomId($"evt:edit-name:{ev.Id}")
            .WithEmote(editEmote)
            .WithStyle(ButtonStyle.Secondary)
            .WithDisabled(!canEdit);

        var editNoteButton = new ButtonBuilder()
            .WithCustomId($"evt:edit-note:{ev.Id}")
            .WithEmote(editEmote)
            .WithStyle(ButtonStyle.Secondary)
            .WithDisabled(!canEdit);

        var signedUpUnix = signup.SignedUpAtUtc.ToUnixTimeSeconds();
        var classText = signup.GameClass.HasValue
            ? $"{iconSyncService.GetEmojiMarkup(signup.GameClass.Value)} {ClassMetadata.Get(signup.GameClass.Value).DisplayName}"
            : "_Chưa chọn_";

        var classLine = signup.Status == SignupStatus.Confirmed
            ? $"**Class:**  {classText}"
            : $"**Class:**  {classText}\n**Status:** {iconSyncService.GetStatusLabel(signup.Status)}";

        var noteDisplay = string.IsNullOrWhiteSpace(signup.Note)
            ? "**Note:**"
            : $"**Note:** {EscapeMarkdown(signup.Note)}";

        var bottomRow = new ActionRowBuilder()
            .WithButton(
                customId: $"evt:info:{ev.Id}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("ℹ️"))
            .WithButton(
                label: "Remove Sign-Up",
                customId: $"evt:remove:{ev.Id}",
                style: ButtonStyle.Secondary,
                emote: iconSyncService.GetUiEmote(UiIcon.NotParticipating),
                disabled: isClosed);

        var optionsContainer = new ContainerBuilder()
            .WithAccentColor(AccentPurple)
            .AddComponent(new SectionBuilder()
                .AddComponent(new TextDisplayBuilder("### ⚙️ Sign-Up Options"))
                .WithAccessory(optionsToggleButton))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new SectionBuilder()
                .AddComponent(new TextDisplayBuilder(
                    $"**Name:**  {EscapeMarkdown(signup.DisplayName)}\n" +
                    $"**Time:**  <t:{signedUpUnix}:f>"))
                .WithAccessory(editNameButton))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder(classLine))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new SectionBuilder()
                .AddComponent(new TextDisplayBuilder(noteDisplay))
                .WithAccessory(editNoteButton))
            .AddComponent(bottomRow);

        return new ComponentBuilderV2()
            .AddComponent(headerContainer)
            .AddComponent(optionsContainer)
            .Build();
    }

    /// <summary>
    /// Dựng thẻ Components V2 sau khi người dùng bấm Remove Sign-Up hoặc Không tham gia.
    /// </summary>
    public MessageComponent BuildRemovedComponentsV2(WarEvent ev)
    {
        var calendarIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Calendar);
        var notParticipatingIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.NotParticipating);

        var titleLink = BuildEventTitleLink(ev);

        var container = new ContainerBuilder()
            .WithAccentColor(AccentPurple)
            .AddComponent(new TextDisplayBuilder($"### {notParticipatingIcon} Bạn đã hủy đăng ký khỏi sự kiện"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder(
                $"{calendarIcon} {titleLink}\n" +
                $"🏳️ **Leader:** <@{ev.LeaderDiscordId}>\n\n" +
                "Bạn có thể chọn lại hệ phái ở form chính bất cứ lúc nào trước khi đóng form."));

        return new ComponentBuilderV2()
            .AddComponent(container)
            .Build();
    }

    /// <summary>
    /// Dựng thẻ thông báo chung bằng Discord Components V2 (dùng đồng bộ cho mọi thông báo thành công / cảnh báo / lỗi).
    /// </summary>
    public MessageComponent BuildNoticeComponentsV2(
        string title,
        string body,
        NoticeKind kind = NoticeKind.Success,
        WarEvent? ev = null)
    {
        var (color, prefixIcon) = kind switch
        {
            NoticeKind.Success => (AccentPurple, "✅"),
            NoticeKind.Info => (AccentPurple, "ℹ️"),
            NoticeKind.Warning => (WarningAmber, "⚠️"),
            NoticeKind.Error => (ErrorRed, "❌"),
            _ => (AccentPurple, "ℹ️")
        };

        var container = new ContainerBuilder()
            .WithAccentColor(color)
            .AddComponent(new TextDisplayBuilder($"### {prefixIcon} {title}"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small));

        if (ev is not null)
        {
            var calendarIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Calendar);
            container.AddComponent(new TextDisplayBuilder(
                $"{calendarIcon} {BuildEventTitleLink(ev)}\n" +
                $"🏳️ **Leader:** <@{ev.LeaderDiscordId}>"));

            if (!string.IsNullOrWhiteSpace(body))
            {
                container.AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small));
                container.AddComponent(new TextDisplayBuilder(body));
            }
        }
        else if (!string.IsNullOrWhiteSpace(body))
        {
            container.AddComponent(new TextDisplayBuilder(body));
        }

        return new ComponentBuilderV2()
            .AddComponent(container)
            .Build();
    }

    /// <summary>
    /// Dựng thẻ quản lý sự kiện cho Leader/Admin theo đúng bố cục Components V2.
    /// </summary>
    public MessageComponent BuildManagePanelComponentsV2(WarEvent ev, string? statusHeadline = null)
    {
        var isClosed = ev.IsEffectivelyClosed(DateTimeOffset.UtcNow);
        var calendarIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Calendar);
        var clockIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Clock);
        var lockIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Lock);

        var headerTitle = string.IsNullOrWhiteSpace(statusHeadline)
            ? $"### ⚙️ Quản lý sự kiện #{ev.Id}"
            : $"### ✅ {statusHeadline}";

        var headerContainer = new ContainerBuilder()
            .WithAccentColor(isClosed ? ClosedGray : AccentPurple)
            .AddComponent(new TextDisplayBuilder(headerTitle))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder(
                $"{calendarIcon} {BuildEventTitleLink(ev)}\n" +
                $"🏳️ **Leader:** <@{ev.LeaderDiscordId}>"));

        var onlineText = ev.EventTimeUtc.HasValue
            ? $"<t:{ev.EventTimeUtc.Value.ToUnixTimeSeconds()}:f>"
            : "_Chưa đặt_";

        var closeText = ev.CloseTimeUtc.HasValue
            ? $"<t:{ev.CloseTimeUtc.Value.ToUnixTimeSeconds()}:f>"
            : "_Chưa đặt_";

        var stateText = isClosed ? $"{lockIcon} Đã đóng" : "🟢 Đang mở";

        var actionRow = new ActionRowBuilder()
            .WithButton(
                label: isClosed ? "Mở lại đăng ký" : "Đóng đăng ký",
                customId: $"evt:toggle-close:{ev.Id}",
                style: isClosed ? ButtonStyle.Success : ButtonStyle.Danger,
                emote: isClosed ? new Emoji("🔓") : iconSyncService.GetUiEmote(UiIcon.Lock))
            .WithButton(
                label: "Làm mới bảng",
                customId: $"evt:refresh:{ev.Id}",
                style: ButtonStyle.Secondary,
                emote: new Emoji("🔄"))
            .WithButton(
                label: "Tải Excel",
                customId: $"evt:export-excel:{ev.Id}",
                style: ButtonStyle.Secondary,
                emote: iconSyncService.GetUiEmote(UiIcon.Excel));

        var controlContainer = new ContainerBuilder()
            .WithAccentColor(AccentPurple)
            .AddComponent(new TextDisplayBuilder("### 📋 Thông tin & Điều khiển"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder(
                $"**Trạng thái:** {stateText}\n" +
                $"{clockIcon} **Giờ online:** {onlineText}\n" +
                $"{lockIcon} **Giờ đóng form:** {closeText}"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(actionRow);

        return new ComponentBuilderV2()
            .AddComponent(headerContainer)
            .AddComponent(controlContainer)
            .Build();
    }

    /// <summary>
    /// Dựng thẻ Components V2 kèm file Excel đính kèm để tải xuống trực tiếp.
    /// </summary>
    public MessageComponent BuildExcelExportComponentsV2(
        WarEvent ev,
        string fileName,
        int activeCount,
        int absentCount,
        int notParticipatingCount)
    {
        var excelIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Excel);
        var calendarIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Calendar);
        var participantsIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Participants);

        var container = new ContainerBuilder()
            .WithAccentColor(AccentPurple)
            .AddComponent(new TextDisplayBuilder($"### {excelIcon} Xuất file Excel thành công!"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder(
                $"{calendarIcon} {BuildEventTitleLink(ev)}\n" +
                $"🏳️ **Leader:** <@{ev.LeaderDiscordId}>\n" +
                $"{participantsIcon} **Danh sách tham gia:** `{activeCount}`  •  **Vắng:** `{absentCount}`  •  **Không tham gia:** `{notParticipatingCount}`"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new FileComponentBuilder()
                .WithFile(new UnfurledMediaItemProperties($"attachment://{fileName}")));

        return new ComponentBuilderV2()
            .AddComponent(container)
            .Build();
    }

    /// <summary>
    /// Dựng thẻ chi tiết thông tin đăng ký (khi bấm nút ℹ️) theo đúng bố cục Components V2.
    /// </summary>
    public MessageComponent BuildEventInfoComponentsV2(WarEvent ev, EventSignup? signup)
    {
        var calendarIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Calendar);
        var clockIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Clock);
        var lockIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Lock);
        var shutdownIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Shutdown);

        var onlineText = ev.EventTimeUtc.HasValue ? $"<t:{ev.EventTimeUtc.Value.ToUnixTimeSeconds()}:F>" : "_Chưa đặt_";
        var closeText = ev.CloseTimeUtc.HasValue ? $"<t:{ev.CloseTimeUtc.Value.ToUnixTimeSeconds()}:F>" : "_Chưa đặt_";

        var headerContainer = new ContainerBuilder()
            .WithAccentColor(AccentPurple)
            .AddComponent(new TextDisplayBuilder($"### ℹ️ Thông tin đăng ký — Event #{ev.Id}"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder(
                $"{calendarIcon} {BuildEventTitleLink(ev)}\n" +
                $"🏳️ **Leader:** <@{ev.LeaderDiscordId}>"));

        var detailsBuilder = new StringBuilder();
        detailsBuilder.AppendLine($"{clockIcon} **Giờ bắt đầu online:** {onlineText}");
        detailsBuilder.AppendLine($"{lockIcon} **Giờ đóng form:** {closeText}");

        if (signup is not null)
        {
            var optionStatus = signup.OptionsEnabled
                ? "☑️ Enabled (cho phép sửa tên/ghi chú)"
                : $"{shutdownIcon} Disabled (đang khóa chỉnh sửa)";
            detailsBuilder.AppendLine($"• **STT đăng ký:** `#{signup.SignupOrder}`");
            detailsBuilder.AppendLine($"• **Trạng thái:** {iconSyncService.GetStatusLabel(signup.Status)}");
            detailsBuilder.AppendLine($"• **Sign-Up Options:** {optionStatus}");
        }
        else
        {
            detailsBuilder.AppendLine("• Bạn chưa đăng ký sự kiện này.");
        }

        var detailContainer = new ContainerBuilder()
            .WithAccentColor(AccentPurple)
            .AddComponent(new TextDisplayBuilder("### 📋 Chi tiết"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder(detailsBuilder.ToString().TrimEnd()));

        return new ComponentBuilderV2()
            .AddComponent(headerContainer)
            .AddComponent(detailContainer)
            .Build();
    }

    /// <summary>
    /// Dựng thẻ Components V2 nhắc giờ online và tag tất cả thành viên đăng ký (trừ Vắng mặt và Không tham gia).
    /// </summary>
    public MessageComponent BuildOnlineReminderComponentsV2(
        WarEvent ev,
        IReadOnlyList<ulong> userIds,
        int totalTargetCount,
        int batchIndex = 0,
        int totalBatches = 1)
    {
        var calendarIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Calendar);
        var clockIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Clock);
        var participantsIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Participants);

        var mentionsLine = string.Join(" ", userIds.Select(id => $"<@{id}>"));

        if (batchIndex > 0)
        {
            var contContainer = new ContainerBuilder()
                .WithAccentColor(AccentPurple)
                .AddComponent(new TextDisplayBuilder($"### {clockIcon} Triệu tập online (tiếp {batchIndex + 1}/{totalBatches})"))
                .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
                .AddComponent(new TextDisplayBuilder(mentionsLine));

            return new ComponentBuilderV2()
                .AddComponent(contContainer)
                .Build();
        }

        var onlineUnix = (ev.EventTimeUtc ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();

        var container = new ContainerBuilder()
            .WithAccentColor(AccentPurple)
            .AddComponent(new TextDisplayBuilder($"### {clockIcon} Đã đến giờ online!"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder(
                $"{calendarIcon} {BuildEventTitleLink(ev)}\n" +
                $"🏳️ **Leader:** <@{ev.LeaderDiscordId}>\n" +
                $"{clockIcon} **Thời gian:** <t:{onlineUnix}:f>\n" +
                $"{participantsIcon} **Triệu tập:** `{totalTargetCount}` thành viên"))
            .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
            .AddComponent(new TextDisplayBuilder(mentionsLine));

        return new ComponentBuilderV2()
            .AddComponent(container)
            .Build();
    }

    private static string BuildEventTitleLink(WarEvent ev)
    {
        var jumpUrl = ev.MessageId > 0
            ? $"https://discord.com/channels/{ev.GuildId}/{ev.ChannelId}/{ev.MessageId}"
            : null;

        return jumpUrl is not null
            ? $"[**{EscapeMarkdown(ev.Title)}**]({jumpUrl})"
            : $"**{EscapeMarkdown(ev.Title)}**";
    }

    private List<string> BuildAllClassFields(
        List<(ClassInfo Meta, List<EventSignup> Members)> classGroups,
        bool forceCompact)
    {
        var result = new List<string>();
        foreach (var (meta, members) in classGroups)
        {
            var emoji = iconSyncService.GetEmojiMarkup(meta.Class);
            result.AddRange(BuildClassColumnValues(emoji, meta.DisplayName, members, forceCompact));
        }

        return result;
    }

    private List<string> BuildClassColumnValues(
        string emoji,
        string className,
        List<EventSignup> members,
        bool forceCompact = false)
    {
        // Bước 1: Thử hiển thị đầy đủ kèm icon hệ phái ở mỗi dòng (đẹp nhất khi <= ~16 người/phái)
        if (!forceCompact && TryBuildSingleColumn(emoji, className, members, includeRowClassIcon: true, out var fullWithIcons))
        {
            return [fullWithIcons];
        }

        // Bước 2: Khi vượt giới hạn 1024 ký tự của Discord Field (do mỗi custom emoji chiếm ~31 ký tự),
        // tự chuyển sang chế độ gọn (chỉ giữ icon ở tiêu đề hệ phái giống Raid-Helper) -> chứa được ~35-40 người trong 1 cột!
        if (TryBuildSingleColumn(emoji, className, members, includeRowClassIcon: false, out var compactSingle))
        {
            return [compactSingle];
        }

        // Bước 3: Nếu vẫn vượt 1024 ký tự (40+ người cùng 1 hệ phái), tự tách sang cột tiếp theo thay vì cắt mất tên
        var pages = new List<string>();
        var sb = new StringBuilder();
        var pageIndex = 0;

        string BuildHeader(int idx) => idx == 0
            ? $"{emoji} **__{className}__ ({members.Count})**"
            : $"{emoji} **__{className} (tiếp)__**";

        sb.AppendLine(BuildHeader(pageIndex));

        foreach (var member in members)
        {
            var line = FormatMemberLine(emoji, member, includeRowClassIcon: false);
            if (sb.Length + line.Length + 2 > 1024)
            {
                pages.Add(sb.ToString().TrimEnd());
                pageIndex++;
                sb.Clear();
                sb.AppendLine(BuildHeader(pageIndex));
            }

            sb.AppendLine(line);
        }

        if (sb.Length > 0)
        {
            pages.Add(sb.ToString().TrimEnd());
        }

        return pages;
    }

    private bool TryBuildSingleColumn(
        string emoji,
        string className,
        List<EventSignup> members,
        bool includeRowClassIcon,
        out string result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{emoji} **__{className}__ ({members.Count})**");

        foreach (var member in members)
        {
            var line = FormatMemberLine(emoji, member, includeRowClassIcon);
            if (sb.Length + line.Length + 2 > 1024)
            {
                result = string.Empty;
                return false;
            }

            sb.AppendLine(line);
        }

        result = sb.ToString().TrimEnd();
        return result.Length <= 1024;
    }

    private string FormatMemberLine(string classEmoji, EventSignup member, bool includeRowClassIcon)
    {
        var statusMarker = member.Status switch
        {
            SignupStatus.Late => $" {iconSyncService.GetUiEmojiMarkup(UiIcon.Clock)}",
            SignupStatus.Bench => $" {iconSyncService.GetUiEmojiMarkup(UiIcon.Bench)}",
            _ => string.Empty
        };
        var noteMarker = string.IsNullOrWhiteSpace(member.Note) ? string.Empty : " 📝";
        var prefix = includeRowClassIcon ? $"{classEmoji} " : string.Empty;

        return $"{prefix}`{member.SignupOrder}` **{EscapeMarkdown(member.DisplayName)}**{statusMarker}{noteMarker}";
    }

    private List<string> BuildStatusSummaryFields(
        List<EventSignup> lateList,
        List<EventSignup> benchList,
        List<EventSignup> absentList,
        List<EventSignup> notParticipatingList)
    {
        var clockIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Clock);
        var benchIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Bench);
        var absentIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.Absent);
        var notParticipatingIcon = iconSyncService.GetUiEmojiMarkup(UiIcon.NotParticipating);

        var lines = new List<string>
        {
            FormatStatusGroupLine(clockIcon, "Đi trễ", lateList, includeClassIcon: true),
            FormatStatusGroupLine(benchIcon, "Dự bị", benchList, includeClassIcon: true),
            FormatStatusGroupLine(absentIcon, "Vắng mặt", absentList, includeClassIcon: false),
            FormatStatusGroupLine(notParticipatingIcon, "Không tham gia", notParticipatingList, includeClassIcon: false)
        };

        // Gom các dòng lại thành ít field nhất có thể mà không vượt quá 1024 ký tự/field
        var fields = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            var safeLine = line.Length <= 1024 ? line : line[..1020] + "...";
            if (current.Length > 0 && current.Length + safeLine.Length + 1 > 1024)
            {
                fields.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            current.AppendLine(safeLine);
        }

        if (current.Length > 0)
        {
            fields.Add(current.ToString().TrimEnd());
        }

        return fields;
    }

    private string FormatStatusGroupLine(string groupIcon, string label, List<EventSignup> list, bool includeClassIcon)
    {
        if (list.Count == 0)
        {
            return $"{groupIcon} **{label} (0):** _Không có_";
        }

        var entries = list.Select(s =>
        {
            var emoji = includeClassIcon && s.GameClass.HasValue
                ? $"{iconSyncService.GetEmojiMarkup(s.GameClass.Value)} "
                : string.Empty;
            return $"{emoji}`{s.SignupOrder}` **{EscapeMarkdown(s.DisplayName)}**";
        });

        var full = $"{groupIcon} **{label} ({list.Count}):** {string.Join(", ", entries)}";
        if (full.Length <= 1024 || !includeClassIcon)
        {
            return full;
        }

        // Nếu nhóm Đi trễ / Dự bị quá đông làm vượt 1024 ký tự -> bỏ icon class từng người để hiện được nhiều tên hơn
        var compactEntries = list.Select(s => $"`{s.SignupOrder}` **{EscapeMarkdown(s.DisplayName)}**");
        return $"{groupIcon} **{label} ({list.Count}):** {string.Join(", ", compactEntries)}";
    }

    private static string EscapeMarkdown(string input)
    {
        return input
            .Replace("\\", "\\\\")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("~", "\\~")
            .Replace("`", "\\`");
    }
}

