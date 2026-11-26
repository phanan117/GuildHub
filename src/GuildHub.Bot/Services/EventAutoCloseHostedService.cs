using Discord;
using Discord.WebSocket;
using GuildHub.Core.Domain.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GuildHub.Bot.Services;

/// <summary>
/// Tác vụ nền kiểm tra mỗi 15 giây:
/// 1. Tự động đóng các form đã quá thời gian đóng (CloseTimeUtc).
/// 2. Khi đến thời gian bắt đầu online (EventTimeUtc), tag toàn bộ thành viên đã đăng ký
///    (bao gồm Tham gia, Đi trễ, Dự bị — loại trừ Vắng mặt và Không tham gia).
/// </summary>
public sealed class EventAutoCloseHostedService(
    EventService eventService,
    EventMessageBuilder messageBuilder,
    DiscordSocketClient client,
    ILogger<EventAutoCloseHostedService> logger) : BackgroundService
{
    private const int MentionsPerMessageBatch = 60;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (client.ConnectionState != ConnectionState.Connected)
            {
                continue;
            }

            await ProcessExpiredFormsAsync(stoppingToken);
            await ProcessOnlineRemindersAsync(stoppingToken);
        }
    }

    private async Task ProcessExpiredFormsAsync(CancellationToken stoppingToken)
    {
        try
        {
            var connectedGuildIds = client.Guilds.Select(g => g.Id).ToList();
            var closedEvents = await eventService.CloseExpiredEventsAsync(connectedGuildIds, stoppingToken);
            foreach (var ev in closedEvents)
            {
                logger.LogInformation("Tự động đóng form sự kiện #{EventId} ('{Title}') do đã hết hạn.", ev.Id, ev.Title);

                if (ev.MessageId == 0 || client.GetChannel(ev.ChannelId) is not IMessageChannel channel)
                {
                    continue;
                }

                try
                {
                    await channel.ModifyMessageAsync(ev.MessageId, props =>
                    {
                        props.Embed = messageBuilder.BuildEventEmbed(ev);
                        props.Components = messageBuilder.BuildEventComponents(ev);
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Không thể cập nhật tin nhắn Discord sau khi tự động đóng sự kiện #{EventId}.", ev.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi trong tác vụ tự động đóng form hết hạn.");
        }
    }

    private async Task ProcessOnlineRemindersAsync(CancellationToken stoppingToken)
    {
        try
        {
            var connectedGuildIds = client.Guilds.Select(g => g.Id).ToList();
            var dueEvents = await eventService.ClaimDueOnlineReminderEventsAsync(connectedGuildIds, stoppingToken);
            foreach (var ev in dueEvents)
            {
                if (client.GetChannel(ev.ChannelId) is not IMessageChannel channel)
                {
                    logger.LogWarning("Không tìm thấy kênh {ChannelId} để gửi thông báo giờ online cho sự kiện #{EventId}.",
                        ev.ChannelId, ev.Id);
                    continue;
                }

                var targetUserIds = ev.Signups
                    .Where(s => s.Status is not (SignupStatus.Absent or SignupStatus.NotParticipating))
                    .OrderBy(s => s.SignupOrder)
                    .Select(s => s.DiscordUserId)
                    .Distinct()
                    .ToList();

                if (targetUserIds.Count == 0)
                {
                    logger.LogInformation("Sự kiện #{EventId} ('{Title}') đã đến giờ online nhưng không có thành viên nào cần tag.",
                        ev.Id, ev.Title);
                    continue;
                }

                var batches = targetUserIds
                    .Chunk(MentionsPerMessageBatch)
                    .Select(chunk => (IReadOnlyList<ulong>)chunk)
                    .ToList();

                var messageReference = ev.MessageId > 0
                    ? new MessageReference(ev.MessageId, ev.ChannelId, ev.GuildId, failIfNotExists: false)
                    : null;

                for (var i = 0; i < batches.Count; i++)
                {
                    var batch = batches[i];
                    var card = messageBuilder.BuildOnlineReminderComponentsV2(
                        ev,
                        batch,
                        targetUserIds.Count,
                        batchIndex: i,
                        totalBatches: batches.Count);

                    var allowedMentions = new AllowedMentions { UserIds = batch.ToList() };

                    try
                    {
                        await channel.SendMessageAsync(
                            components: card,
                            allowedMentions: allowedMentions,
                            messageReference: i == 0 ? messageReference : null,
                            flags: MessageFlags.ComponentsV2);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Lỗi khi gửi thông báo tag giờ online (lượt {Batch}/{Total}) cho sự kiện #{EventId}.",
                            i + 1, batches.Count, ev.Id);
                    }
                }

                logger.LogInformation(
                    "Đã gửi thông báo đến giờ online cho sự kiện #{EventId} ('{Title}') và tag {Count} thành viên.",
                    ev.Id, ev.Title, targetUserIds.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi trong tác vụ gửi thông báo đến giờ online.");
        }
    }
}

