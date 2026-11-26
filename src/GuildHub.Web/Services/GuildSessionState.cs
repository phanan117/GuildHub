using System.Security.Claims;
using System.Text.Json;
using GuildHub.Web.Auth;

namespace GuildHub.Web.Services;

public sealed class GuildSessionState
{
    public DiscordUserProfile? User { get; private set; }
    public ulong CurrentGuildId { get; private set; }
    public string QuickSearchQuery { get; set; } = string.Empty;

    public AuthorizedGuildInfo? CurrentGuild =>
        User?.AuthorizedGuilds.FirstOrDefault(g => g.GuildId == CurrentGuildId)
        ?? User?.AuthorizedGuilds.FirstOrDefault();

    public event Action? StateChanged;

    public void InitializeFromPrincipal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            User = null;
            CurrentGuildId = 0;
            return;
        }

        var userIdStr = principal.FindFirstValue(DiscordClaimTypes.UserId);
        if (!ulong.TryParse(userIdStr, out var userId))
        {
            return;
        }

        var guildsJson = principal.FindFirstValue(DiscordClaimTypes.AuthorizedGuilds);
        var guilds = string.IsNullOrWhiteSpace(guildsJson)
            ? []
            : JsonSerializer.Deserialize<List<AuthorizedGuildInfo>>(guildsJson) ?? [];

        User = new DiscordUserProfile(
            UserId: userId,
            Username: principal.FindFirstValue(DiscordClaimTypes.Username) ?? string.Empty,
            DisplayName: principal.FindFirstValue(DiscordClaimTypes.DisplayName) ?? principal.Identity.Name ?? "Admin",
            AvatarUrl: principal.FindFirstValue(DiscordClaimTypes.AvatarUrl),
            AuthorizedGuilds: guilds);

        if (CurrentGuildId == 0 || !guilds.Any(g => g.GuildId == CurrentGuildId))
        {
            CurrentGuildId = guilds.FirstOrDefault()?.GuildId ?? 0;
        }
    }

    public void SelectGuild(ulong guildId)
    {
        if (User is null || !User.AuthorizedGuilds.Any(g => g.GuildId == guildId))
        {
            return;
        }

        if (CurrentGuildId != guildId)
        {
            CurrentGuildId = guildId;
            NotifyStateChanged();
        }
    }

    public void NotifyStateChanged() => StateChanged?.Invoke();
}
