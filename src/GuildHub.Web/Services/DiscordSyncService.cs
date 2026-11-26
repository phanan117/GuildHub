using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;
using GuildHub.Web.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuildHub.Web.Services;

public sealed record DiscordRoleOption(ulong RoleId, string Name, int Color, int Position);

public sealed record DiscordChannelOption(ulong ChannelId, string Name, string? CategoryName, int Position, bool IsAnnouncement);

public sealed record DiscordMemberCandidate(
    ulong DiscordUserId,
    string Username,
    string DisplayName,
    string? ServerNickname,
    string? AvatarUrl);

public sealed record DiscordRoleActionResult(bool Success, bool MemberNotInGuild, string? ErrorMessage = null);

public sealed record DiscordSyncResult(
    bool Success,
    int SyncedCount,
    int NewlyLinkedFromEvents,
    bool UsedSignupFallback,
    string Message,
    DateTimeOffset SyncedAtUtc);

public sealed class DiscordSyncService(
    IDbContextFactory<GuildHubDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<WebDiscordOptions> options,
    ILogger<DiscordSyncService> logger)
{
    private static readonly ConcurrentDictionary<ulong, DateTimeOffset> LastSyncCooldowns = new();
    private static readonly TimeSpan SyncCooldown = TimeSpan.FromSeconds(15);
    public async Task<List<DiscordRoleOption>> GetGuildRolesAsync(
        ulong guildId,
        bool includeEveryone = false,
        CancellationToken ct = default)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var client = httpClientFactory.CreateClient("DiscordApi");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{guildId}/roles");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Không thể lấy danh sách Role cho Guild {GuildId} (HTTP {Code}).", guildId, resp.StatusCode);
            return [];
        }

        var roles = await resp.Content.ReadFromJsonAsync<List<DiscordRoleDto>>(cancellationToken: ct) ?? [];
        return roles
            .Where(r => !r.Managed && (includeEveryone || r.Name != "@everyone"))
            .OrderByDescending(r => r.Name == "@everyone" ? int.MaxValue : r.Position)
            .Select(r => ulong.TryParse(r.Id, out var id)
                ? new DiscordRoleOption(id, r.Name, r.Color, r.Position)
                : null)
            .Where(r => r is not null)
            .Cast<DiscordRoleOption>()
            .ToList();
    }

    public async Task<List<DiscordChannelOption>> GetGuildTextChannelsAsync(ulong guildId, CancellationToken ct = default)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var client = httpClientFactory.CreateClient("DiscordApi");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{guildId}/channels");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Không thể lấy danh sách Kênh cho Guild {GuildId} (HTTP {Code}).", guildId, resp.StatusCode);
            return [];
        }

        var channels = await resp.Content.ReadFromJsonAsync<List<DiscordChannelDto>>(cancellationToken: ct) ?? [];

        // Type 4 = Category, Type 0 = Text, Type 5 = Announcement
        var categoriesById = channels
            .Where(c => c.Type == 4 && !string.IsNullOrWhiteSpace(c.Id))
            .ToDictionary(c => c.Id, c => (c.Name, c.Position));

        return channels
            .Where(c => c.Type is 0 or 5 && ulong.TryParse(c.Id, out _))
            .OrderBy(c => c.ParentId is not null && categoriesById.TryGetValue(c.ParentId, out var cat) ? cat.Position : -1)
            .ThenBy(c => c.Position)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c =>
            {
                var catName = c.ParentId is not null && categoriesById.TryGetValue(c.ParentId, out var cat)
                    ? cat.Name
                    : null;
                return new DiscordChannelOption(
                    ChannelId: ulong.Parse(c.Id),
                    Name: c.Name,
                    CategoryName: catName,
                    Position: c.Position,
                    IsAnnouncement: c.Type == 5);
            })
            .ToList();
    }

    /// <summary>
    /// Lấy danh sách thành viên trên server Discord CHƯA có Role bang (để chọn trong modal Thêm thành viên).
    /// Nếu server chưa cấu hình Role bang thì lọc những thành viên chưa có mặt trong bảng guild_members đang hoạt động.
    /// </summary>
    public async Task<(List<DiscordMemberCandidate> Members, string? Warning)> GetUnassignedGuildMembersAsync(
        ulong guildId,
        CancellationToken ct = default)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return ([], "Chưa cấu hình Discord Bot Token.");
        }

        var (remoteMembers, missingIntent) = await FetchAllGuildMembersAsync(guildId, token, ct);
        if (remoteMembers is null)
        {
            return ([], missingIntent
                ? "Không thể tải danh sách thành viên server Discord (cần bật Server Members Intent cho Bot)."
                : "Không thể tải danh sách thành viên từ Discord API.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var config = await db.GuildConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId, ct);
        var requiredRoleStr = config?.MemberRoleId?.ToString();

        HashSet<ulong>? existingActiveIds = null;
        if (string.IsNullOrEmpty(requiredRoleStr))
        {
            var ids = await db.GuildMembers
                .AsNoTracking()
                .Where(m => m.GuildId == guildId && !m.IsRemoved && m.DiscordUserId != null)
                .Select(m => m.DiscordUserId!.Value)
                .ToListAsync(ct);
            existingActiveIds = ids.ToHashSet();
        }

        var candidates = new List<DiscordMemberCandidate>();
        foreach (var rm in remoteMembers)
        {
            if (rm.User is null || rm.User.Bot || !ulong.TryParse(rm.User.Id, out var uid))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(requiredRoleStr))
            {
                // Bỏ qua những người ĐÃ có Role bang
                if (rm.Roles is not null && rm.Roles.Contains(requiredRoleStr))
                {
                    continue;
                }
            }
            else if (existingActiveIds is not null && existingActiveIds.Contains(uid))
            {
                continue;
            }

            var displayName = !string.IsNullOrWhiteSpace(rm.User.GlobalName)
                ? rm.User.GlobalName
                : rm.User.Username;
            var avatarUrl = !string.IsNullOrWhiteSpace(rm.User.Avatar)
                ? $"https://cdn.discordapp.com/avatars/{rm.User.Id}/{rm.User.Avatar}.png?size=128"
                : null;

            candidates.Add(new DiscordMemberCandidate(
                DiscordUserId: uid,
                Username: rm.User.Username,
                DisplayName: displayName,
                ServerNickname: string.IsNullOrWhiteSpace(rm.Nick) ? null : rm.Nick.Trim(),
                AvatarUrl: avatarUrl));
        }

        var sorted = candidates
            .OrderBy(c => c.ServerNickname ?? c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (sorted, null);
    }

    /// <summary>
    /// Gỡ Role bang khỏi thành viên trực tiếp trên server Discord (yêu cầu Bot có quyền Manage Roles và vị trí Role cao hơn Role bang).
    /// </summary>
    public async Task<DiscordRoleActionResult> RemoveGuildRoleFromMemberAsync(
        ulong guildId,
        ulong discordUserId,
        ulong roleId,
        CancellationToken ct = default)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return new DiscordRoleActionResult(false, false, "Chưa cấu hình DiscordWeb:BotToken.");
        }

        var client = httpClientFactory.CreateClient("DiscordApi");
        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            $"https://discord.com/api/v10/guilds/{guildId}/members/{discordUserId}/roles/{roleId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
        req.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", "GuildHub Web Admin removed member from guild role");

        using var resp = await client.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
        {
            logger.LogInformation("Đã gỡ Role {RoleId} khỏi user {UserId} trên Guild {GuildId}.", roleId, discordUserId, guildId);
            return new DiscordRoleActionResult(true, false);
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogInformation("User {UserId} không còn trong Guild {GuildId} khi gỡ Role {RoleId}.", discordUserId, guildId, roleId);
            return new DiscordRoleActionResult(true, true);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        logger.LogWarning("Không thể gỡ Role {RoleId} khỏi user {UserId} trên Guild {GuildId} (HTTP {Code}): {Body}",
            roleId, discordUserId, guildId, resp.StatusCode, body);

        var message = resp.StatusCode == HttpStatusCode.Forbidden
            ? "Bot không đủ quyền gỡ Role trên Discord (HTTP 403). Vui lòng kiểm tra: (1) Bot có quyền 'Manage Roles', và (2) Role của Bot nằm CAO HƠN Role bang trong Server Settings → Roles."
            : $"Discord API báo lỗi khi gỡ Role (HTTP {(int)resp.StatusCode}).";

        return new DiscordRoleActionResult(false, false, message);
    }

    /// <summary>
    /// Gán lại Role bang cho thành viên trên server Discord khi khôi phục hồ sơ.
    /// </summary>
    public async Task<DiscordRoleActionResult> AddGuildRoleToMemberAsync(
        ulong guildId,
        ulong discordUserId,
        ulong roleId,
        CancellationToken ct = default)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return new DiscordRoleActionResult(false, false, "Chưa cấu hình DiscordWeb:BotToken.");
        }

        var client = httpClientFactory.CreateClient("DiscordApi");
        using var req = new HttpRequestMessage(
            HttpMethod.Put,
            $"https://discord.com/api/v10/guilds/{guildId}/members/{discordUserId}/roles/{roleId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
        req.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", "GuildHub Web Admin restored member to guild role");

        using var resp = await client.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
        {
            logger.LogInformation("Đã gán lại Role {RoleId} cho user {UserId} trên Guild {GuildId}.", roleId, discordUserId, guildId);
            return new DiscordRoleActionResult(true, false);
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return new DiscordRoleActionResult(false, true, "Thành viên không còn ở trong server Discord nên không thể gán lại Role.");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        logger.LogWarning("Không thể gán lại Role {RoleId} cho user {UserId} trên Guild {GuildId} (HTTP {Code}): {Body}",
            roleId, discordUserId, guildId, resp.StatusCode, body);

        var message = resp.StatusCode == HttpStatusCode.Forbidden
            ? "Bot thiếu quyền 'Manage Roles' hoặc vị trí Role của Bot thấp hơn Role bang nên chưa thể gán lại Role trên Discord."
            : $"Discord API báo lỗi khi gán lại Role (HTTP {(int)resp.StatusCode}).";

        return new DiscordRoleActionResult(false, false, message);
    }

    /// <summary>
    /// Đổi (hoặc xóa) Nickname của thành viên trực tiếp trên server Discord.
    /// Yêu cầu Bot có quyền Manage Nicknames và Role của Bot cao hơn người bị đổi (không đổi được chủ server).
    /// </summary>
    public async Task<DiscordRoleActionResult> UpdateMemberNicknameOnDiscordAsync(
        ulong guildId,
        ulong discordUserId,
        string? newNickname,
        CancellationToken ct = default)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return new DiscordRoleActionResult(false, false, "Chưa cấu hình DiscordWeb:BotToken.");
        }

        var trimmed = newNickname?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && trimmed.Length > 32)
        {
            return new DiscordRoleActionResult(false, false, "Nickname trên Discord không được vượt quá 32 ký tự.");
        }

        var client = httpClientFactory.CreateClient("DiscordApi");
        using var req = new HttpRequestMessage(
            HttpMethod.Patch,
            $"https://discord.com/api/v10/guilds/{guildId}/members/{discordUserId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
        req.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", "GuildHub Web Admin updated member nickname");
        req.Content = JsonContent.Create(new { nick = string.IsNullOrEmpty(trimmed) ? string.Empty : trimmed });

        using var resp = await client.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
        {
            logger.LogInformation("Đã đổi Nickname của user {UserId} trên Guild {GuildId} thành '{Nick}'.",
                discordUserId, guildId, string.IsNullOrEmpty(trimmed) ? "(xóa nick)" : trimmed);
            return new DiscordRoleActionResult(true, false);
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return new DiscordRoleActionResult(false, true, "Thành viên không còn trong server Discord nên không thể đổi nickname.");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        logger.LogWarning("Không thể đổi Nickname user {UserId} trên Guild {GuildId} (HTTP {Code}): {Body}",
            discordUserId, guildId, resp.StatusCode, body);

        var message = resp.StatusCode == HttpStatusCode.Forbidden
            ? "Bot không đủ quyền đổi Nickname trên Discord (HTTP 403). Lưu ý: Bot cần quyền 'Manage Nicknames', Role của Bot phải cao hơn thành viên đó và Discord không cho phép bất kỳ Bot nào đổi nickname của Chủ Server (Owner)."
            : $"Discord API báo lỗi khi đổi Nickname (HTTP {(int)resp.StatusCode}).";

        return new DiscordRoleActionResult(false, false, message);
    }

    public async Task<DiscordSyncResult> SyncGuildMembersAsync(ulong guildId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        if (LastSyncCooldowns.TryGetValue(guildId, out var lastRun))
        {
            var elapsed = now - lastRun;
            if (elapsed < SyncCooldown)
            {
                var remaining = Math.Ceiling((SyncCooldown - elapsed).TotalSeconds);
                return new DiscordSyncResult(
                    Success: false,
                    SyncedCount: 0,
                    NewlyLinkedFromEvents: 0,
                    UsedSignupFallback: true,
                    Message: $"Vừa đồng bộ xong. Vui lòng đợi {remaining} giây rồi thử lại để tránh giới hạn Discord API.",
                    SyncedAtUtc: lastRun);
            }
        }

        LastSyncCooldowns[guildId] = now;
        var token = options.Value.BotToken;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var config = await db.GuildConfigs.FirstOrDefaultAsync(c => c.GuildId == guildId, ct);
        if (config is null)
        {
            config = new GuildConfig
            {
                GuildId = guildId,
                GuildName = $"Server #{guildId}",
                UpdatedAtUtc = now
            };
            db.GuildConfigs.Add(config);
        }

        // Lấy sẵn bản đồ hệ phái mới nhất từ lịch sử điểm danh của Bot để tự động liên kết cho người chưa chọn phái trên Web
        var latestSignups = await db.EventSignups
            .AsNoTracking()
            .Where(s => s.WarEvent.GuildId == guildId)
            .OrderByDescending(s => s.UpdatedAtUtc)
            .Select(s => new { s.DiscordUserId, s.DisplayName, s.GameClass })
            .ToListAsync(ct);

        var latestClassByUser = new Dictionary<ulong, (string DisplayName, GameClass? GameClass)>();
        foreach (var s in latestSignups)
        {
            if (!latestClassByUser.ContainsKey(s.DiscordUserId))
            {
                latestClassByUser[s.DiscordUserId] = (s.DisplayName, s.GameClass);
            }
            else if (latestClassByUser[s.DiscordUserId].GameClass is null && s.GameClass.HasValue)
            {
                latestClassByUser[s.DiscordUserId] = (latestClassByUser[s.DiscordUserId].DisplayName, s.GameClass);
            }
        }

        // Thử kéo toàn bộ thành viên từ Discord REST API (cần Server Members Intent bật trong Discord Developer Portal)
        List<DiscordGuildMemberDto>? remoteMembers = null;
        var missingMembersIntent = false;

        if (!string.IsNullOrWhiteSpace(token))
        {
            var fetchResult = await FetchAllGuildMembersAsync(guildId, token, ct);
            remoteMembers = fetchResult.Members;
            missingMembersIntent = fetchResult.MissingIntent;
        }

        var existingMembers = await db.GuildMembers
            .Where(m => m.GuildId == guildId)
            .ToListAsync(ct);

        var existingByDiscordId = existingMembers
            .Where(m => m.DiscordUserId.HasValue)
            .ToDictionary(m => m.DiscordUserId!.Value);

        // Bản đồ Role Discord -> Hệ phái (nếu Admin đã cấu hình trong trang Cấu hình)
        var classConfigs = await db.GuildClassConfigs
            .AsNoTracking()
            .Where(c => c.GuildId == guildId && c.DiscordRoleId != null)
            .ToListAsync(ct);

        var classByRoleId = classConfigs
            .GroupBy(c => c.DiscordRoleId!.Value.ToString())
            .ToDictionary(g => g.Key, g => g.First().GameClass);

        int syncedCount = 0;
        int newlyLinked = 0;

        if (remoteMembers is not null)
        {
            var seenDiscordIds = new HashSet<ulong>();
            var requiredRoleStr = config.MemberRoleId?.ToString();

            foreach (var rm in remoteMembers)
            {
                if (rm.User is null || rm.User.Bot || !ulong.TryParse(rm.User.Id, out var uid))
                {
                    continue;
                }

                // Nếu admin đã cấu hình Role bang cụ thể: chỉ sync những người có Role đó HOẶC đã tồn tại trong DB (để đánh dấu Mất role)
                var hasRole = string.IsNullOrEmpty(requiredRoleStr)
                    || (rm.Roles is not null && rm.Roles.Contains(requiredRoleStr));

                if (!string.IsNullOrEmpty(requiredRoleStr) && !hasRole && !existingByDiscordId.ContainsKey(uid))
                {
                    continue;
                }

                seenDiscordIds.Add(uid);

                // DisplayName giữ tên hiển thị gốc trên Discord; IngameNickname tự đồng bộ từ Server Nickname (rm.Nick)
                var accountDisplayName = !string.IsNullOrWhiteSpace(rm.User.GlobalName)
                    ? rm.User.GlobalName
                    : rm.User.Username;
                var serverNick = !string.IsNullOrWhiteSpace(rm.Nick) ? Truncate(rm.Nick.Trim(), 100) : null;

                var avatarUrl = !string.IsNullOrWhiteSpace(rm.User.Avatar)
                    ? $"https://cdn.discordapp.com/avatars/{rm.User.Id}/{rm.User.Avatar}.png?size=128"
                    : null;

                // Suy luận hệ phái ưu tiên từ Role hệ phái trên Discord -> nếu không có thì lấy từ lịch sử điểm danh Bot
                GameClass? roleMatchedClass = null;
                if (rm.Roles is not null && classByRoleId.Count > 0)
                {
                    foreach (var rId in rm.Roles)
                    {
                        if (classByRoleId.TryGetValue(rId, out var matched))
                        {
                            roleMatchedClass = matched;
                            break;
                        }
                    }
                }

                if (existingByDiscordId.TryGetValue(uid, out var existing))
                {
                    existing.DiscordUsername = Truncate(rm.User.Username, 100);
                    existing.DisplayName = Truncate(accountDisplayName, 100);
                    existing.IngameNickname = serverNick;
                    existing.AvatarUrl = avatarUrl;
                    existing.HasGuildRole = hasRole;
                    existing.UpdatedAtUtc = now;

                    // Nếu thành viên từng bị Remove trên Web nhưng đã được cấp lại Role bang trên Discord -> tự khôi phục
                    if (!string.IsNullOrEmpty(requiredRoleStr) && hasRole && existing.IsRemoved)
                    {
                        existing.IsRemoved = false;
                        existing.RemovedAtUtc = null;
                    }

                    if (roleMatchedClass.HasValue)
                    {
                        if (existing.GameClass is null) newlyLinked++;
                        existing.GameClass = roleMatchedClass;
                    }
                    else if (existing.GameClass is null &&
                             latestClassByUser.TryGetValue(uid, out var inferred) &&
                             inferred.GameClass.HasValue)
                    {
                        existing.GameClass = inferred.GameClass;
                        newlyLinked++;
                    }
                }
                else
                {
                    GameClass? inferredClass = roleMatchedClass;
                    if (inferredClass is null &&
                        latestClassByUser.TryGetValue(uid, out var inferred) &&
                        inferred.GameClass.HasValue)
                    {
                        inferredClass = inferred.GameClass;
                    }

                    if (inferredClass.HasValue)
                    {
                        newlyLinked++;
                    }

                    db.GuildMembers.Add(new GuildMember
                    {
                        GuildId = guildId,
                        DiscordUserId = uid,
                        DiscordUsername = Truncate(rm.User.Username, 100),
                        DisplayName = Truncate(accountDisplayName, 100),
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

            // Đánh dấu những người đã rời khỏi server Discord là Mất role
            foreach (var existing in existingMembers.Where(m => m.DiscordUserId.HasValue && !m.IsExternal))
            {
                if (!seenDiscordIds.Contains(existing.DiscordUserId!.Value))
                {
                    existing.HasGuildRole = false;
                    existing.UpdatedAtUtc = now;
                }
            }

            config.LastSyncedAtUtc = now;
            config.LastSyncedMemberCount = syncedCount;
            config.UpdatedAtUtc = now;

            await db.SaveChangesAsync(ct);

            logger.LogInformation("Đồng bộ thành công {Count} thành viên từ Discord cho Guild {GuildId} (tự link {Linked} phái).",
                syncedCount, guildId, newlyLinked);

            return new DiscordSyncResult(
                Success: true,
                SyncedCount: syncedCount,
                NewlyLinkedFromEvents: newlyLinked,
                UsedSignupFallback: false,
                Message: $"Đồng bộ thành công {syncedCount} thành viên từ Discord" +
                         (newlyLinked > 0 ? $" (tự liên kết môn phái cho {newlyLinked} người)." : "."),
                SyncedAtUtc: now);
        }

        // Fallback an toàn: nếu chưa bật Server Members Intent -> đồng bộ thành viên từ lịch sử đăng ký của Bot!
        foreach (var (uid, info) in latestClassByUser)
        {
            if (existingByDiscordId.TryGetValue(uid, out var existing))
            {
                if (existing.GameClass is null && info.GameClass.HasValue)
                {
                    existing.GameClass = info.GameClass;
                    existing.UpdatedAtUtc = now;
                    newlyLinked++;
                }
            }
            else
            {
                db.GuildMembers.Add(new GuildMember
                {
                    GuildId = guildId,
                    DiscordUserId = uid,
                    DiscordUsername = uid.ToString(),
                    DisplayName = Truncate(info.DisplayName, 100),
                    GameClass = info.GameClass,
                    HasGuildRole = true,
                    IsRemoved = false,
                    IsExternal = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                if (info.GameClass.HasValue)
                {
                    newlyLinked++;
                }
            }

            syncedCount++;
        }

        config.LastSyncedAtUtc = now;
        config.LastSyncedMemberCount = syncedCount;
        config.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);

        var hint = missingMembersIntent
            ? $"Đã nhập {syncedCount} thành viên từ lịch sử điểm danh của Bot. Để đồng bộ toàn bộ server Discord (kèm Avatar & Role), vui lòng bật 'Server Members Intent' trong Discord Developer Portal → Bot."
            : $"Đã đồng bộ {syncedCount} thành viên từ dữ liệu điểm danh.";

        return new DiscordSyncResult(
            Success: true,
            SyncedCount: syncedCount,
            NewlyLinkedFromEvents: newlyLinked,
            UsedSignupFallback: true,
            Message: hint,
            SyncedAtUtc: now);
    }

    private async Task<(List<DiscordGuildMemberDto>? Members, bool MissingIntent)> FetchAllGuildMembersAsync(
        ulong guildId, string botToken, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("DiscordApi");
        var all = new List<DiscordGuildMemberDto>();
        string? after = null;

        for (var page = 0; page < 10; page++)
        {
            var url = $"https://discord.com/api/v10/guilds/{guildId}/members?limit=1000" +
                      (after is not null ? $"&after={after}" : string.Empty);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);

            using var resp = await client.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Discord API trả 403 khi lấy danh sách thành viên Guild {GuildId} (có thể chưa bật Server Members Intent): {Body}",
                    guildId, body);
                return (null, true);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Lỗi lấy danh sách thành viên Guild {GuildId} (HTTP {Code}): {Body}",
                    guildId, resp.StatusCode, body);
                return (null, false);
            }

            var batch = await resp.Content.ReadFromJsonAsync<List<DiscordGuildMemberDto>>(cancellationToken: ct) ?? [];
            all.AddRange(batch);

            if (batch.Count < 1000)
            {
                break;
            }

            after = batch[^1].User?.Id;
            if (string.IsNullOrEmpty(after))
            {
                break;
            }
        }

        return (all, false);
    }

    private static string Truncate(string input, int maxLen) =>
        input.Length <= maxLen ? input : input[..maxLen];

    private sealed class DiscordRoleDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("color")]
        public int Color { get; set; }

        [JsonPropertyName("position")]
        public int Position { get; set; }

        [JsonPropertyName("managed")]
        public bool Managed { get; set; }
    }

    private sealed class DiscordChannelDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("position")]
        public int Position { get; set; }

        [JsonPropertyName("parent_id")]
        public string? ParentId { get; set; }
    }

    private sealed class DiscordGuildMemberDto
    {
        [JsonPropertyName("user")]
        public DiscordUserMiniDto? User { get; set; }

        [JsonPropertyName("nick")]
        public string? Nick { get; set; }

        [JsonPropertyName("roles")]
        public List<string>? Roles { get; set; }
    }

    private sealed class DiscordUserMiniDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("global_name")]
        public string? GlobalName { get; set; }

        [JsonPropertyName("avatar")]
        public string? Avatar { get; set; }

        [JsonPropertyName("bot")]
        public bool Bot { get; set; }
    }
}
