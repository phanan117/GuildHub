using Discord;
using Discord.WebSocket;
using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GuildHub.Bot.Services;

/// <summary>
/// Đồng bộ thành viên Real-time từ Discord Gateway (GuildMemberUpdated, UserJoined, UserLeft)
/// và đồng bộ định kỳ toàn bộ server về PostgreSQL cho Web quản trị.
/// </summary>
public sealed class MemberRealtimeSyncService(
    IDbContextFactory<GuildHubDbContext> dbFactory,
    ILogger<MemberRealtimeSyncService> logger)
{
    public async Task HandleMemberUpdatedAsync(SocketGuildUser after, CancellationToken ct = default)
    {
        if (after.IsBot) return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var guildId = after.Guild.Id;

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var config = await db.GuildConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.GuildId == guildId, ct);

            var requiredRoleId = config?.MemberRoleId;
            var hasGuildRole = !requiredRoleId.HasValue || after.Roles.Any(r => r.Id == requiredRoleId.Value);

            var existing = await db.GuildMembers
                .FirstOrDefaultAsync(m => m.GuildId == guildId && m.DiscordUserId == after.Id, ct);

            // Nếu server có lọc theo Role bang và user chưa có Role này lẫn chưa có trong DB -> bỏ qua
            if (requiredRoleId.HasValue && !hasGuildRole && existing is null)
            {
                return;
            }

            var classConfigs = await db.GuildClassConfigs
                .AsNoTracking()
                .Where(c => c.GuildId == guildId && c.DiscordRoleId != null)
                .ToListAsync(ct);

            var userRoleIds = after.Roles.Select(r => r.Id).ToHashSet();
            GameClass? roleMatchedClass = classConfigs
                .FirstOrDefault(c => userRoleIds.Contains(c.DiscordRoleId!.Value))
                ?.GameClass;

            var accountDisplayName = Truncate(
                !string.IsNullOrWhiteSpace(after.GlobalName) ? after.GlobalName : after.Username,
                100);
            var serverNick = !string.IsNullOrWhiteSpace(after.Nickname)
                ? Truncate(after.Nickname.Trim(), 100)
                : null;
            var avatarUrl = after.GetAvatarUrl(ImageFormat.Png, 128);

            if (existing is not null)
            {
                existing.DiscordUsername = Truncate(after.Username, 100);
                existing.DisplayName = accountDisplayName;
                existing.IngameNickname = serverNick;
                existing.AvatarUrl = avatarUrl;
                existing.HasGuildRole = hasGuildRole;
                existing.UpdatedAtUtc = now;

                // Nếu trước đó bị Remove trên Web nhưng nay được cấp lại Role bang trực tiếp trên Discord -> tự khôi phục
                if (requiredRoleId.HasValue && hasGuildRole && existing.IsRemoved)
                {
                    existing.IsRemoved = false;
                    existing.RemovedAtUtc = null;
                }

                // Nếu có cấu hình Role hệ phái và thành viên mang Role đó -> tự đồng bộ môn phái
                if (roleMatchedClass.HasValue)
                {
                    existing.GameClass = roleMatchedClass;
                }
            }
            else
            {
                db.GuildMembers.Add(new GuildMember
                {
                    GuildId = guildId,
                    DiscordUserId = after.Id,
                    DiscordUsername = Truncate(after.Username, 100),
                    DisplayName = accountDisplayName,
                    IngameNickname = serverNick,
                    AvatarUrl = avatarUrl,
                    GameClass = roleMatchedClass,
                    HasGuildRole = hasGuildRole,
                    IsRemoved = false,
                    IsExternal = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }

            await db.SaveChangesAsync(ct);
            logger.LogDebug("Real-time sync thành viên {Username} ({UserId}) tại Guild {GuildId}.",
                after.Username, after.Id, guildId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lỗi real-time sync GuildMemberUpdated cho user {UserId} tại Guild {GuildId}.",
                after.Id, after.Guild.Id);
        }
    }

    public async Task HandleUserJoinedAsync(SocketGuildUser user, CancellationToken ct = default)
    {
        if (user.IsBot) return;
        await HandleMemberUpdatedAsync(user, ct);
    }

    public async Task HandleUserLeftAsync(SocketGuild guild, SocketUser user, CancellationToken ct = default)
    {
        if (user.IsBot) return;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.GuildMembers
                .FirstOrDefaultAsync(m => m.GuildId == guild.Id && m.DiscordUserId == user.Id, ct);

            if (existing is null) return;

            existing.HasGuildRole = false;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Real-time sync: User {Username} ({UserId}) đã rời Guild {GuildId} -> đánh dấu Mất role.",
                user.Username, user.Id, guild.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lỗi real-time sync UserLeft cho user {UserId} tại Guild {GuildId}.", user.Id, guild.Id);
        }
    }

    /// <summary>
    /// Khi thành viên bấm chọn hệ phái trên form đăng ký của Bot mà trên Web đang 'Chưa link',
    /// tự động liên kết luôn môn phái đó vào hồ sơ guild_members.
    /// </summary>
    public async Task SyncClassFromBotSignupAsync(
        ulong guildId,
        ulong discordUserId,
        string displayName,
        GameClass gameClass,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var member = await db.GuildMembers
                .FirstOrDefaultAsync(m => m.GuildId == guildId && m.DiscordUserId == discordUserId, ct);

            if (member is null || member.GameClass.HasValue)
            {
                return;
            }

            member.GameClass = gameClass;
            member.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Tự động liên kết môn phái {Class} cho {Name} ({UserId}) từ đăng ký form Bot.",
                gameClass, displayName, discordUserId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không thể tự động liên kết môn phái từ form Bot cho user {UserId}.", discordUserId);
        }
    }

    /// <summary>
    /// Đồng bộ nền định kỳ toàn bộ thành viên của 1 server (lưới an toàn mỗi 30 phút).
    /// </summary>
    public async Task SyncGuildSnapshotAsync(SocketGuild guild, CancellationToken ct = default)
    {
        try
        {
            await guild.DownloadUsersAsync();
            var users = guild.Users.Where(u => !u.IsBot).ToList();
            if (users.Count == 0) return;

            var now = DateTimeOffset.UtcNow;
            var guildId = guild.Id;

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var config = await db.GuildConfigs.FirstOrDefaultAsync(c => c.GuildId == guildId, ct);
            if (config is null)
            {
                config = new GuildConfig
                {
                    GuildId = guildId,
                    GuildName = Truncate(guild.Name, 150),
                    IconUrl = guild.IconUrl,
                    UpdatedAtUtc = now
                };
                db.GuildConfigs.Add(config);
            }
            else
            {
                config.GuildName = Truncate(guild.Name, 150);
                config.IconUrl = guild.IconUrl;
            }

            var classConfigs = await db.GuildClassConfigs
                .AsNoTracking()
                .Where(c => c.GuildId == guildId && c.DiscordRoleId != null)
                .ToListAsync(ct);

            var classByRoleId = classConfigs
                .GroupBy(c => c.DiscordRoleId!.Value)
                .ToDictionary(g => g.Key, g => g.First().GameClass);

            var latestSignups = await db.EventSignups
                .AsNoTracking()
                .Where(s => s.WarEvent.GuildId == guildId && s.GameClass != null)
                .OrderByDescending(s => s.UpdatedAtUtc)
                .Select(s => new { s.DiscordUserId, s.GameClass })
                .ToListAsync(ct);

            var latestClassByUser = new Dictionary<ulong, GameClass>();
            foreach (var s in latestSignups)
            {
                if (s.GameClass.HasValue && !latestClassByUser.ContainsKey(s.DiscordUserId))
                {
                    latestClassByUser[s.DiscordUserId] = s.GameClass.Value;
                }
            }

            var existingMembers = await db.GuildMembers
                .Where(m => m.GuildId == guildId)
                .ToListAsync(ct);

            var existingByDiscordId = existingMembers
                .Where(m => m.DiscordUserId.HasValue)
                .ToDictionary(m => m.DiscordUserId!.Value);

            var requiredRoleId = config.MemberRoleId;
            var seenIds = new HashSet<ulong>();
            var syncedCount = 0;

            foreach (var u in users)
            {
                var hasRole = !requiredRoleId.HasValue || u.Roles.Any(r => r.Id == requiredRoleId.Value);
                if (requiredRoleId.HasValue && !hasRole && !existingByDiscordId.ContainsKey(u.Id))
                {
                    continue;
                }

                seenIds.Add(u.Id);
                var accountDisplayName = Truncate(
                    !string.IsNullOrWhiteSpace(u.GlobalName) ? u.GlobalName : u.Username,
                    100);
                var serverNick = !string.IsNullOrWhiteSpace(u.Nickname)
                    ? Truncate(u.Nickname.Trim(), 100)
                    : null;
                var avatarUrl = u.GetAvatarUrl(ImageFormat.Png, 128);

                GameClass? roleMatchedClass = null;
                foreach (var r in u.Roles)
                {
                    if (classByRoleId.TryGetValue(r.Id, out var matched))
                    {
                        roleMatchedClass = matched;
                        break;
                    }
                }

                if (existingByDiscordId.TryGetValue(u.Id, out var existing))
                {
                    existing.DiscordUsername = Truncate(u.Username, 100);
                    existing.DisplayName = accountDisplayName;
                    existing.IngameNickname = serverNick;
                    existing.AvatarUrl = avatarUrl;
                    existing.HasGuildRole = hasRole;
                    existing.UpdatedAtUtc = now;

                    if (requiredRoleId.HasValue && hasRole && existing.IsRemoved)
                    {
                        existing.IsRemoved = false;
                        existing.RemovedAtUtc = null;
                    }

                    if (roleMatchedClass.HasValue)
                    {
                        existing.GameClass = roleMatchedClass;
                    }
                    else if (existing.GameClass is null && latestClassByUser.TryGetValue(u.Id, out var inferred))
                    {
                        existing.GameClass = inferred;
                    }
                }
                else
                {
                    GameClass? inferredClass = roleMatchedClass;
                    if (inferredClass is null && latestClassByUser.TryGetValue(u.Id, out var inferred))
                    {
                        inferredClass = inferred;
                    }

                    db.GuildMembers.Add(new GuildMember
                    {
                        GuildId = guildId,
                        DiscordUserId = u.Id,
                        DiscordUsername = Truncate(u.Username, 100),
                        DisplayName = accountDisplayName,
                        IngameNickname = serverNick,
                        AvatarUrl = avatarUrl,
                        GameClass = inferredClass,
                        HasGuildRole = hasRole,
                        IsRemoved = false,
                        IsExternal = false,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }

                syncedCount++;
            }

            foreach (var existing in existingMembers.Where(m => m.DiscordUserId.HasValue && !m.IsExternal))
            {
                if (!seenIds.Contains(existing.DiscordUserId!.Value))
                {
                    existing.HasGuildRole = false;
                    existing.UpdatedAtUtc = now;
                }
            }

            config.LastSyncedAtUtc = now;
            config.LastSyncedMemberCount = syncedCount;
            config.UpdatedAtUtc = now;

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Auto-sync định kỳ Guild '{GuildName}' ({GuildId}) hoàn tất: {Count} thành viên.",
                guild.Name, guildId, syncedCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lỗi auto-sync định kỳ cho Guild {GuildId}.", guild.Id);
        }
    }

    private static string Truncate(string input, int maxLen) =>
        input.Length <= maxLen ? input : input[..maxLen];
}
