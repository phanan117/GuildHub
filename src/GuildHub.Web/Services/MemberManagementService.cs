using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;
using GuildHub.Core.Metadata;
using GuildHub.Web.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuildHub.Web.Services;

public sealed record ResolvedClassConfig(
    GameClass GameClass,
    ClassRole Role,
    string DisplayName,
    string IconFileName,
    ulong? DiscordRoleId,
    string? DiscordRoleName,
    string HexColor,
    string SoftBgHex,
    string BorderHex,
    bool IsCustom = false,
    int SortOrder = 0);

public enum MemberLinkFilter
{
    ActiveAll = 0,
    Linked = 1,
    Unlinked = 2,
    Removed = 3
}

public enum MemberRoleFilter
{
    All = 0,
    HasRole = 1,
    NoRole = 2
}

public sealed record MemberQuery(
    ulong GuildId,
    string? Search = null,
    GameClass? GameClass = null,
    MemberLinkFilter StatusFilter = MemberLinkFilter.ActiveAll,
    MemberRoleFilter RoleFilter = MemberRoleFilter.All,
    string SortBy = "name",
    bool SortDesc = false,
    int Page = 1,
    int PageSize = 25);

public sealed record PagedMembersResult(
    IReadOnlyList<GuildMember> Items,
    int TotalCount,
    int Page,
    int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public sealed class MemberManagementService(
    IDbContextFactory<GuildHubDbContext> dbFactory,
    IOptions<WebDiscordOptions> webOptions)
{
    public async Task<PagedMembersResult> QueryMembersAsync(MemberQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<GuildMember> q = db.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == query.GuildId);

        q = query.StatusFilter switch
        {
            MemberLinkFilter.Linked => q.Where(m => !m.IsRemoved && m.GameClass != null),
            MemberLinkFilter.Unlinked => q.Where(m => !m.IsRemoved && m.GameClass == null),
            MemberLinkFilter.Removed => q.Where(m => m.IsRemoved),
            _ => q.Where(m => !m.IsRemoved)
        };

        if (query.GameClass.HasValue)
        {
            q = q.Where(m => m.GameClass == query.GameClass.Value);
        }

        if (query.RoleFilter == MemberRoleFilter.HasRole)
        {
            q = q.Where(m => m.HasGuildRole);
        }
        else if (query.RoleFilter == MemberRoleFilter.NoRole)
        {
            q = q.Where(m => !m.HasGuildRole);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = $"%{query.Search.Trim()}%";
            q = q.Where(m =>
                EF.Functions.ILike(m.DisplayName, term) ||
                EF.Functions.ILike(m.DiscordUsername, term) ||
                (m.IngameNickname != null && EF.Functions.ILike(m.IngameNickname, term)));
        }

        q = (query.SortBy.ToLowerInvariant(), query.SortDesc) switch
        {
            ("nickname", false) => q.OrderBy(m => m.IngameNickname ?? string.Empty).ThenBy(m => m.DisplayName),
            ("nickname", true) => q.OrderByDescending(m => m.IngameNickname ?? string.Empty).ThenBy(m => m.DisplayName),
            ("username", false) => q.OrderBy(m => m.DiscordUsername),
            ("username", true) => q.OrderByDescending(m => m.DiscordUsername),
            ("class", false) => q.OrderBy(m => m.GameClass == null ? 99 : (int)m.GameClass).ThenBy(m => m.DisplayName),
            ("class", true) => q.OrderByDescending(m => m.GameClass == null ? -1 : (int)m.GameClass).ThenBy(m => m.DisplayName),
            ("status", false) => q.OrderBy(m => m.GameClass == null ? 1 : 0).ThenBy(m => m.DisplayName),
            ("status", true) => q.OrderByDescending(m => m.GameClass == null ? 1 : 0).ThenBy(m => m.DisplayName),
            ("role", false) => q.OrderByDescending(m => m.HasGuildRole).ThenBy(m => m.DisplayName),
            ("role", true) => q.OrderBy(m => m.HasGuildRole).ThenBy(m => m.DisplayName),
            (_, true) => q.OrderByDescending(m => m.DisplayName),
            _ => q.OrderBy(m => m.DisplayName)
        };

        var total = await q.CountAsync(ct);
        var safePageSize = Math.Clamp(query.PageSize, 10, 200);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)safePageSize));
        var safePage = Math.Clamp(query.Page, 1, totalPages);

        var items = await q
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        return new PagedMembersResult(items, total, safePage, safePageSize);
    }

    public async Task UpdateNicknameAsync(ulong guildId, long memberId, string? nickname, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.GuildId == guildId, ct);
        if (member is null)
        {
            return;
        }

        var trimmed = nickname?.Trim();
        member.IngameNickname = string.IsNullOrWhiteSpace(trimmed)
            ? null
            : (trimmed.Length <= 100 ? trimmed : trimmed[..100]);
        member.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateClassAsync(ulong guildId, long memberId, GameClass? gameClass, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.GuildId == guildId, ct);
        if (member is null)
        {
            return;
        }

        member.GameClass = gameClass;
        member.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<GuildMember> AddManualMemberAsync(
        ulong guildId,
        string displayName,
        string? discordUsername,
        string? ingameNickname,
        GameClass? gameClass,
        bool isExternal,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var entity = new GuildMember
        {
            GuildId = guildId,
            DiscordUserId = null,
            DisplayName = displayName.Trim(),
            DiscordUsername = string.IsNullOrWhiteSpace(discordUsername) ? "manual" : discordUsername.Trim(),
            IngameNickname = string.IsNullOrWhiteSpace(ingameNickname) ? null : ingameNickname.Trim(),
            GameClass = gameClass,
            HasGuildRole = !isExternal,
            IsExternal = isExternal,
            IsRemoved = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.GuildMembers.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<GuildMember> UpsertDiscordMemberAsync(
        ulong guildId,
        ulong discordUserId,
        string discordUsername,
        string displayName,
        string? ingameNickname,
        string? avatarUrl,
        GameClass? gameClass,
        bool hasGuildRole,
        bool isExternal,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.GuildMembers
            .FirstOrDefaultAsync(m => m.GuildId == guildId && m.DiscordUserId == discordUserId, ct);

        if (existing is not null)
        {
            existing.DiscordUsername = discordUsername.Trim();
            existing.DisplayName = displayName.Trim();
            existing.IngameNickname = string.IsNullOrWhiteSpace(ingameNickname) ? null : ingameNickname.Trim();
            existing.AvatarUrl = avatarUrl;
            existing.GameClass = gameClass;
            existing.HasGuildRole = hasGuildRole;
            existing.IsExternal = isExternal;
            existing.IsRemoved = false;
            existing.RemovedAtUtc = null;
            existing.UpdatedAtUtc = now;

            await db.SaveChangesAsync(ct);
            return existing;
        }

        var entity = new GuildMember
        {
            GuildId = guildId,
            DiscordUserId = discordUserId,
            DiscordUsername = discordUsername.Trim(),
            DisplayName = displayName.Trim(),
            IngameNickname = string.IsNullOrWhiteSpace(ingameNickname) ? null : ingameNickname.Trim(),
            AvatarUrl = avatarUrl,
            GameClass = gameClass,
            HasGuildRole = hasGuildRole,
            IsExternal = isExternal,
            IsRemoved = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.GuildMembers.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task SoftRemoveAsync(ulong guildId, long memberId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.GuildId == guildId, ct);
        if (member is null)
        {
            return;
        }

        member.IsRemoved = true;
        member.HasGuildRole = false;
        member.RemovedAtUtc = DateTimeOffset.UtcNow;
        member.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RestoreAsync(ulong guildId, long memberId, bool hasGuildRole, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.GuildId == guildId, ct);
        if (member is null)
        {
            return;
        }

        member.IsRemoved = false;
        member.HasGuildRole = hasGuildRole;
        member.RemovedAtUtc = null;
        member.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeletePermanentlyAsync(ulong guildId, long memberId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.GuildId == guildId, ct);
        if (member is null)
        {
            return;
        }

        db.GuildMembers.Remove(member);
        await db.SaveChangesAsync(ct);
    }

    public async Task<GuildConfig?> GetGuildConfigAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.GuildConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId, ct);
    }

    public async Task UpdateMemberRoleConfigAsync(ulong guildId, ulong? roleId, string? roleName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var config = await db.GuildConfigs.FirstOrDefaultAsync(c => c.GuildId == guildId, ct);
        if (config is null)
        {
            config = new GuildConfig
            {
                GuildId = guildId,
                GuildName = $"Server #{guildId}"
            };
            db.GuildConfigs.Add(config);
        }

        config.MemberRoleId = roleId;
        config.MemberRoleName = roleId.HasValue ? roleName : null;
        config.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ResolvedClassConfig>> GetResolvedClassConfigsAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var savedList = await db.GuildClassConfigs
            .AsNoTracking()
            .Where(c => c.GuildId == guildId)
            .ToListAsync(ct);

        var saved = savedList.ToDictionary(c => c.GameClass);
        var result = new List<ResolvedClassConfig>();

        // 1. 8 hệ phái mặc định (ghi đè bằng cấu hình tùy chỉnh nếu có)
        for (var i = 0; i < ClassMetadata.All.Count; i++)
        {
            var meta = ClassMetadata.All[i];
            if (saved.TryGetValue(meta.Class, out var custom))
            {
                var hex = NormalizeHexColor(custom.HexColor, meta.HexColor);
                result.Add(new ResolvedClassConfig(
                    GameClass: meta.Class,
                    Role: custom.Role,
                    DisplayName: string.IsNullOrWhiteSpace(custom.DisplayName) ? meta.DisplayName : custom.DisplayName,
                    IconFileName: string.IsNullOrWhiteSpace(custom.IconFileName) ? meta.FileName : custom.IconFileName,
                    DiscordRoleId: custom.DiscordRoleId,
                    DiscordRoleName: custom.DiscordRoleName,
                    HexColor: hex,
                    SoftBgHex: $"{hex}1F",
                    BorderHex: $"{hex}55",
                    IsCustom: false,
                    SortOrder: custom.SortOrder > 0 ? custom.SortOrder : i + 1));
            }
            else
            {
                result.Add(new ResolvedClassConfig(
                    GameClass: meta.Class,
                    Role: meta.Role,
                    DisplayName: meta.DisplayName,
                    IconFileName: meta.FileName,
                    DiscordRoleId: null,
                    DiscordRoleName: null,
                    HexColor: meta.HexColor,
                    SoftBgHex: $"{meta.HexColor}1F",
                    BorderHex: $"{meta.HexColor}55",
                    IsCustom: false,
                    SortOrder: i + 1));
            }
        }

        // 2. Các hệ phái mới do Admin thêm cho server (IsCustom == true)
        foreach (var custom in savedList
            .Where(c => c.IsCustom && !ClassMetadata.TryGet(c.GameClass, out _))
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => (int)c.GameClass))
        {
            var hex = NormalizeHexColor(custom.HexColor, "#6366F1");
            result.Add(new ResolvedClassConfig(
                GameClass: custom.GameClass,
                Role: custom.Role,
                DisplayName: string.IsNullOrWhiteSpace(custom.DisplayName) ? $"Hệ phái #{(int)custom.GameClass}" : custom.DisplayName,
                IconFileName: string.IsNullOrWhiteSpace(custom.IconFileName) ? "dps.png" : custom.IconFileName,
                DiscordRoleId: custom.DiscordRoleId,
                DiscordRoleName: custom.DiscordRoleName,
                HexColor: hex,
                SoftBgHex: $"{hex}1F",
                BorderHex: $"{hex}55",
                IsCustom: true,
                SortOrder: custom.SortOrder > 0 ? custom.SortOrder : (int)custom.GameClass));
        }

        return result.OrderBy(c => c.SortOrder).ThenBy(c => (int)c.GameClass).ToList();
    }

    public async Task SaveClassConfigsAsync(
        ulong guildId,
        IReadOnlyList<GuildClassConfig> incoming,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.GuildClassConfigs
            .Where(c => c.GuildId == guildId)
            .ToDictionaryAsync(c => c.GameClass, ct);

        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < incoming.Count; i++)
        {
            var item = incoming[i];
            var hasMeta = ClassMetadata.TryGet(item.GameClass, out var meta);

            var fallbackName = hasMeta ? meta.DisplayName : $"Hệ phái #{(int)item.GameClass}";
            var fallbackIcon = hasMeta ? meta.FileName : "dps.png";
            var fallbackHex = hasMeta ? meta.HexColor : "#6366F1";

            var cleanName = string.IsNullOrWhiteSpace(item.DisplayName) ? fallbackName : item.DisplayName.Trim();
            if (cleanName.Length > 80) cleanName = cleanName[..80];

            var cleanIcon = SanitizeIconRelativePath(item.IconFileName) ?? fallbackIcon;
            if (cleanIcon.Length > 120) cleanIcon = fallbackIcon;

            var cleanHex = NormalizeHexColor(item.HexColor, fallbackHex);

            if (existing.TryGetValue(item.GameClass, out var row))
            {
                row.DisplayName = cleanName;
                row.Role = item.Role;
                row.HexColor = cleanHex;
                row.IconFileName = cleanIcon;
                row.DiscordRoleId = item.DiscordRoleId;
                row.DiscordRoleName = item.DiscordRoleId.HasValue ? item.DiscordRoleName : null;
                row.SortOrder = item.SortOrder > 0 ? item.SortOrder : i + 1;
                row.UpdatedAtUtc = now;
            }
            else
            {
                db.GuildClassConfigs.Add(new GuildClassConfig
                {
                    GuildId = guildId,
                    GameClass = item.GameClass,
                    DisplayName = cleanName,
                    Role = item.Role,
                    HexColor = cleanHex,
                    IconFileName = cleanIcon,
                    DiscordRoleId = item.DiscordRoleId,
                    DiscordRoleName = item.DiscordRoleId.HasValue ? item.DiscordRoleName : null,
                    SortOrder = item.SortOrder > 0 ? item.SortOrder : i + 1,
                    IsCustom = !hasMeta,
                    UpdatedAtUtc = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<(bool Success, ResolvedClassConfig? Created, string? Error)> AddCustomClassAsync(
        ulong guildId,
        string displayName,
        ClassRole role,
        string hexColor,
        ulong? discordRoleId,
        string? discordRoleName,
        string? uploadedFileName = null,
        Stream? uploadedIconStream = null,
        CancellationToken ct = default)
    {
        var cleanName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            return (false, null, "Vui lòng nhập tên hệ phái.");
        }

        if (cleanName.Length > 80) cleanName = cleanName[..80];
        var cleanHex = NormalizeHexColor(hexColor, "#6366F1");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existingClasses = await db.GuildClassConfigs
            .Where(c => c.GuildId == guildId)
            .Select(c => new { Id = (int)c.GameClass, c.SortOrder })
            .ToListAsync(ct);

        var nextId = Math.Max(8, existingClasses.Select(x => x.Id).DefaultIfEmpty(8).Max()) + 1;
        var nextOrder = Math.Max(8, existingClasses.Select(x => x.SortOrder).DefaultIfEmpty(8).Max()) + 1;
        var newClassEnum = (GameClass)nextId;

        var defaultIcon = role switch
        {
            ClassRole.Tank => "tanker.png",
            ClassRole.Healer => "heal.png",
            _ => "dps.png"
        };

        var iconPath = defaultIcon;
        if (!string.IsNullOrWhiteSpace(uploadedFileName) && uploadedIconStream is not null)
        {
            var dir = ResolveIconDirectory(webOptions.Value.IconDirectory);
            var ext = Path.GetExtension(uploadedFileName).ToLowerInvariant();
            if (dir is not null && ext is ".png" or ".webp")
            {
                var guildFolder = Path.Combine(dir, "custom", guildId.ToString());
                Directory.CreateDirectory(guildFolder);
                var safeFile = $"custom_{nextId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{ext}";
                var destPath = Path.Combine(guildFolder, safeFile);

                await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await uploadedIconStream.CopyToAsync(fs, ct);
                iconPath = $"custom/{guildId}/{safeFile}";
            }
        }

        var entity = new GuildClassConfig
        {
            GuildId = guildId,
            GameClass = newClassEnum,
            DisplayName = cleanName,
            Role = role,
            HexColor = cleanHex,
            IconFileName = iconPath,
            DiscordRoleId = discordRoleId,
            DiscordRoleName = discordRoleId.HasValue ? discordRoleName : null,
            SortOrder = nextOrder,
            IsCustom = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        db.GuildClassConfigs.Add(entity);
        await db.SaveChangesAsync(ct);

        var resolved = new ResolvedClassConfig(
            GameClass: entity.GameClass,
            Role: entity.Role,
            DisplayName: entity.DisplayName,
            IconFileName: entity.IconFileName,
            DiscordRoleId: entity.DiscordRoleId,
            DiscordRoleName: entity.DiscordRoleName,
            HexColor: cleanHex,
            SoftBgHex: $"{cleanHex}1F",
            BorderHex: $"{cleanHex}55",
            IsCustom: true,
            SortOrder: entity.SortOrder);

        return (true, resolved, null);
    }

    public async Task<(bool Success, string? Error)> DeleteCustomClassAsync(
        ulong guildId,
        GameClass gameClass,
        CancellationToken ct = default)
    {
        if (ClassMetadata.TryGet(gameClass, out _))
        {
            return (false, "Không thể xóa 8 hệ phái mặc định của hệ thống.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.GuildClassConfigs
            .FirstOrDefaultAsync(c => c.GuildId == guildId && c.GameClass == gameClass && c.IsCustom, ct);

        if (entity is null)
        {
            return (false, "Không tìm thấy hệ phái cần xóa.");
        }

        // Hủy gán hệ phái này ở các thành viên đang chọn để tránh dữ liệu mồ côi
        var affectedMembers = await db.GuildMembers
            .Where(m => m.GuildId == guildId && m.GameClass == gameClass)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var member in affectedMembers)
        {
            member.GameClass = null;
            member.UpdatedAtUtc = now;
        }

        var oldIcon = entity.IconFileName;
        db.GuildClassConfigs.Remove(entity);
        await db.SaveChangesAsync(ct);

        var dir = ResolveIconDirectory(webOptions.Value.IconDirectory);
        if (dir is not null)
        {
            DeleteCustomIconIfSafe(dir, guildId, oldIcon);
        }

        return (true, null);
    }

    public static string NormalizeHexColor(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;
        var s = input.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        if (s.Length == 7 && s.Skip(1).All(Uri.IsHexDigit))
        {
            return s.ToUpperInvariant();
        }

        return fallback;
    }

    /// <summary>
    /// Upload icon riêng lẻ cho đúng 1 hệ phái của server (lưu tại Icon/custom/{guildId}/{class}_{ts}.png)
    /// để không ảnh hưởng các file icon hệ thống và tự phá cache trình duyệt.
    /// </summary>
    public async Task<(bool Success, string? RelativePath, string? Error)> UploadClassIconAsync(
        ulong guildId,
        GameClass gameClass,
        string originalFileName,
        Stream content,
        CancellationToken ct = default)
    {
        var dir = ResolveIconDirectory(webOptions.Value.IconDirectory);
        if (dir is null)
        {
            return (false, null, "Không tìm thấy thư mục Icon trên máy chủ.");
        }

        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (ext is not (".png" or ".webp"))
        {
            return (false, null, "Chỉ hỗ trợ định dạng ảnh .png hoặc .webp.");
        }

        var guildFolder = Path.Combine(dir, "custom", guildId.ToString());
        Directory.CreateDirectory(guildFolder);

        var classKey = gameClass.ToString().ToLowerInvariant();
        var fileName = $"{classKey}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{ext}";
        var destPath = Path.Combine(guildFolder, fileName);
        var relativePath = $"custom/{guildId}/{fileName}";

        await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fs, ct);
        }

        // Lưu ngay vào DB và dọn file custom cũ của chính hệ phái này (nếu có)
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.GuildClassConfigs
            .FirstOrDefaultAsync(c => c.GuildId == guildId && c.GameClass == gameClass, ct);

        string? oldRelativePath = existing?.IconFileName;
        var hasMeta = ClassMetadata.TryGet(gameClass, out var meta);

        if (existing is null)
        {
            db.GuildClassConfigs.Add(new GuildClassConfig
            {
                GuildId = guildId,
                GameClass = gameClass,
                DisplayName = hasMeta ? meta.DisplayName : $"Hệ phái #{(int)gameClass}",
                Role = hasMeta ? meta.Role : ClassRole.Dps,
                HexColor = hasMeta ? meta.HexColor : "#6366F1",
                IconFileName = relativePath,
                SortOrder = (int)gameClass,
                IsCustom = !hasMeta,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.IconFileName = relativePath;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        DeleteCustomIconIfSafe(dir, guildId, oldRelativePath);
        return (true, relativePath, null);
    }

    public async Task<string> ResetClassIconToDefaultAsync(ulong guildId, GameClass gameClass, CancellationToken ct = default)
    {
        var fallbackFile = ClassMetadata.TryGet(gameClass, out var meta) ? meta.FileName : "dps.png";
        var dir = ResolveIconDirectory(webOptions.Value.IconDirectory);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.GuildClassConfigs
            .FirstOrDefaultAsync(c => c.GuildId == guildId && c.GameClass == gameClass, ct);

        if (existing is not null)
        {
            var oldPath = existing.IconFileName;
            existing.IconFileName = fallbackFile;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (dir is not null)
            {
                DeleteCustomIconIfSafe(dir, guildId, oldPath);
            }
        }

        return fallbackFile;
    }

    private static void DeleteCustomIconIfSafe(string iconRootDir, ulong guildId, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        var expectedPrefix = $"custom/{guildId}/";
        if (!relativePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var fullPath = Path.Combine(iconRootDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // Bỏ qua nếu file đang bận đọc
        }
    }

    private static string? SanitizeIconRelativePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().Replace('\\', '/');
        if (normalized.Contains("..") || normalized.StartsWith('/')) return null;

        var ext = Path.GetExtension(normalized).ToLowerInvariant();
        if (ext is not (".png" or ".webp")) return null;

        return normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/')
            ? normalized
            : null;
    }

    private static string? ResolveIconDirectory(string configuredPath)
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
}
