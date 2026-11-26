using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GuildHub.Bot.Services;

/// <summary>
/// Lớp lưới an toàn: tự động đồng bộ nền toàn bộ thành viên các server mỗi 30 phút
/// (kết hợp cùng Real-time Sync qua Gateway event).
/// </summary>
public sealed class PeriodicMemberSyncHostedService(
    DiscordSocketClient client,
    MemberRealtimeSyncService syncService,
    ILogger<PeriodicMemberSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Chờ 30 giây sau khi khởi động để Bot kết nối ổn định rồi chạy lượt đồng bộ đầu tiên
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        await RunSyncForAllGuildsAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSyncForAllGuildsAsync(stoppingToken);
        }
    }

    private async Task RunSyncForAllGuildsAsync(CancellationToken ct)
    {
        if (client.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        var guilds = client.Guilds.ToList();
        foreach (var guild in guilds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await syncService.SyncGuildSnapshotAsync(guild, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lỗi khi auto-sync Guild {GuildId}.", guild.Id);
            }
        }
    }
}
