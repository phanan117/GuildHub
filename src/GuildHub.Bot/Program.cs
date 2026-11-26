using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GuildHub.Bot.Configuration;
using GuildHub.Core.Data;
using GuildHub.Bot.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    builder.Services.AddSerilog((services, loggerConfig) => loggerConfig
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Thiếu ConnectionStrings:DefaultConnection trong cấu hình.");

    builder.Services.AddDbContextFactory<GuildHubDbContext>(options =>
        options.UseNpgsql(connectionString, b => b.MigrationsAssembly("GuildHub.Core")));

    builder.Services.AddHttpClient("DiscordApi", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    builder.Services.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers,
        LogLevel = LogSeverity.Info,
        AlwaysDownloadUsers = false
    }));

    builder.Services.AddSingleton(sp =>
    {
        var client = sp.GetRequiredService<DiscordSocketClient>();
        return new InteractionService(client, new InteractionServiceConfig
        {
            DefaultRunMode = RunMode.Sync,
            LogLevel = LogSeverity.Info,
            UseCompiledLambda = false
        });
    });

    builder.Services.AddSingleton<IconSyncService>();
    builder.Services.AddSingleton<EventService>();
    builder.Services.AddSingleton<EventMessageBuilder>();
    builder.Services.AddSingleton<EventExcelExporter>();
    builder.Services.AddSingleton<MemberRealtimeSyncService>();

    builder.Services.AddHostedService<DiscordBotHostedService>();
    builder.Services.AddHostedService<EventAutoCloseHostedService>();
    builder.Services.AddHostedService<PeriodicMemberSyncHostedService>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (HostAbortedException)
{
    // Bỏ qua khi EF Core design-time tools dừng host
}
catch (Exception ex)
{
    Log.Fatal(ex, "GuildHub Bot bị dừng đột ngột do lỗi nghiêm trọng.");
}
finally
{
    await Log.CloseAndFlushAsync();
}

