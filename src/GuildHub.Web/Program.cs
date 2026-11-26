using GuildHub.Core.Data;
using GuildHub.Web.Auth;
using GuildHub.Web.Components;
using GuildHub.Web.Configuration;
using GuildHub.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    builder.Host.UseSerilog((context, services, loggerConfig) => loggerConfig
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.Configure<WebDiscordOptions>(
        builder.Configuration.GetSection(WebDiscordOptions.SectionName));

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Thiếu ConnectionStrings:DefaultConnection trong cấu hình.");

    builder.Services.AddDbContextFactory<GuildHubDbContext>(options =>
        options.UseNpgsql(connectionString, b => b.MigrationsAssembly("GuildHub.Core")));

    builder.Services.AddHttpClient("DiscordApi", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    // Lưu khóa mã hóa Cookie ra thư mục bền vững để không bị đăng xuất khi rebuild/restart Docker
    var keysDir = Path.Combine(AppContext.BaseDirectory, "keys");
    Directory.CreateDirectory(keysDir);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
        .SetApplicationName("GuildHub.Web");

    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "guildhub_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login?error=not_admin";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
        });

    builder.Services.AddAuthorization();
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddHttpContextAccessor();

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddScoped<GuildSessionState>();
    builder.Services.AddScoped<DiscordSyncService>();
    builder.Services.AddScoped<DashboardService>();
    builder.Services.AddScoped<MemberManagementService>();

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });

    var app = builder.Build();

    // Áp dụng EF Core Migrations tự động khi khởi động
    var webOptions = app.Services.GetRequiredService<IOptions<WebDiscordOptions>>().Value;
    if (webOptions.AutoMigrateDatabase)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<GuildHubDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
        Log.Information("GuildHub Web: PostgreSQL database đã sẵn sàng.");
    }

    app.UseForwardedHeaders();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStaticFiles();

    // Phục vụ trực tiếp thư mục Icon/ (21 file PNG dùng chung với Bot) tại URL /icons/*.png
    var resolvedIconDir = ResolveIconDirectory(webOptions.IconDirectory);
    if (resolvedIconDir is not null)
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(resolvedIconDir),
            RequestPath = "/icons"
        });
        Log.Information("Phục vụ static icons tại /icons từ '{Dir}'.", resolvedIconDir);
    }

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapDiscordAuthEndpoints();

    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    await app.RunAsync();
}
catch (HostAbortedException)
{
    // Bỏ qua khi EF Core design-time tools dừng host
}
catch (Exception ex)
{
    Log.Fatal(ex, "GuildHub Web bị dừng đột ngột do lỗi nghiêm trọng.");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string? ResolveIconDirectory(string configuredPath)
{
    if (Path.IsPathRooted(configuredPath) && Directory.Exists(configuredPath))
    {
        return configuredPath;
    }

    var roots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
    foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var dir = new DirectoryInfo(root);
        for (var depth = 0; depth < 6 && dir is not null; depth++)
        {
            var candidate = Path.Combine(dir.FullName, configuredPath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }
    }

    return null;
}
