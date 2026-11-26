namespace GuildHub.Web.Auth;

public sealed record AuthorizedGuildInfo(ulong GuildId, string Name, string? IconUrl);

public sealed record DiscordUserProfile(
    ulong UserId,
    string Username,
    string DisplayName,
    string? AvatarUrl,
    IReadOnlyList<AuthorizedGuildInfo> AuthorizedGuilds);

public static class DiscordClaimTypes
{
    public const string UserId = "discord:user_id";
    public const string Username = "discord:username";
    public const string DisplayName = "discord:display_name";
    public const string AvatarUrl = "discord:avatar_url";
    public const string AuthorizedGuilds = "discord:authorized_guilds";
}
