using GuildHub.Core.Metadata;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Discord;
using GuildHub.Bot.Configuration;
using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GuildHub.Bot.Services;

public sealed record IconSyncResult(int Uploaded, int Updated, int Unchanged, int MissingFiles, string ResolvedDirectory);

public sealed class IconSyncService(
    IDbContextFactory<GuildHubDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<BotOptions> options,
    ILogger<IconSyncService> logger)
{
    private readonly ConcurrentDictionary<GameClass, (string Name, ulong Id)> _classCache = new();
    private readonly ConcurrentDictionary<UiIcon, (string Name, ulong Id)> _uiCache = new();

    public async Task LoadCacheFromDatabaseAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var classEntries = await db.ClassEmojis.AsNoTracking().ToListAsync(ct);
        foreach (var entry in classEntries)
        {
            _classCache[entry.GameClass] = (entry.EmojiName, entry.EmojiId);
        }

        var uiEntries = await db.UiEmojis.AsNoTracking().ToListAsync(ct);
        foreach (var entry in uiEntries)
        {
            _uiCache[entry.UiIcon] = (entry.EmojiName, entry.EmojiId);
        }

        logger.LogInformation(
            "Đã nạp {ClassCount} emoji hệ phái và {UiCount} emoji giao diện từ PostgreSQL vào bộ nhớ đệm.",
            classEntries.Count, uiEntries.Count);
    }

    public string GetEmojiMarkup(GameClass gameClass)
    {
        if (_classCache.TryGetValue(gameClass, out var emoji))
        {
            return $"<:{emoji.Name}:{emoji.Id}>";
        }

        return ClassMetadata.Get(gameClass).FallbackEmoji;
    }

    public IEmote GetEmote(GameClass gameClass)
    {
        if (_classCache.TryGetValue(gameClass, out var emoji) &&
            Emote.TryParse($"<:{emoji.Name}:{emoji.Id}>", out var parsed))
        {
            return parsed;
        }

        return new Emoji(ClassMetadata.Get(gameClass).FallbackEmoji);
    }

    public string GetUiEmojiMarkup(UiIcon icon)
    {
        if (_uiCache.TryGetValue(icon, out var emoji))
        {
            return $"<:{emoji.Name}:{emoji.Id}>";
        }

        return UiIconMetadata.Get(icon).FallbackEmoji;
    }

    public IEmote GetUiEmote(UiIcon icon)
    {
        if (_uiCache.TryGetValue(icon, out var emoji) &&
            Emote.TryParse($"<:{emoji.Name}:{emoji.Id}>", out var parsed))
        {
            return parsed;
        }

        return new Emoji(UiIconMetadata.Get(icon).FallbackEmoji);
    }

    public string GetStatusLabel(SignupStatus status) => status switch
    {
        SignupStatus.Confirmed => "✅ Tham gia",
        SignupStatus.Late => $"{GetUiEmojiMarkup(UiIcon.Clock)} Online trễ (Late)",
        SignupStatus.Bench => $"{GetUiEmojiMarkup(UiIcon.Bench)} Dự bị (Bench)",
        SignupStatus.Absent => $"{GetUiEmojiMarkup(UiIcon.Absent)} Vắng (Absence)",
        SignupStatus.NotParticipating => $"{GetUiEmojiMarkup(UiIcon.NotParticipating)} Không tham gia",
        _ => status.ToString()
    };

    public async Task<IconSyncResult> SyncApplicationEmojisAsync(ulong applicationId, CancellationToken ct = default)
    {
        var iconDir = ResolveIconDirectory(options.Value.IconDirectory);
        if (iconDir is null)
        {
            logger.LogWarning("Không tìm thấy thư mục icon '{ConfiguredPath}'. Bot sẽ dùng emoji mặc định.", options.Value.IconDirectory);
            await LoadCacheFromDatabaseAsync(ct);
            return new IconSyncResult(0, 0, 0, ClassMetadata.All.Count + UiIconMetadata.All.Count, options.Value.IconDirectory);
        }

        var http = httpClientFactory.CreateClient("DiscordApi");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", options.Value.Token);

        var existingRemote = await GetRemoteApplicationEmojisAsync(http, applicationId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var classDbMap = await db.ClassEmojis.ToDictionaryAsync(e => e.GameClass, ct);
        var uiDbMap = await db.UiEmojis.ToDictionaryAsync(e => e.UiIcon, ct);

        int uploaded = 0, updated = 0, unchanged = 0, missing = 0;

        // 1. Đồng bộ 8 icon hệ phái
        foreach (var meta in ClassMetadata.All)
        {
            var outcome = await SyncSingleIconAsync(
                http,
                applicationId,
                iconDir,
                meta.DisplayName,
                meta.FileName,
                meta.EmojiName,
                existingRemote,
                classDbMap.TryGetValue(meta.Class, out var dbEntry) ? (dbEntry.EmojiId, dbEntry.EmojiName, dbEntry.FileSha256) : null,
                onCreateOrLink: (name, id, sha) =>
                {
                    if (dbEntry is null)
                    {
                        db.ClassEmojis.Add(new ClassEmoji
                        {
                            GameClass = meta.Class,
                            EmojiName = name,
                            EmojiId = id,
                            FileSha256 = sha,
                            SyncedAtUtc = DateTimeOffset.UtcNow
                        });
                    }
                    else
                    {
                        dbEntry.EmojiName = name;
                        dbEntry.EmojiId = id;
                        dbEntry.FileSha256 = sha;
                        dbEntry.SyncedAtUtc = DateTimeOffset.UtcNow;
                    }

                    _classCache[meta.Class] = (name, id);
                },
                ct);

            ApplyOutcome(outcome, ref uploaded, ref updated, ref unchanged, ref missing);
        }

        // 2. Đồng bộ 10 icon giao diện trên form
        foreach (var meta in UiIconMetadata.All)
        {
            var outcome = await SyncSingleIconAsync(
                http,
                applicationId,
                iconDir,
                meta.DisplayName,
                meta.FileName,
                meta.EmojiName,
                existingRemote,
                uiDbMap.TryGetValue(meta.Icon, out var dbEntry) ? (dbEntry.EmojiId, dbEntry.EmojiName, dbEntry.FileSha256) : null,
                onCreateOrLink: (name, id, sha) =>
                {
                    if (dbEntry is null)
                    {
                        db.UiEmojis.Add(new UiEmoji
                        {
                            UiIcon = meta.Icon,
                            EmojiName = name,
                            EmojiId = id,
                            FileSha256 = sha,
                            SyncedAtUtc = DateTimeOffset.UtcNow
                        });
                    }
                    else
                    {
                        dbEntry.EmojiName = name;
                        dbEntry.EmojiId = id;
                        dbEntry.FileSha256 = sha;
                        dbEntry.SyncedAtUtc = DateTimeOffset.UtcNow;
                    }

                    _uiCache[meta.Icon] = (name, id);
                },
                ct);

            ApplyOutcome(outcome, ref uploaded, ref updated, ref unchanged, ref missing);
        }

        await db.SaveChangesAsync(ct);
        return new IconSyncResult(uploaded, updated, unchanged, missing, iconDir);
    }

    private enum SyncOutcome
    {
        Uploaded,
        Updated,
        Unchanged,
        Missing,
        Failed
    }

    private static void ApplyOutcome(
        SyncOutcome outcome,
        ref int uploaded,
        ref int updated,
        ref int unchanged,
        ref int missing)
    {
        switch (outcome)
        {
            case SyncOutcome.Uploaded: uploaded++; break;
            case SyncOutcome.Updated: updated++; break;
            case SyncOutcome.Unchanged: unchanged++; break;
            case SyncOutcome.Missing: missing++; break;
        }
    }

    private async Task<SyncOutcome> SyncSingleIconAsync(
        HttpClient http,
        ulong applicationId,
        string iconDir,
        string displayName,
        string fileName,
        string emojiName,
        List<DiscordEmojiDto> existingRemote,
        (ulong EmojiId, string EmojiName, string FileSha256)? dbEntry,
        Action<string, ulong, string> onCreateOrLink,
        CancellationToken ct)
    {
        var filePath = Path.Combine(iconDir, fileName);
        if (!File.Exists(filePath))
        {
            logger.LogWarning("Thiếu file icon cho '{DisplayName}': {Path}", displayName, filePath);
            return SyncOutcome.Missing;
        }

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));

        var remoteMatch = existingRemote.FirstOrDefault(e =>
            string.Equals(e.Name, emojiName, StringComparison.OrdinalIgnoreCase));

        if (dbEntry is not null &&
            remoteMatch is not null &&
            ulong.TryParse(remoteMatch.Id, out var remoteId) &&
            dbEntry.Value.EmojiId == remoteId &&
            string.Equals(dbEntry.Value.FileSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            onCreateOrLink(dbEntry.Value.EmojiName, dbEntry.Value.EmojiId, sha256);
            return SyncOutcome.Unchanged;
        }

        if (remoteMatch is not null && ulong.TryParse(remoteMatch.Id, out var oldRemoteId))
        {
            // Nếu hash file không đổi (hoặc DB đang lưu ID từ một bot khác dùng chung DB), tái sử dụng luôn emoji trên Discord App này
            if (dbEntry is null || string.Equals(dbEntry.Value.FileSha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                onCreateOrLink(remoteMatch.Name, oldRemoteId, sha256);
                return SyncOutcome.Unchanged;
            }

            await DeleteRemoteApplicationEmojiAsync(http, applicationId, oldRemoteId, ct);
        }

        var dataUri = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        var created = await CreateRemoteApplicationEmojiAsync(http, applicationId, emojiName, dataUri, ct);

        if (created is null || !ulong.TryParse(created.Id, out var newEmojiId))
        {
            logger.LogWarning("Không thể upload emoji '{Name}' lên Discord Application.", emojiName);
            return SyncOutcome.Failed;
        }

        onCreateOrLink(created.Name, newEmojiId, sha256);
        logger.LogInformation("Đã đồng bộ icon '{DisplayName}' ({File}) -> <:{Name}:{Id}>", displayName, fileName, created.Name, newEmojiId);
        return dbEntry is null ? SyncOutcome.Uploaded : SyncOutcome.Updated;
    }

    private static string? ResolveIconDirectory(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && Directory.Exists(configuredPath))
        {
            return configuredPath;
        }

        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
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

    private async Task<List<DiscordEmojiDto>> GetRemoteApplicationEmojisAsync(
        HttpClient http, ulong applicationId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://discord.com/api/v10/applications/{applicationId}/emojis", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Không thể lấy danh sách Application Emojis (HTTP {Code}): {Body}", response.StatusCode, body);
            return [];
        }

        var payload = await response.Content.ReadFromJsonAsync<DiscordEmojiListDto>(cancellationToken: ct);
        return payload?.Items ?? [];
    }

    private async Task<DiscordEmojiDto?> CreateRemoteApplicationEmojiAsync(
        HttpClient http, ulong applicationId, string name, string imageDataUri, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"https://discord.com/api/v10/applications/{applicationId}/emojis",
            new { name, image = imageDataUri },
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Lỗi upload Application Emoji '{Name}' (HTTP {Code}): {Body}", name, response.StatusCode, body);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DiscordEmojiDto>(cancellationToken: ct);
    }

    private async Task DeleteRemoteApplicationEmojiAsync(
        HttpClient http, ulong applicationId, ulong emojiId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(
            $"https://discord.com/api/v10/applications/{applicationId}/emojis/{emojiId}", ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Không thể xóa Application Emoji cũ {EmojiId} (HTTP {Code}).", emojiId, response.StatusCode);
        }
    }

    private sealed class DiscordEmojiListDto
    {
        [JsonPropertyName("items")]
        public List<DiscordEmojiDto> Items { get; set; } = [];
    }

    private sealed class DiscordEmojiDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}

