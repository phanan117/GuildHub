using Discord;
using Discord.Net;
using Discord.WebSocket;
using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GuildHub.Bot.Services;

/// <summary>
/// Tác vụ nền kiểm tra mỗi 10 giây các lịch hoạt động (ScheduledEvent):
/// 1. Các lịch đã được Admin bấm "Tạo form ngay" trên Web (TriggerFormNow = true).
/// 2. Các lịch bật AutoCreateForm = true và đã tới giờ tự động mở form (AutoCreateAtUtc <= now).
/// Tự động tạo WarEvent, gửi Embed form + Components + ping Role lên kênh Discord chỉ định,
/// rồi cập nhật lại CreatedWarEventId vào bảng scheduled_events.
/// </summary>
public sealed class ScheduledFormHostedService(
    IDbContextFactory<GuildHubDbContext> dbFactory,
    EventService eventService,
    EventMessageBuilder messageBuilder,
    DiscordSocketClient client,
    ILogger<ScheduledFormHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AutoRetryBackoff = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (client.ConnectionState != ConnectionState.Connected)
            {
                continue;
            }

            await ProcessDueSchedulesAsync(stoppingToken);
        }
    }

    private async Task ProcessDueSchedulesAsync(CancellationToken stoppingToken)
    {
        try
        {
            var connectedGuildIds = client.Guilds.Select(g => g.Id).ToList();
            if (connectedGuildIds.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var retryCutoff = now - AutoRetryBackoff;
            var staleCutoff = now.AddHours(-24);

            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);

            var dueIds = await db.ScheduledEvents
                .AsNoTracking()
                .Where(e => !e.IsCancelled
                            && e.CreatedWarEventId == null
                            && connectedGuildIds.Contains(e.GuildId)
                            && (e.TriggerFormNow
                                || (e.AutoCreateForm
                                    && e.AutoCreateAtUtc != null
                                    && e.AutoCreateAtUtc <= now
                                    && e.ScheduledTimeUtc >= staleCutoff
                                    && (e.LastFormAttemptAtUtc == null || e.LastFormAttemptAtUtc <= retryCutoff))))
                .OrderBy(e => e.ScheduledTimeUtc)
                .Select(e => e.Id)
                .ToListAsync(stoppingToken);

            foreach (var scheduleId in dueIds)
            {
                await ProcessSingleScheduleAsync(scheduleId, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Lỗi trong tác vụ tự động tạo form từ Lịch hoạt động.");
        }
    }

    private async Task ProcessSingleScheduleAsync(long scheduleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var scheduled = await db.ScheduledEvents.FirstOrDefaultAsync(e => e.Id == scheduleId, ct);
        if (scheduled is null || scheduled.IsCancelled || scheduled.CreatedWarEventId.HasValue)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var isManualTrigger = scheduled.TriggerFormNow;

        // Kiểm tra lại điều kiện tránh race condition
        if (!isManualTrigger)
        {
            if (!scheduled.AutoCreateForm || !scheduled.AutoCreateAtUtc.HasValue || scheduled.AutoCreateAtUtc.Value > now)
            {
                return;
            }

            if (scheduled.CloseTimeUtc.HasValue && scheduled.CloseTimeUtc.Value <= now)
            {
                scheduled.AutoCreateForm = false;
                scheduled.LastFormAttemptAtUtc = now;
                scheduled.LastFormError = "Đã quá giờ đóng form nên hệ thống bỏ qua việc tự động tạo form.";
                scheduled.UpdatedAtUtc = now;
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        if (!scheduled.TargetChannelId.HasValue || scheduled.TargetChannelId.Value == 0)
        {
            scheduled.TriggerFormNow = false;
            scheduled.LastFormAttemptAtUtc = now;
            scheduled.LastFormError = "Lịch hoạt động chưa chọn kênh Discord để đăng form.";
            scheduled.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return;
        }

        var guild = client.GetGuild(scheduled.GuildId);
        if (guild is null || client.GetChannel(scheduled.TargetChannelId.Value) is not IMessageChannel channel)
        {
            scheduled.TriggerFormNow = false;
            scheduled.LastFormAttemptAtUtc = now;
            scheduled.LastFormError = "Không tìm thấy kênh Discord chỉ định (kênh đã bị xóa hoặc Bot chưa có quyền xem kênh).";
            scheduled.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "Không tìm thấy kênh {ChannelId} trên Guild {GuildId} cho lịch #{ScheduleId} ('{Title}').",
                scheduled.TargetChannelId.Value, scheduled.GuildId, scheduled.Id, scheduled.Title);
            return;
        }

        // Nếu tạo thủ công sau khi đã qua giờ đóng cũ -> bỏ CloseTimeUtc cũ để form vẫn mở cho thành viên đăng ký
        var effectiveCloseTimeUtc = scheduled.CloseTimeUtc.HasValue && scheduled.CloseTimeUtc.Value > now
            ? scheduled.CloseTimeUtc
            : null;

        WarEvent? createdEvent = null;
        try
        {
            createdEvent = await eventService.CreateEventAsync(
                guildId: scheduled.GuildId,
                channelId: channel.Id,
                leaderDiscordId: scheduled.CreatedByDiscordId,
                title: scheduled.Title,
                onlineTimeUtc: scheduled.ScheduledTimeUtc,
                closeTimeUtc: effectiveCloseTimeUtc,
                mentionRoleId: scheduled.MentionRoleId,
                description: scheduled.Description,
                eventType: scheduled.EventType,
                ct: ct);

            var embed = messageBuilder.BuildEventEmbed(createdEvent);
            var components = messageBuilder.BuildEventComponents(createdEvent);

            string? mentionText = null;
            var allowedMentions = AllowedMentions.None;

            if (scheduled.MentionRoleId.HasValue && scheduled.MentionRoleId.Value > 0)
            {
                var isEveryone = scheduled.MentionRoleId.Value == guild.EveryoneRole.Id;
                mentionText = isEveryone ? "@everyone" : $"<@&{scheduled.MentionRoleId.Value}>";
                allowedMentions = isEveryone
                    ? new AllowedMentions(AllowedMentionTypes.Everyone)
                    : new AllowedMentions { RoleIds = [scheduled.MentionRoleId.Value] };
            }

            var sentMessage = await channel.SendMessageAsync(
                text: mentionText,
                embed: embed,
                components: components,
                allowedMentions: allowedMentions);

            await eventService.AttachMessageIdAsync(createdEvent.Id, sentMessage.Id, ct);

            var completedAt = DateTimeOffset.UtcNow;
            scheduled.CreatedWarEventId = createdEvent.Id;
            scheduled.FormCreatedAtUtc = completedAt;
            scheduled.TriggerFormNow = false;
            scheduled.LastFormError = null;
            scheduled.LastFormAttemptAtUtc = completedAt;
            scheduled.UpdatedAtUtc = completedAt;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Đã tự động đăng form WarEvent #{WarEventId} từ Lịch #{ScheduleId} ('{Title}') vào kênh #{ChannelId} (MessageId: {MessageId}).",
                createdEvent.Id, scheduled.Id, scheduled.Title, channel.Id, sentMessage.Id);
        }
        catch (HttpException httpEx)
        {
            if (createdEvent is not null)
            {
                await eventService.DeleteEventIfNoMessageAsync(createdEvent.Id, ct);
            }

            var errMsg = $"Bot không thể gửi tin nhắn vào kênh (HTTP {(int)httpEx.HttpCode} / {httpEx.DiscordCode}). Kiểm tra quyền View Channel, Send Messages, Embed Links, Mention Roles.";
            scheduled.TriggerFormNow = false;
            scheduled.LastFormAttemptAtUtc = DateTimeOffset.UtcNow;
            scheduled.LastFormError = Truncate(errMsg, 500);
            scheduled.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogError(
                httpEx,
                "Lỗi Discord API khi đăng form từ Lịch #{ScheduleId} ('{Title}') vào kênh {ChannelId}.",
                scheduled.Id, scheduled.Title, scheduled.TargetChannelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (createdEvent is not null)
            {
                await eventService.DeleteEventIfNoMessageAsync(createdEvent.Id, ct);
            }

            scheduled.TriggerFormNow = false;
            scheduled.LastFormAttemptAtUtc = DateTimeOffset.UtcNow;
            scheduled.LastFormError = Truncate($"Lỗi tạo form: {ex.Message}", 500);
            scheduled.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogError(
                ex,
                "Lỗi không mong muốn khi đăng form từ Lịch #{ScheduleId} ('{Title}').",
                scheduled.Id, scheduled.Title);
        }
    }

    private static string Truncate(string input, int maxLen) =>
        input.Length <= maxLen ? input : input[..maxLen];
}
