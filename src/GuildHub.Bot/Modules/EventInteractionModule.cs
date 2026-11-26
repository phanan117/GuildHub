using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;
using GuildHub.Bot.Services;
using Microsoft.Extensions.Logging;

namespace GuildHub.Bot.Modules;

public sealed class EventInteractionModule(
    EventService eventService,
    EventMessageBuilder messageBuilder,
    EventExcelExporter excelExporter,
    MemberRealtimeSyncService memberRealtimeSync,
    DiscordSocketClient client,
    ILogger<EventInteractionModule> logger) : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("evt:class:*", ignoreGroupNames: true)]
    public async Task HandleClassSelectedAsync(string eventIdRaw, string[] selectedValues)
    {
        if (!long.TryParse(eventIdRaw, out var eventId) ||
            selectedValues.Length == 0 ||
            !int.TryParse(selectedValues[0], out var classInt) ||
            !Enum.IsDefined(typeof(GameClass), classInt))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "Dữ liệu hệ phái không hợp lệ.", NoticeKind.Error);
            return;
        }

        await DeferAsync(ephemeral: true);

        var displayName = ResolveMemberDisplayName();
        var result = await eventService.SignupWithClassAsync(
            eventId,
            Context.User.Id,
            displayName,
            (GameClass)classInt);

        if (!result.Success || result.Event is null || result.Signup is null)
        {
            await FollowupNoticeAsync("Không thể đăng ký", result.ErrorMessage ?? "Đã có lỗi xảy ra.", NoticeKind.Warning);
            return;
        }

        await RefreshEventMessageAsync(result.Event);
        await memberRealtimeSync.SyncClassFromBotSignupAsync(
            result.Event.GuildId,
            Context.User.Id,
            result.Signup.DisplayName,
            (GameClass)classInt);

        logger.LogInformation(
            "Event #{EventId}: {DisplayName} ({UserId}) chọn hệ phái {GameClass} (STT #{Order}).",
            eventId, result.Signup.DisplayName, Context.User.Id, result.Signup.GameClass, result.Signup.SignupOrder);

        var componentsV2 = messageBuilder.BuildConfirmationComponentsV2(
            result.Event,
            result.Signup,
            result.Headline ?? "Bạn đã đăng ký sự kiện thành công!");

        await FollowupAsync(components: componentsV2, ephemeral: true, flags: MessageFlags.ComponentsV2);
    }

    [ComponentInteraction("evt:status:*:*", ignoreGroupNames: true)]
    public async Task HandleStatusButtonAsync(string eventIdRaw, string statusKey)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var targetStatus = statusKey.ToLowerInvariant() switch
        {
            "late" => SignupStatus.Late,
            "bench" => SignupStatus.Bench,
            "absent" => SignupStatus.Absent,
            _ => (SignupStatus?)null
        };

        if (!targetStatus.HasValue)
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "Trạng thái không hợp lệ.", NoticeKind.Error);
            return;
        }

        await DeferAsync(ephemeral: true);

        var result = await eventService.SetStatusAsync(eventId, Context.User.Id, targetStatus.Value);
        if (!result.Success || result.Event is null || result.Signup is null)
        {
            await FollowupNoticeAsync("Không thể đổi trạng thái", result.ErrorMessage ?? "Đã có lỗi xảy ra.", NoticeKind.Warning);
            return;
        }

        await RefreshEventMessageAsync(result.Event);

        logger.LogInformation(
            "Event #{EventId}: {DisplayName} ({UserId}) đổi trạng thái thành {Status}.",
            eventId, result.Signup.DisplayName, Context.User.Id, result.Signup.Status);

        var componentsV2 = messageBuilder.BuildConfirmationComponentsV2(
            result.Event,
            result.Signup,
            result.Headline ?? "Đã cập nhật trạng thái!");

        await FollowupAsync(components: componentsV2, ephemeral: true, flags: MessageFlags.ComponentsV2);
    }

    [ComponentInteraction("evt:toggle-opt:*", ignoreGroupNames: true)]
    public async Task HandleToggleOptionsAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var result = await eventService.ToggleOptionsEnabledAsync(eventId, Context.User.Id);
        if (!result.Success || result.Event is null || result.Signup is null)
        {
            await RespondNoticeAsync("Không thể cập nhật", result.ErrorMessage ?? "Đã có lỗi xảy ra.", NoticeKind.Warning);
            return;
        }

        logger.LogInformation(
            "Event #{EventId}: User {UserId} chuyển Sign-Up Options sang {State}.",
            eventId, Context.User.Id, result.Signup.OptionsEnabled ? "Enabled" : "Disabled");

        var updatedCard = messageBuilder.BuildConfirmationComponentsV2(
            result.Event,
            result.Signup,
            result.Headline ?? "Đã cập nhật tùy chọn đăng ký!");

        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.UpdateAsync(props => props.Components = updatedCard);
        }
        else
        {
            await RespondAsync(components: updatedCard, ephemeral: true, flags: MessageFlags.ComponentsV2);
        }
    }

    [ComponentInteraction("evt:info:*", ignoreGroupNames: true)]
    public async Task HandleInfoButtonAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var ev = await eventService.GetEventWithSignupsAsync(eventId);
        if (ev is null)
        {
            await RespondNoticeAsync("Không tìm thấy sự kiện", "Sự kiện này không tồn tại hoặc đã bị xóa.", NoticeKind.Error);
            return;
        }

        var signup = ev.Signups.FirstOrDefault(s => s.DiscordUserId == Context.User.Id);
        var infoCard = messageBuilder.BuildEventInfoComponentsV2(ev, signup);
        await RespondAsync(components: infoCard, ephemeral: true, flags: MessageFlags.ComponentsV2);
    }

    [ComponentInteraction("evt:edit-name:*", ignoreGroupNames: true)]
    public async Task HandleEditNameButtonAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var ev = await eventService.GetEventWithSignupsAsync(eventId);
        var signup = ev?.Signups.FirstOrDefault(s => s.DiscordUserId == Context.User.Id);
        if (signup is null)
        {
            await RespondNoticeAsync("Chưa đăng ký", "Bạn chưa đăng ký sự kiện này.", NoticeKind.Warning);
            return;
        }

        if (!signup.OptionsEnabled)
        {
            await RespondNoticeAsync("Chỉnh sửa đang khóa", "Vui lòng bấm nút **Disabled → Enabled** để mở khóa chỉnh sửa.", NoticeKind.Warning);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle("Đổi tên hiển thị trong form")
            .WithCustomId($"evt:modal-name:{eventId}")
            .AddTextInput(
                label: "Tên nhân vật / tên hiển thị",
                customId: "display_name",
                style: TextInputStyle.Short,
                placeholder: "VD: BinTV",
                minLength: 1,
                maxLength: 100,
                required: true,
                value: signup.DisplayName)
            .Build();

        await RespondWithModalAsync(modal);
    }

    [ModalInteraction("evt:modal-name:*", ignoreGroupNames: true)]
    public async Task HandleEditNameModalAsync(string eventIdRaw, EditDisplayNameModal modal)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var result = await eventService.UpdateDisplayNameAsync(eventId, Context.User.Id, modal.DisplayName);
        if (!result.Success || result.Event is null || result.Signup is null)
        {
            await RespondNoticeAsync("Không thể đổi tên", result.ErrorMessage ?? "Đã có lỗi xảy ra.", NoticeKind.Warning);
            return;
        }

        await RefreshEventMessageAsync(result.Event);

        logger.LogInformation(
            "Event #{EventId}: User {UserId} đổi tên hiển thị thành '{DisplayName}'.",
            eventId, Context.User.Id, result.Signup.DisplayName);

        var updatedCard = messageBuilder.BuildConfirmationComponentsV2(
            result.Event,
            result.Signup,
            result.Headline ?? "Đã cập nhật tên hiển thị!");

        if (Context.Interaction is SocketModal socketModal)
        {
            await socketModal.UpdateAsync(props => props.Components = updatedCard);
        }
        else
        {
            await RespondAsync(components: updatedCard, ephemeral: true, flags: MessageFlags.ComponentsV2);
        }
    }

    [ComponentInteraction("evt:edit-note:*", ignoreGroupNames: true)]
    public async Task HandleEditNoteButtonAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var ev = await eventService.GetEventWithSignupsAsync(eventId);
        var signup = ev?.Signups.FirstOrDefault(s => s.DiscordUserId == Context.User.Id);
        if (signup is null)
        {
            await RespondNoticeAsync("Chưa đăng ký", "Bạn chưa đăng ký sự kiện này.", NoticeKind.Warning);
            return;
        }

        if (!signup.OptionsEnabled)
        {
            await RespondNoticeAsync("Chỉnh sửa đang khóa", "Vui lòng bấm nút **Disabled → Enabled** để mở khóa chỉnh sửa ghi chú.", NoticeKind.Warning);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle("Ghi chú đăng ký")
            .WithCustomId($"evt:modal-note:{eventId}")
            .AddTextInput(
                label: "Ghi chú (để trống để xóa)",
                customId: "signup_note",
                style: TextInputStyle.Paragraph,
                placeholder: "VD: Có thể lên trễ 10 phút, đánh được cả Tố Vấn...",
                maxLength: 300,
                required: false,
                value: signup.Note ?? string.Empty)
            .Build();

        await RespondWithModalAsync(modal);
    }

    [ModalInteraction("evt:modal-note:*", ignoreGroupNames: true)]
    public async Task HandleEditNoteModalAsync(string eventIdRaw, EditNoteModal modal)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var result = await eventService.UpdateNoteAsync(eventId, Context.User.Id, modal.Note);
        if (!result.Success || result.Event is null || result.Signup is null)
        {
            await RespondNoticeAsync("Không thể sửa ghi chú", result.ErrorMessage ?? "Đã có lỗi xảy ra.", NoticeKind.Warning);
            return;
        }

        await RefreshEventMessageAsync(result.Event);

        logger.LogInformation(
            "Event #{EventId}: {DisplayName} ({UserId}) cập nhật ghi chú.",
            eventId, result.Signup.DisplayName, Context.User.Id);

        var updatedCard = messageBuilder.BuildConfirmationComponentsV2(
            result.Event,
            result.Signup,
            result.Headline ?? "Đã cập nhật ghi chú!");

        if (Context.Interaction is SocketModal socketModal)
        {
            await socketModal.UpdateAsync(props => props.Components = updatedCard);
        }
        else
        {
            await RespondAsync(components: updatedCard, ephemeral: true, flags: MessageFlags.ComponentsV2);
        }
    }

    [ComponentInteraction("evt:remove:*", ignoreGroupNames: true)]
    public async Task HandleRemoveSignupAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var result = await eventService.RemoveSignupAsync(eventId, Context.User.Id);
        if (!result.Success || result.Event is null)
        {
            await RespondNoticeAsync("Không thể hủy đăng ký", result.ErrorMessage ?? "Đã có lỗi xảy ra.", NoticeKind.Warning);
            return;
        }

        await RefreshEventMessageAsync(result.Event);
        logger.LogInformation("Event #{EventId}: User {Username} ({UserId}) đã hủy đăng ký.", eventId, Context.User.Username, Context.User.Id);

        var removedCard = messageBuilder.BuildRemovedComponentsV2(result.Event);
        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.UpdateAsync(props => props.Components = removedCard);
        }
        else
        {
            await RespondAsync(components: removedCard, ephemeral: true, flags: MessageFlags.ComponentsV2);
        }
    }

    [ComponentInteraction("evt:not-participating:*", ignoreGroupNames: true)]
    public async Task HandleNotParticipatingAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        await DeferAsync(ephemeral: true);

        var displayName = ResolveMemberDisplayName();
        var result = await eventService.MarkNotParticipatingAsync(eventId, Context.User.Id, displayName);
        if (!result.Success || result.Event is null || result.Signup is null)
        {
            await FollowupNoticeAsync("Không thể cập nhật", result.ErrorMessage ?? "Đã có lỗi xảy ra.", NoticeKind.Warning);
            return;
        }

        await RefreshEventMessageAsync(result.Event);
        logger.LogInformation("Event #{EventId}: {DisplayName} ({UserId}) đổi trạng thái thành {Status}.",
            eventId, result.Signup.DisplayName, Context.User.Id, result.Signup.Status);

        var componentsV2 = messageBuilder.BuildConfirmationComponentsV2(
            result.Event,
            result.Signup,
            result.Headline ?? "Đã ghi nhận trạng thái: Không tham gia!");

        await FollowupAsync(components: componentsV2, ephemeral: true, flags: MessageFlags.ComponentsV2);
    }

    [ComponentInteraction("evt:manage:*", ignoreGroupNames: true)]
    public async Task HandleManageButtonAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var ev = await eventService.GetEventWithSignupsAsync(eventId);
        if (ev is null)
        {
            await RespondNoticeAsync("Không tìm thấy sự kiện", "Sự kiện này không tồn tại hoặc đã bị xóa.", NoticeKind.Error);
            return;
        }

        if (!CanManageEvent(ev))
        {
            await RespondNoticeAsync("Không đủ quyền hạn", "Chỉ Leader của sự kiện hoặc quản trị viên mới có quyền quản lý form.", NoticeKind.Warning);
            return;
        }

        var manageCard = messageBuilder.BuildManagePanelComponentsV2(ev);
        await RespondAsync(components: manageCard, ephemeral: true, flags: MessageFlags.ComponentsV2);
    }

    [ComponentInteraction("evt:toggle-close:*", ignoreGroupNames: true)]
    public async Task HandleToggleCloseAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var ev = await eventService.GetEventWithSignupsAsync(eventId);
        if (ev is null)
        {
            await RespondNoticeAsync("Không tìm thấy sự kiện", "Sự kiện này không tồn tại hoặc đã bị xóa.", NoticeKind.Error);
            return;
        }

        if (!CanManageEvent(ev))
        {
            await RespondNoticeAsync("Không đủ quyền hạn", "Bạn không có quyền thực hiện thao tác này.", NoticeKind.Warning);
            return;
        }

        var isClosed = ev.IsEffectivelyClosed(DateTimeOffset.UtcNow);
        var result = await eventService.SetClosedAsync(eventId, !isClosed);
        if (!result.Success || result.Event is null)
        {
            await RespondNoticeAsync("Thao tác thất bại", result.ErrorMessage ?? "Đã có lỗi xảy ra.", NoticeKind.Error);
            return;
        }

        await RefreshEventMessageAsync(result.Event);

        var updatedPanel = messageBuilder.BuildManagePanelComponentsV2(result.Event, result.Headline);
        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.UpdateAsync(props => props.Components = updatedPanel);
        }
        else
        {
            await RespondAsync(components: updatedPanel, ephemeral: true, flags: MessageFlags.ComponentsV2);
        }
    }

    [ComponentInteraction("evt:refresh:*", ignoreGroupNames: true)]
    public async Task HandleRefreshAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        var ev = await eventService.GetEventWithSignupsAsync(eventId);
        if (ev is null)
        {
            await RespondNoticeAsync("Không tìm thấy sự kiện", "Sự kiện này không tồn tại hoặc đã bị xóa.", NoticeKind.Error);
            return;
        }

        await RefreshEventMessageAsync(ev);

        var updatedPanel = messageBuilder.BuildManagePanelComponentsV2(ev, "Đã làm mới form đăng ký!");
        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.UpdateAsync(props => props.Components = updatedPanel);
        }
        else
        {
            await RespondAsync(components: updatedPanel, ephemeral: true, flags: MessageFlags.ComponentsV2);
        }
    }

    [ComponentInteraction("evt:export-excel:*", ignoreGroupNames: true)]
    public async Task HandleExportExcelAsync(string eventIdRaw)
    {
        if (!long.TryParse(eventIdRaw, out var eventId))
        {
            await RespondNoticeAsync("Dữ liệu không hợp lệ", "ID sự kiện không hợp lệ.", NoticeKind.Error);
            return;
        }

        await DeferAsync(ephemeral: true);

        var ev = await eventService.GetEventWithSignupsAsync(eventId);
        if (ev is null)
        {
            await FollowupNoticeAsync("Không tìm thấy sự kiện", "Sự kiện này không tồn tại hoặc đã bị xóa.", NoticeKind.Error);
            return;
        }

        if (!CanManageEvent(ev))
        {
            await FollowupNoticeAsync("Không đủ quyền hạn", "Chỉ Leader của sự kiện hoặc quản trị viên mới có quyền xuất Excel.", NoticeKind.Warning);
            return;
        }

        var export = excelExporter.Export(ev);
        using var stream = new MemoryStream(export.Content);

        var exportCard = messageBuilder.BuildExcelExportComponentsV2(
            ev,
            export.FileName,
            export.ActiveCount,
            export.AbsentCount,
            export.NotParticipatingCount);

        await FollowupWithFileAsync(
            fileStream: stream,
            fileName: export.FileName,
            components: exportCard,
            ephemeral: true,
            flags: MessageFlags.ComponentsV2);

        logger.LogInformation(
            "Event #{EventId}: User {Username} ({UserId}) đã xuất file Excel '{FileName}' ({Active} tham gia, {Absent} vắng, {NotPart} không tham gia).",
            ev.Id, Context.User.Username, Context.User.Id, export.FileName,
            export.ActiveCount, export.AbsentCount, export.NotParticipatingCount);
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

    private string ResolveMemberDisplayName()
    {
        if (Context.User is SocketGuildUser guildUser)
        {
            return guildUser.DisplayName;
        }

        return Context.User.GlobalName ?? Context.User.Username;
    }

    private bool CanManageEvent(WarEvent ev)
    {
        if (ev.LeaderDiscordId == Context.User.Id)
        {
            return true;
        }

        if (Context.User is SocketGuildUser guildUser)
        {
            return guildUser.GuildPermissions.Administrator || guildUser.GuildPermissions.ManageGuild;
        }

        return false;
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
}

public sealed class EditDisplayNameModal : IModal
{
    public string Title => "Đổi tên hiển thị trong form";

    [InputLabel("Tên nhân vật / tên hiển thị")]
    [ModalTextInput("display_name", TextInputStyle.Short, maxLength: 100)]
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class EditNoteModal : IModal
{
    public string Title => "Ghi chú đăng ký";

    [InputLabel("Ghi chú (để trống để xóa)")]
    [ModalTextInput("signup_note", TextInputStyle.Paragraph, maxLength: 300)]
    public string? Note { get; set; }
}

