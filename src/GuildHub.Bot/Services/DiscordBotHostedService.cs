using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GuildHub.Bot.Configuration;
using GuildHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GuildHub.Bot.Services;

public sealed class DiscordBotHostedService(
    DiscordSocketClient client,
    InteractionService interactionService,
    IServiceProvider serviceProvider,
    IDbContextFactory<GuildHubDbContext> dbFactory,
    IconSyncService iconSyncService,
    MemberRealtimeSyncService memberRealtimeSync,
    IOptions<BotOptions> options,
    ILogger<DiscordBotHostedService> logger) : IHostedService
{
    private bool _commandsRegistered;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var botConfig = options.Value;

        if (botConfig.AutoMigrateDatabase)
        {
            logger.LogInformation("Đang kiểm tra và áp dụng EF Core Migrations cho PostgreSQL...");
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("PostgreSQL database đã sẵn sàng.");
        }

        await iconSyncService.LoadCacheFromDatabaseAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(botConfig.Token) || botConfig.Token.Contains("YOUR_DISCORD_BOT_TOKEN"))
        {
            logger.LogError(
                "Chưa cấu hình Discord Bot Token! Vui lòng cập nhật 'DiscordBot:Token' trong appsettings.json hoặc biến môi trường 'DiscordBot__Token'.");
            return;
        }

        client.Log += LogDiscordMessageAsync;
        interactionService.Log += LogDiscordMessageAsync;
        client.Ready += OnClientReadyAsync;
        client.InteractionCreated += OnInteractionCreatedAsync;
        client.GuildMemberUpdated += OnGuildMemberUpdatedAsync;
        client.UserJoined += OnUserJoinedAsync;
        client.UserLeft += OnUserLeftAsync;
        interactionService.InteractionExecuted += OnInteractionExecutedAsync;

        await interactionService.AddModulesAsync(Assembly.GetExecutingAssembly(), serviceProvider);

        await client.LoginAsync(TokenType.Bot, botConfig.Token);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        client.Ready -= OnClientReadyAsync;
        client.InteractionCreated -= OnInteractionCreatedAsync;
        client.GuildMemberUpdated -= OnGuildMemberUpdatedAsync;
        client.UserJoined -= OnUserJoinedAsync;
        client.UserLeft -= OnUserLeftAsync;
        interactionService.InteractionExecuted -= OnInteractionExecutedAsync;

        await client.StopAsync();
        await client.LogoutAsync();
    }

    private Task OnGuildMemberUpdatedAsync(Cacheable<SocketGuildUser, ulong> before, SocketGuildUser after)
    {
        _ = Task.Run(() => memberRealtimeSync.HandleMemberUpdatedAsync(after));
        return Task.CompletedTask;
    }

    private Task OnUserJoinedAsync(SocketGuildUser user)
    {
        _ = Task.Run(() => memberRealtimeSync.HandleUserJoinedAsync(user));
        return Task.CompletedTask;
    }

    private Task OnUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        _ = Task.Run(() => memberRealtimeSync.HandleUserLeftAsync(guild, user));
        return Task.CompletedTask;
    }

    private async Task OnClientReadyAsync()
    {
        logger.LogInformation("Bot đã kết nối thành công với tài khoản: {Username} ({Id})",
            client.CurrentUser.Username, client.CurrentUser.Id);

        if (_commandsRegistered)
        {
            return;
        }

        _commandsRegistered = true;

        try
        {
            if (options.Value.AutoSyncIcons)
            {
                var appInfo = await client.GetApplicationInfoAsync();
                var syncResult = await iconSyncService.SyncApplicationEmojisAsync(appInfo.Id);
                logger.LogInformation(
                    "Đồng bộ icon hoàn tất từ '{Dir}': +{Uploaded} mới, {Updated} cập nhật, {Unchanged} giữ nguyên, {Missing} thiếu.",
                    syncResult.ResolvedDirectory,
                    syncResult.Uploaded,
                    syncResult.Updated,
                    syncResult.Unchanged,
                    syncResult.MissingFiles);
            }

            if (options.Value.DevGuildId is > 0)
            {
                await interactionService.RegisterCommandsToGuildAsync(options.Value.DevGuildId.Value);
                logger.LogInformation("Đã đăng ký Slash Commands cho Guild ID: {GuildId}", options.Value.DevGuildId.Value);
            }
            else
            {
                await interactionService.RegisterCommandsGloballyAsync();
                logger.LogInformation("Đã đăng ký Slash Commands toàn cục (Global) cho mọi server.");

                // Tự động xóa Guild Commands cũ (nếu trước đó từng chạy chế độ DevGuildId) để tránh hiện 2 lệnh trùng nhau
                foreach (var guild in client.Guilds)
                {
                    try
                    {
                        var guildCommands = await guild.GetApplicationCommandsAsync();
                        if (guildCommands.Count > 0)
                        {
                            await guild.DeleteApplicationCommandsAsync();
                            logger.LogInformation("Đã dọn {Count} Guild Command cũ tại server {GuildName} ({GuildId}) để tránh trùng với Global Command.",
                                guildCommands.Count, guild.Name, guild.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Không thể kiểm tra/dọn Guild Command cũ tại server {GuildId}.", guild.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi trong quá trình khởi tạo Slash Commands / đồng bộ Icon.");
        }
    }

    private Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        // Đẩy sang background task để không chặn Gateway thread của Discord.Net,
        // đồng thời giữ IServiceScope sống trọn vẹn cho tới khi RunMode.Sync xử lý xong.
        _ = Task.Run(() => HandleInteractionAsync(interaction));
        return Task.CompletedTask;
    }

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        var interactionDetail = interaction switch
        {
            SocketSlashCommand slash => $"/{slash.CommandName}",
            SocketMessageComponent comp => $"component:{comp.Data.CustomId}",
            SocketModal modal => $"modal:{modal.Data.CustomId}",
            _ => interaction.Type.ToString()
        };

        logger.LogInformation(
            "Nhận interaction {Detail} (ID: {InteractionId}) từ {Username} ({UserId})",
            interactionDetail, interaction.Id, interaction.User.Username, interaction.User.Id);

        try
        {
            using var scope = serviceProvider.CreateScope();
            var context = new SocketInteractionContext(client, interaction);
            var result = await interactionService.ExecuteCommandAsync(context, scope.ServiceProvider);

            if (!result.IsSuccess)
            {
                var exception = result is ExecuteResult execResult ? execResult.Exception : null;
                logger.LogError(
                    exception,
                    "Xử lý interaction {Detail} thất bại ({Error}): {Reason}",
                    interactionDetail, result.Error, result.ErrorReason);

                await TrySendErrorResponseAsync(
                    interaction,
                    $"❌ Không thể xử lý yêu cầu: {result.ErrorReason}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi ngoại lệ khi xử lý interaction {Detail} (ID: {InteractionId}).",
                interactionDetail, interaction.Id);

            await TrySendErrorResponseAsync(
                interaction,
                "❌ Đã có lỗi xảy ra khi xử lý yêu cầu. Vui lòng kiểm tra file logs.");
        }
    }

    private static async Task TrySendErrorResponseAsync(SocketInteraction interaction, string message)
    {
        if (interaction.Type is not (InteractionType.ApplicationCommand
            or InteractionType.MessageComponent
            or InteractionType.ModalSubmit))
        {
            return;
        }

        var errorCard = new ComponentBuilderV2()
            .AddComponent(new ContainerBuilder()
                .WithAccentColor(new Color(0xEF, 0x44, 0x44))
                .AddComponent(new TextDisplayBuilder("### ❌ Đã xảy ra lỗi"))
                .AddComponent(new SeparatorBuilder(isDivider: true, spacing: SeparatorSpacingSize.Small))
                .AddComponent(new TextDisplayBuilder(message)))
            .Build();

        try
        {
            if (!interaction.HasResponded)
            {
                await interaction.RespondAsync(components: errorCard, ephemeral: true, flags: MessageFlags.ComponentsV2);
            }
            else
            {
                await interaction.FollowupAsync(components: errorCard, ephemeral: true, flags: MessageFlags.ComponentsV2);
            }
        }
        catch
        {
            // Bỏ qua lỗi phụ nếu token interaction đã hết hạn
        }
    }

    private Task OnInteractionExecutedAsync(ICommandInfo commandInfo, IInteractionContext context, IResult result)
    {
        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Hoàn tất interaction '{Name}' của {Username} ({UserId}).",
                commandInfo?.Name ?? "unknown",
                context.User.Username,
                context.User.Id);
        }
        else
        {
            var exception = result is ExecuteResult execResult ? execResult.Exception : null;
            logger.LogError(
                exception,
                "Interaction '{Name}' của {Username} ({UserId}) thất bại ({Error}): {Reason}",
                commandInfo?.Name ?? "unknown",
                context.User.Username,
                context.User.Id,
                result.Error,
                result.ErrorReason);
        }

        return Task.CompletedTask;
    }

    private Task LogDiscordMessageAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        logger.Log(level, message.Exception, "[Discord:{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}

