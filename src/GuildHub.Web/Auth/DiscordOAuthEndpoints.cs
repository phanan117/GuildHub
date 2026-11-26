using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using GuildHub.Web.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuildHub.Web.Auth;

public static class DiscordOAuthEndpoints
{
    private const long PermissionAdministrator = 0x8L;
    private const long PermissionManageGuild = 0x20L;
    private const string StateCookieName = "gh_oauth_state";

    public static void MapDiscordAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/discord-login", (
            HttpContext http,
            IOptions<WebDiscordOptions> opts) =>
        {
            var cfg = opts.Value;
            if (string.IsNullOrWhiteSpace(cfg.ClientId) || string.IsNullOrWhiteSpace(cfg.ClientSecret))
            {
                return Results.Redirect("/login?error=not_configured");
            }

            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            http.Response.Cookies.Append(StateCookieName, state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(10)
            });

            var switchAccount = string.Equals(http.Request.Query["switch"], "true", StringComparison.OrdinalIgnoreCase);
            var prompt = switchAccount ? "consent" : "none";

            var redirectUri = BuildRedirectUri(http, cfg);
            var url = "https://discord.com/oauth2/authorize" +
                      $"?client_id={Uri.EscapeDataString(cfg.ClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      "&response_type=code" +
                      "&scope=identify%20guilds" +
                      $"&prompt={prompt}" +
                      $"&state={Uri.EscapeDataString(state)}";

            return Results.Redirect(url);
        });

        app.MapGet("/auth/discord-callback", async (
            HttpContext http,
            IHttpClientFactory httpClientFactory,
            IDbContextFactory<GuildHubDbContext> dbFactory,
            IOptions<WebDiscordOptions> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("DiscordOAuth");
            var cfg = opts.Value;

            var error = http.Request.Query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
            {
                return Results.Redirect("/login?error=cancelled");
            }

            var code = http.Request.Query["code"].ToString();
            var state = http.Request.Query["state"].ToString();
            var expectedState = http.Request.Cookies[StateCookieName];
            http.Response.Cookies.Delete(StateCookieName);

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || state != expectedState)
            {
                logger.LogWarning("Discord OAuth state không khớp hoặc thiếu code.");
                return Results.Redirect("/login?error=invalid_state");
            }

            var client = httpClientFactory.CreateClient("DiscordApi");
            var redirectUri = BuildRedirectUri(http, cfg);

            // 1. Đổi authorization code lấy access_token
            using var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId,
                ["client_secret"] = cfg.ClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri
            });

            using var tokenResp = await client.PostAsync("https://discord.com/api/v10/oauth2/token", tokenContent, ct);
            if (!tokenResp.IsSuccessStatusCode)
            {
                var errBody = await tokenResp.Content.ReadAsStringAsync(ct);
                logger.LogError("Đổi Discord OAuth token thất bại ({Code}): {Body}", tokenResp.StatusCode, errBody);
                return Results.Redirect("/login?error=token_failed");
            }

            var tokenData = await tokenResp.Content.ReadFromJsonAsync<DiscordTokenResponse>(cancellationToken: ct);
            if (tokenData is null || string.IsNullOrWhiteSpace(tokenData.AccessToken))
            {
                return Results.Redirect("/login?error=token_failed");
            }

            // 2. Lấy thông tin user Discord
            using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
            using var userResp = await client.SendAsync(userReq, ct);
            if (!userResp.IsSuccessStatusCode)
            {
                return Results.Redirect("/login?error=profile_failed");
            }

            var user = await userResp.Content.ReadFromJsonAsync<DiscordUserDto>(cancellationToken: ct);
            if (user is null || !ulong.TryParse(user.Id, out var userId))
            {
                return Results.Redirect("/login?error=profile_failed");
            }

            // 3. Lấy danh sách server của user và lọc các server user có quyền Administrator hoặc Manage Server
            using var guildsReq = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me/guilds");
            guildsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
            using var guildsResp = await client.SendAsync(guildsReq, ct);
            if (!guildsResp.IsSuccessStatusCode)
            {
                return Results.Redirect("/login?error=guilds_failed");
            }

            var userGuilds = await guildsResp.Content.ReadFromJsonAsync<List<DiscordGuildDto>>(cancellationToken: ct) ?? [];
            var adminGuilds = userGuilds
                .Where(g => g.Owner || HasAdminPermission(g.Permissions))
                .ToList();

            // 4. Đối chiếu với danh sách server mà Bot đang tham gia
            HashSet<string>? botGuildIds = null;
            if (!string.IsNullOrWhiteSpace(cfg.BotToken))
            {
                using var botGuildsReq = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me/guilds");
                botGuildsReq.Headers.Authorization = new AuthenticationHeaderValue("Bot", cfg.BotToken);
                using var botGuildsResp = await client.SendAsync(botGuildsReq, ct);
                if (botGuildsResp.IsSuccessStatusCode)
                {
                    var botGuilds = await botGuildsResp.Content.ReadFromJsonAsync<List<DiscordGuildDto>>(cancellationToken: ct) ?? [];
                    botGuildIds = botGuilds.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
                }
            }

            var authorized = new List<AuthorizedGuildInfo>();
            foreach (var g in adminGuilds)
            {
                if (botGuildIds is not null && !botGuildIds.Contains(g.Id))
                {
                    continue;
                }

                if (ulong.TryParse(g.Id, out var gid))
                {
                    var iconUrl = string.IsNullOrWhiteSpace(g.Icon)
                        ? null
                        : $"https://cdn.discordapp.com/icons/{g.Id}/{g.Icon}.png?size=128";
                    authorized.Add(new AuthorizedGuildInfo(gid, g.Name, iconUrl));
                }
            }

            if (authorized.Count == 0)
            {
                logger.LogWarning(
                    "Từ chối đăng nhập user {Username} ({UserId}): không có quyền Admin/ManageServer trên bất kỳ server nào có bot.",
                    user.Username, userId);
                return Results.Redirect("/login?error=not_admin");
            }

            // Cập nhật thông tin GuildConfig vào PostgreSQL
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var ids = authorized.Select(x => x.GuildId).ToList();
                var existingConfigs = await db.GuildConfigs
                    .Where(c => ids.Contains(c.GuildId))
                    .ToDictionaryAsync(c => c.GuildId, ct);

                foreach (var g in authorized)
                {
                    if (existingConfigs.TryGetValue(g.GuildId, out var config))
                    {
                        config.GuildName = g.Name;
                        config.IconUrl = g.IconUrl;
                        config.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        db.GuildConfigs.Add(new GuildConfig
                        {
                            GuildId = g.GuildId,
                            GuildName = g.Name,
                            IconUrl = g.IconUrl,
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        });
                    }
                }

                await db.SaveChangesAsync(ct);
            }

            var displayName = !string.IsNullOrWhiteSpace(user.GlobalName) ? user.GlobalName : user.Username;
            var avatarUrl = string.IsNullOrWhiteSpace(user.Avatar)
                ? null
                : $"https://cdn.discordapp.com/avatars/{user.Id}/{user.Avatar}.png?size=128";

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id),
                new(ClaimTypes.Name, displayName),
                new(DiscordClaimTypes.UserId, user.Id),
                new(DiscordClaimTypes.Username, user.Username),
                new(DiscordClaimTypes.DisplayName, displayName),
                new(DiscordClaimTypes.AuthorizedGuilds, JsonSerializer.Serialize(authorized))
            };

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                claims.Add(new Claim(DiscordClaimTypes.AvatarUrl, avatarUrl));
            }

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
                });

            logger.LogInformation(
                "Admin {Username} ({UserId}) đăng nhập thành công với {Count} server được cấp quyền.",
                user.Username, userId, authorized.Count);

            return Results.Redirect("/");
        });

        app.MapGet("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    private static bool HasAdminPermission(string? permissionsRaw)
    {
        if (!long.TryParse(permissionsRaw, out var perms))
        {
            return false;
        }

        return (perms & PermissionAdministrator) != 0 || (perms & PermissionManageGuild) != 0;
    }

    private static string BuildRedirectUri(HttpContext http, WebDiscordOptions cfg)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(cfg.PublicBaseUrl)
            ? cfg.PublicBaseUrl.TrimEnd('/')
            : $"{http.Request.Scheme}://{http.Request.Host}";

        var path = cfg.CallbackPath.StartsWith('/') ? cfg.CallbackPath : "/" + cfg.CallbackPath;
        return baseUrl + path;
    }

    private sealed class DiscordTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;
    }

    private sealed class DiscordUserDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("global_name")]
        public string? GlobalName { get; set; }

        [JsonPropertyName("avatar")]
        public string? Avatar { get; set; }
    }

    private sealed class DiscordGuildDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        [JsonPropertyName("owner")]
        public bool Owner { get; set; }

        [JsonPropertyName("permissions")]
        public string? Permissions { get; set; }
    }
}
