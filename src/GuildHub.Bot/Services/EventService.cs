using System.Collections.Concurrent;
using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace GuildHub.Bot.Services;

public sealed record SignupActionResult(
    bool Success,
    string? ErrorMessage = null,
    WarEvent? Event = null,
    EventSignup? Signup = null,
    string? Headline = null);

public sealed class EventService(IDbContextFactory<GuildHubDbContext> dbFactory)
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _eventLocks = new();

    private SemaphoreSlim GetLock(long eventId) => _eventLocks.GetOrAdd(eventId, _ => new SemaphoreSlim(1, 1));

    public async Task<WarEvent> CreateEventAsync(
        ulong guildId,
        ulong channelId,
        ulong leaderDiscordId,
        string title,
        DateTimeOffset onlineTimeUtc,
        DateTimeOffset closeTimeUtc,
        ulong? mentionRoleId,
        string? description,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entity = new WarEvent
        {
            GuildId = guildId,
            ChannelId = channelId,
            LeaderDiscordId = leaderDiscordId,
            EventType = EventType.GuildWar,
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            EventTimeUtc = onlineTimeUtc.ToUniversalTime(),
            CloseTimeUtc = closeTimeUtc.ToUniversalTime(),
            MentionRoleId = mentionRoleId,
            IsClosed = false,
            NextSignupOrder = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        db.WarEvents.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task AttachMessageIdAsync(long eventId, ulong messageId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ev = await db.WarEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null)
        {
            return;
        }

        ev.MessageId = messageId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<WarEvent?> GetEventWithSignupsAsync(long eventId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WarEvents
            .AsNoTracking()
            .Include(e => e.Signups)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);
    }

    public async Task<SignupActionResult> SignupWithClassAsync(
        long eventId,
        ulong userId,
        string defaultDisplayName,
        GameClass gameClass,
        CancellationToken ct = default)
    {
        var gate = GetLock(eventId);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ev = await db.WarEvents
                .Include(e => e.Signups)
                .FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (ev is null)
            {
                return new SignupActionResult(false, "Không tìm thấy sự kiện này.");
            }

            var now = DateTimeOffset.UtcNow;
            if (ev.IsEffectivelyClosed(now))
            {
                if (!ev.IsClosed)
                {
                    ev.IsClosed = true;
                    await db.SaveChangesAsync(ct);
                }

                return new SignupActionResult(false, "Form đăng ký đã đóng.");
            }

            var existing = ev.Signups.FirstOrDefault(s => s.DiscordUserId == userId);
            string headline;

            if (existing is null)
            {
                existing = new EventSignup
                {
                    WarEventId = ev.Id,
                    DiscordUserId = userId,
                    DisplayName = Truncate(defaultDisplayName, 100),
                    GameClass = gameClass,
                    Status = SignupStatus.Confirmed,
                    SignupOrder = ev.NextSignupOrder++,
                    OptionsEnabled = true,
                    SignedUpAtUtc = now,
                    UpdatedAtUtc = now
                };
                ev.Signups.Add(existing);
                headline = "Bạn đã đăng ký sự kiện thành công!";
            }
            else
            {
                existing.GameClass = gameClass;
                existing.Status = SignupStatus.Confirmed;
                existing.UpdatedAtUtc = now;
                headline = "Đã cập nhật hệ phái đăng ký của bạn!";
            }

            await db.SaveChangesAsync(ct);
            return new SignupActionResult(true, Event: ev, Signup: existing, Headline: headline);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SignupActionResult> SetStatusAsync(
        long eventId,
        ulong userId,
        SignupStatus targetStatus,
        CancellationToken ct = default)
    {
        var gate = GetLock(eventId);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ev = await db.WarEvents
                .Include(e => e.Signups)
                .FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (ev is null)
            {
                return new SignupActionResult(false, "Không tìm thấy sự kiện này.");
            }

            var now = DateTimeOffset.UtcNow;
            if (ev.IsEffectivelyClosed(now))
            {
                if (!ev.IsClosed)
                {
                    ev.IsClosed = true;
                    await db.SaveChangesAsync(ct);
                }

                return new SignupActionResult(false, "Form đăng ký đã đóng.");
            }

            var existing = ev.Signups.FirstOrDefault(s => s.DiscordUserId == userId);
            if (existing is null)
            {
                return new SignupActionResult(
                    false,
                    "Bạn chưa đăng ký sự kiện này. Vui lòng chọn hệ phái ở menu **Chọn class** trước khi bấm Online trễ / Dự bị / Vắng.");
            }

            string headline;
            if (existing.Status == targetStatus && existing.GameClass.HasValue)
            {
                existing.Status = SignupStatus.Confirmed;
                headline = "Đã chuyển trạng thái của bạn về Tham gia chính thức!";
            }
            else
            {
                existing.Status = targetStatus;
                headline = targetStatus switch
                {
                    SignupStatus.Late => "Đã cập nhật trạng thái: Online trễ (Late)!",
                    SignupStatus.Bench => "Đã cập nhật trạng thái: Dự bị (Bench)!",
                    SignupStatus.Absent => "Đã cập nhật trạng thái: Báo vắng (Absence)!",
                    _ => "Đã cập nhật trạng thái đăng ký!"
                };
            }

            existing.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return new SignupActionResult(true, Event: ev, Signup: existing, Headline: headline);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SignupActionResult> MarkNotParticipatingAsync(
        long eventId,
        ulong userId,
        string defaultDisplayName,
        CancellationToken ct = default)
    {
        var gate = GetLock(eventId);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ev = await db.WarEvents
                .Include(e => e.Signups)
                .FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (ev is null)
            {
                return new SignupActionResult(false, "Không tìm thấy sự kiện này.");
            }

            var now = DateTimeOffset.UtcNow;
            if (ev.IsEffectivelyClosed(now))
            {
                if (!ev.IsClosed)
                {
                    ev.IsClosed = true;
                    await db.SaveChangesAsync(ct);
                }

                return new SignupActionResult(false, "Form đăng ký đã đóng.");
            }

            var existing = ev.Signups.FirstOrDefault(s => s.DiscordUserId == userId);
            string headline;

            if (existing is null)
            {
                existing = new EventSignup
                {
                    WarEventId = ev.Id,
                    DiscordUserId = userId,
                    DisplayName = Truncate(defaultDisplayName, 100),
                    GameClass = null,
                    Status = SignupStatus.NotParticipating,
                    SignupOrder = ev.NextSignupOrder++,
                    OptionsEnabled = true,
                    SignedUpAtUtc = now,
                    UpdatedAtUtc = now
                };
                ev.Signups.Add(existing);
                headline = "Đã ghi nhận trạng thái: Không tham gia!";
            }
            else if (existing.Status == SignupStatus.NotParticipating && existing.GameClass.HasValue)
            {
                existing.Status = SignupStatus.Confirmed;
                existing.UpdatedAtUtc = now;
                headline = "Đã chuyển trạng thái của bạn về Tham gia chính thức!";
            }
            else
            {
                existing.Status = SignupStatus.NotParticipating;
                existing.UpdatedAtUtc = now;
                headline = "Đã cập nhật trạng thái: Không tham gia!";
            }

            await db.SaveChangesAsync(ct);
            return new SignupActionResult(true, Event: ev, Signup: existing, Headline: headline);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SignupActionResult> ToggleOptionsEnabledAsync(
        long eventId,
        ulong userId,
        CancellationToken ct = default)
    {
        var gate = GetLock(eventId);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ev = await db.WarEvents
                .Include(e => e.Signups)
                .FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (ev is null)
            {
                return new SignupActionResult(false, "Không tìm thấy sự kiện này.");
            }

            var existing = ev.Signups.FirstOrDefault(s => s.DiscordUserId == userId);
            if (existing is null)
            {
                return new SignupActionResult(false, "Bạn chưa đăng ký sự kiện này.");
            }

            existing.OptionsEnabled = !existing.OptionsEnabled;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var headline = existing.OptionsEnabled
                ? "Bạn đã bật tùy chọn chỉnh sửa đăng ký!"
                : "Bạn đã khóa tùy chọn chỉnh sửa đăng ký!";

            return new SignupActionResult(true, Event: ev, Signup: existing, Headline: headline);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SignupActionResult> UpdateDisplayNameAsync(
        long eventId,
        ulong userId,
        string newDisplayName,
        CancellationToken ct = default)
    {
        var trimmed = newDisplayName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new SignupActionResult(false, "Tên hiển thị không được để trống.");
        }

        var gate = GetLock(eventId);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ev = await db.WarEvents
                .Include(e => e.Signups)
                .FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (ev is null)
            {
                return new SignupActionResult(false, "Không tìm thấy sự kiện này.");
            }

            var existing = ev.Signups.FirstOrDefault(s => s.DiscordUserId == userId);
            if (existing is null)
            {
                return new SignupActionResult(false, "Bạn chưa đăng ký sự kiện này.");
            }

            if (!existing.OptionsEnabled)
            {
                return new SignupActionResult(false, "Vui lòng bật trạng thái **Enabled** trước khi chỉnh sửa.");
            }

            existing.DisplayName = Truncate(trimmed, 100);
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return new SignupActionResult(true, Event: ev, Signup: existing, Headline: "Đã cập nhật tên hiển thị!");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SignupActionResult> UpdateNoteAsync(
        long eventId,
        ulong userId,
        string? note,
        CancellationToken ct = default)
    {
        var gate = GetLock(eventId);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ev = await db.WarEvents
                .Include(e => e.Signups)
                .FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (ev is null)
            {
                return new SignupActionResult(false, "Không tìm thấy sự kiện này.");
            }

            var existing = ev.Signups.FirstOrDefault(s => s.DiscordUserId == userId);
            if (existing is null)
            {
                return new SignupActionResult(false, "Bạn chưa đăng ký sự kiện này.");
            }

            if (!existing.OptionsEnabled)
            {
                return new SignupActionResult(false, "Vui lòng bật trạng thái **Enabled** trước khi chỉnh sửa ghi chú.");
            }

            existing.Note = string.IsNullOrWhiteSpace(note) ? null : Truncate(note.Trim(), 300);
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return new SignupActionResult(true, Event: ev, Signup: existing, Headline: "Đã cập nhật ghi chú đăng ký!");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SignupActionResult> RemoveSignupAsync(
        long eventId,
        ulong userId,
        CancellationToken ct = default)
    {
        var gate = GetLock(eventId);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ev = await db.WarEvents
                .Include(e => e.Signups)
                .FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (ev is null)
            {
                return new SignupActionResult(false, "Không tìm thấy sự kiện này.");
            }

            if (ev.IsEffectivelyClosed(DateTimeOffset.UtcNow))
            {
                return new SignupActionResult(false, "Form đã đóng, không thể hủy đăng ký.");
            }

            var existing = ev.Signups.FirstOrDefault(s => s.DiscordUserId == userId);
            if (existing is null)
            {
                return new SignupActionResult(false, "Bạn chưa đăng ký sự kiện này.");
            }

            db.EventSignups.Remove(existing);
            ev.Signups.Remove(existing);
            await db.SaveChangesAsync(ct);

            return new SignupActionResult(true, Event: ev, Headline: "Bạn đã hủy đăng ký khỏi sự kiện.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SignupActionResult> SetClosedAsync(long eventId, bool isClosed, CancellationToken ct = default)
    {
        var gate = GetLock(eventId);
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ev = await db.WarEvents
                .Include(e => e.Signups)
                .FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (ev is null)
            {
                return new SignupActionResult(false, "Không tìm thấy sự kiện này.");
            }

            ev.IsClosed = isClosed;
            if (!isClosed && ev.CloseTimeUtc.HasValue && ev.CloseTimeUtc.Value <= DateTimeOffset.UtcNow)
            {
                // Nếu Leader chủ động mở lại sau khi đã qua giờ đóng, xóa mốc tự đóng cũ để không bị auto-close lại ngay
                ev.CloseTimeUtc = null;
            }

            await db.SaveChangesAsync(ct);

            return new SignupActionResult(
                true,
                Event: ev,
                Headline: isClosed ? "Đã đóng form đăng ký." : "Đã mở lại form đăng ký.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<List<WarEvent>> CloseExpiredEventsAsync(
        IReadOnlyCollection<ulong> connectedGuildIds,
        CancellationToken ct = default)
    {
        if (connectedGuildIds.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var expiredIds = await db.WarEvents
            .AsNoTracking()
            .Where(e => !e.IsClosed
                        && e.CloseTimeUtc != null
                        && e.CloseTimeUtc <= now
                        && connectedGuildIds.Contains(e.GuildId))
            .Select(e => e.Id)
            .ToListAsync(ct);

        var closedEvents = new List<WarEvent>();
        foreach (var id in expiredIds)
        {
            var result = await SetClosedAsync(id, isClosed: true, ct);
            if (result.Success && result.Event is not null)
            {
                closedEvents.Add(result.Event);
            }
        }

        return closedEvents;
    }

    /// <summary>
    /// Tìm các sự kiện đã tới thời gian bắt đầu online (EventTimeUtc) trong các server bot đang tham gia
    /// nhưng chưa gửi thông báo tag thành viên, đánh dấu OnlineReminderSent = true và trả về kèm danh sách đăng ký.
    /// </summary>
    public async Task<List<WarEvent>> ClaimDueOnlineReminderEventsAsync(
        IReadOnlyCollection<ulong> connectedGuildIds,
        CancellationToken ct = default)
    {
        if (connectedGuildIds.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now.AddHours(-24);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var dueEvents = await db.WarEvents
            .Include(e => e.Signups)
            .Where(e => !e.OnlineReminderSent
                        && e.EventTimeUtc != null
                        && e.EventTimeUtc <= now
                        && connectedGuildIds.Contains(e.GuildId))
            .ToListAsync(ct);

        if (dueEvents.Count == 0)
        {
            return [];
        }

        var toNotify = new List<WarEvent>();
        foreach (var ev in dueEvents)
        {
            ev.OnlineReminderSent = true;

            // Chỉ gửi tag nếu sự kiện chưa quá cũ (> 24h) để tránh spam sự kiện tồn đọng
            if (ev.EventTimeUtc >= staleCutoff)
            {
                toNotify.Add(ev);
            }
        }

        await db.SaveChangesAsync(ct);
        return toNotify;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

