using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using GuildHub.Core.Domain.Entities;
using GuildHub.Bot.Services;
using Microsoft.Extensions.Logging;

namespace GuildHub.Bot.Modules;

public sealed class EventSlashModule(
    EventService eventService,
    EventMessageBuilder messageBuilder,
    IconSyncService iconSyncService,
    DiscordSocketClient client,
    ILogger<EventSlashModule> logger) : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    [SlashCommand("tao-form", "Tạo form đăng ký sự kiện (tiêu đề, giờ online, giờ đóng form, role tag)")]
    [RequireContext(ContextType.Guild)]
    public async Task CreateEventFormAsync(
        [Summary("tieu_de", "Tiêu đề sự kiện (VD: SCRIM THỨ 5, BANG CHIẾN TUẦN 12)")] string title,
        [Summary("thoi_gian_online", "Thời gian bắt đầu online giờ VN (VD: 26/11/2026 20:15 hoặc 26/11 20:15 hoặc 20:15)")] string onlineTime,
        [Summary("thoi_gian_dong", "Thời gian đóng form giờ VN (VD: 26/11/2026 19:45 hoặc 26/11 19:45 hoặc 19:45)")] string closeTime,
        [Summary("role", "Role sẽ được tag thông báo khi tạo form")] IRole role,
        [Summary("noi_dung", "Ghi chú hiển thị trên đầu form, VD: Tập trung lúc 19:45 (tùy chọn)")] string? description = null,
        [Summary("kenh", "Kênh đăng form (mặc định là kênh hiện tại)")] ITextChannel? channel = null)
    {
        logger.LogInformation(
            "User {Username} ({UserId}) chạy /tao-form tại Guild {GuildId} | Tiêu đề: {Title} | Online: {Online} | Đóng: {Close} | Role: {RoleName} ({RoleId})",
            Context.User.Username, Context.User.Id, Context.Guild.Id, title, onlineTime, closeTime, role.Name, role.Id);

        if (!TryParseVietnamDateTime(onlineTime, out var onlineTimeOffset))
        {
            await RespondNoticeAsync(
                "Định dạng thời gian chưa hợp lệ",
                "Tham số `thoi_gian_online` chưa đúng. Hỗ trợ: `dd/MM/yyyy HH:mm` (VD: `26/11/2026 20:15`), `dd/MM HH:mm` (VD: `26/11 20:15`), hoặc `HH:mm` (VD: `20:15`).",
                NoticeKind.Error);
            return;
        }

        if (!TryParseVietnamDateTime(closeTime, out var closeTimeOffset))
        {
            await RespondNoticeAsync(
                "Định dạng thời gian chưa hợp lệ",
                "Tham số `thoi_gian_dong` chưa đúng. Hỗ trợ: `dd/MM/yyyy HH:mm` (VD: `26/11/2026 19:45`), `dd/MM HH:mm` (VD: `26/11 19:45`), hoặc `HH:mm` (VD: `19:45`).",
                NoticeKind.Error);
            return;
        }

        IMessageChannel? targetChannel = (IMessageChannel?)channel ?? Context.Channel;
        if (targetChannel is null)
        {
            await RespondNoticeAsync("Không thể tạo form", "Không thể gửi form vào kênh này.", NoticeKind.Error);
            return;
        }

        await DeferAsync(ephemeral: true);

        var warEvent = await eventService.CreateEventAsync(
            guildId: Context.Guild.Id,
            channelId: targetChannel.Id,
            leaderDiscordId: Context.User.Id,
            title: title,
            onlineTimeUtc: onlineTimeOffset.ToUniversalTime(),
            closeTimeUtc: closeTimeOffset.ToUniversalTime(),
            mentionRoleId: role.Id,
            description: description);

        var embed = messageBuilder.BuildEventEmbed(warEvent);
        var components = messageBuilder.BuildEventComponents(warEvent);

        var isEveryone = role.Id == Context.Guild.EveryoneRole.Id;
        var mentionText = isEveryone ? "@everyone" : role.Mention;
        var allowedMentions = isEveryone
            ? new AllowedMentions(AllowedMentionTypes.Everyone)
            : new AllowedMentions { RoleIds = [role.Id] };

        try
        {
            var sentMessage = await targetChannel.SendMessageAsync(
                text: mentionText,
                embed: embed,
                components: components,
                allowedMentions: allowedMentions);

            await eventService.AttachMessageIdAsync(warEvent.Id, sentMessage.Id);
            warEvent.MessageId = sentMessage.Id;

            logger.LogInformation(
                "Đã tạo sự kiện #{EventId} ('{Title}') và gửi message {MessageId} vào kênh {ChannelId}.",
                warEvent.Id, warEvent.Title, sentMessage.Id, targetChannel.Id);

            await FollowupNoticeAsync(
                "Đã tạo form đăng ký thành công!",
                $"• **Kênh:** <#{targetChannel.Id}>\n" +
                $"• **Event ID:** `#{warEvent.Id}`\n" +
                $"• **Giờ online:** <t:{onlineTimeOffset.ToUnixTimeSeconds()}:f>\n" +
                $"• **Giờ đóng form:** <t:{closeTimeOffset.ToUnixTimeSeconds()}:f>",
                NoticeKind.Success,
                warEvent);
        }
        catch (HttpException httpEx)
        {
            logger.LogError(
                httpEx,
                "Lỗi Discord API khi gửi form sự kiện #{EventId} vào kênh {ChannelId} (DiscordCode: {Code}).",
                warEvent.Id, targetChannel.Id, httpEx.DiscordCode);

            await FollowupNoticeAsync(
                "Không thể gửi form vào kênh",
                $"Bot không thể gửi tin nhắn vào kênh <#{targetChannel.Id}> (Mã lỗi: `{httpEx.DiscordCode ?? (DiscordErrorCode)httpEx.HttpCode}`).\n" +
                "Vui lòng kiểm tra bot đã có đủ quyền **View Channel**, **Send Messages**, **Embed Links**, **Mention Roles** và **Use External Emojis**.",
                NoticeKind.Error);
        }
    }

    [SlashCommand("dong-form", "Đóng form đăng ký sự kiện theo ID")]
    [RequireContext(ContextType.Guild)]
    public async Task CloseFormAsync(
        [Summary("event_id", "ID của sự kiện (xem ở dòng footer của form)")] long eventId)
    {
        await ToggleCloseInternalAsync(eventId, isClosed: true);
    }

    [SlashCommand("mo-form", "Mở lại form đăng ký sự kiện theo ID")]
    [RequireContext(ContextType.Guild)]
    public async Task ReopenFormAsync(
        [Summary("event_id", "ID của sự kiện (xem ở dòng footer của form)")] long eventId)
    {
        await ToggleCloseInternalAsync(eventId, isClosed: false);
    }

    [SlashCommand("sync-icon", "Đồng bộ lại các file PNG trong thư mục Icon thành Emoji của Bot")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task SyncIconsAsync()
    {
        logger.LogInformation("User {Username} ({UserId}) chạy /sync-icon.", Context.User.Username, Context.User.Id);
        await DeferAsync(ephemeral: true);

        var app = await client.GetApplicationInfoAsync();
        var result = await iconSyncService.SyncApplicationEmojisAsync(app.Id);

        await FollowupNoticeAsync(
            "Đã đồng bộ Icon thành công!",
            $"• **Thư mục:** `{result.ResolvedDirectory}`\n" +
            $"• **Upload mới:** `{result.Uploaded}`\n" +
            $"• **Cập nhật:** `{result.Updated}`\n" +
            $"• **Không đổi:** `{result.Unchanged}`\n" +
            $"• **Thiếu file:** `{result.MissingFiles}`",
            NoticeKind.Success);
    }

    private async Task ToggleCloseInternalAsync(long eventId, bool isClosed)
    {
        await DeferAsync(ephemeral: true);

        var existing = await eventService.GetEventWithSignupsAsync(eventId);
        if (existing is null || existing.GuildId != Context.Guild.Id)
        {
            await FollowupNoticeAsync("Không tìm thấy sự kiện", $"Không tìm thấy sự kiện `#{eventId}` trong server này.", NoticeKind.Error);
            return;
        }

        var guildUser = Context.User as SocketGuildUser;
        var canManage = existing.LeaderDiscordId == Context.User.Id ||
                        (guildUser?.GuildPermissions.ManageGuild ?? false) ||
                        (guildUser?.GuildPermissions.Administrator ?? false);

        if (!canManage)
        {
            await FollowupNoticeAsync("Không đủ quyền hạn", "Chỉ Leader của sự kiện hoặc quản trị viên mới có thể thao tác.", NoticeKind.Warning);
            return;
        }

        var result = await eventService.SetClosedAsync(eventId, isClosed);
        if (!result.Success || result.Event is null)
        {
            await FollowupNoticeAsync("Thao tác thất bại", result.ErrorMessage ?? "Đã có lỗi xảy ra.", NoticeKind.Error);
            return;
        }

        await RefreshEventMessageAsync(result.Event);
        logger.LogInformation(
            "User {Username} ({UserId}) đã {Action} sự kiện #{EventId}.",
            Context.User.Username, Context.User.Id, isClosed ? "đóng" : "mở lại", eventId);

        await FollowupNoticeAsync(
            result.Headline ?? (isClosed ? "Đã đóng form đăng ký." : "Đã mở lại form đăng ký."),
            isClosed ? "Thành viên sẽ không thể đăng ký hoặc đổi trạng thái trên form này nữa." : "Thành viên đã có thể tiếp tục đăng ký trên form.",
            NoticeKind.Success,
            result.Event);
    }

    private Task RespondNoticeAsync(string title, string body, NoticeKind kind, WarEvent? ev = null)
    {
        var card = messageBuilder.BuildNoticeComponentsV2(title, body, kind, ev);
        return RespondAsync(components: card, ephemeral: true, flags: MessageFlags.ComponentsV2);
    }

    private Task FollowupNoticeAsync(string title, string body, NoticeKind kind, WarEvent? ev = null)
    {
        var card = messageBuilder.BuildNoticeComponentsV2(title, body, kind, ev);
        return FollowupAsync(components: card, ephemeral: true, flags: MessageFlags.ComponentsV2);
    }

    private async Task RefreshEventMessageAsync(WarEvent ev)
    {
        if (client.GetChannel(ev.ChannelId) is not IMessageChannel channel || ev.MessageId == 0)
        {
            return;
        }

        await channel.ModifyMessageAsync(ev.MessageId, props =>
        {
            props.Embed = messageBuilder.BuildEventEmbed(ev);
            props.Components = messageBuilder.BuildEventComponents(ev);
        });
    }

    private static bool TryParseVietnamDateTime(string input, out DateTimeOffset result)
    {
        var trimmed = input.Trim();
        var nowVn = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);

        string[] fullFormats =
        [
            "dd/MM/yyyy HH:mm",
            "d/M/yyyy HH:mm",
            "dd-MM-yyyy HH:mm",
            "d-M-yyyy HH:mm",
            "yyyy-MM-dd HH:mm"
        ];

        if (DateTime.TryParseExact(trimmed, fullFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fullParsed))
        {
            result = new DateTimeOffset(fullParsed, VietnamOffset);
            return true;
        }

        string[] dayMonthFormats =
        [
            "dd/MM HH:mm",
            "d/M HH:mm",
            "dd-MM HH:mm",
            "d-M HH:mm"
        ];

        if (DateTime.TryParseExact(trimmed, dayMonthFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dmParsed))
        {
            var dt = new DateTime(nowVn.Year, dmParsed.Month, dmParsed.Day, dmParsed.Hour, dmParsed.Minute, 0);
            result = new DateTimeOffset(dt, VietnamOffset);
            return true;
        }

        string[] timeOnlyFormats = ["HH:mm", "H:mm"];
        if (DateTime.TryParseExact(trimmed, timeOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeParsed))
        {
            var dt = new DateTime(nowVn.Year, nowVn.Month, nowVn.Day, timeParsed.Hour, timeParsed.Minute, 0);
            result = new DateTimeOffset(dt, VietnamOffset);
            return true;
        }

        result = default;
        return false;
    }
}

