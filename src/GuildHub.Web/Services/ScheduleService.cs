using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;
using GuildHub.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace GuildHub.Web.Services;

public enum CalendarItemSource
{
    Scheduled = 1,
    BotWarEvent = 2
}

public enum ScheduleFormStatus
{
    NoForm = 0,
    PendingAuto = 1,
    CreatingNow = 2,
    FormCreated = 3,
    Closed = 4,
    FormError = 5,
    Cancelled = 6
}

public sealed record CalendarEventItem(
    CalendarItemSource Source,
    long? ScheduledEventId,
    long? WarEventId,
    string Title,
    string? Description,
    EventType EventType,
    DateTimeOffset ScheduledTimeUtc,
    DateTimeOffset? CloseTimeUtc,
    bool AutoCreateForm,
    DateTimeOffset? AutoCreateAtUtc,
    bool TriggerFormNow,
    ulong? TargetChannelId,
    string? TargetChannelName,
    ulong? MentionRoleId,
    string? MentionRoleName,
    DateTimeOffset? FormCreatedAtUtc,
    string? LastFormError,
    bool IsCancelled,
    bool IsWarEventClosed,
    ulong? DiscordMessageId,
    int ConfirmedCount,
    int LateCount,
    int BenchCount,
    int AbsentCount,
    int NotParticipatingCount)
{
    public int ActiveAttendeeCount => ConfirmedCount + LateCount + BenchCount;

    public ScheduleFormStatus FormStatus
    {
        get
        {
            if (IsCancelled) return ScheduleFormStatus.Cancelled;
            if (WarEventId.HasValue) return IsWarEventClosed ? ScheduleFormStatus.Closed : ScheduleFormStatus.FormCreated;
            if (TriggerFormNow) return ScheduleFormStatus.CreatingNow;
            if (!string.IsNullOrWhiteSpace(LastFormError)) return ScheduleFormStatus.FormError;
            if (AutoCreateForm) return ScheduleFormStatus.PendingAuto;
            return ScheduleFormStatus.NoForm;
        }
    }
}

public sealed record ScheduleCalendarMonthResult(
    IReadOnlyList<CalendarEventItem> Items,
    int TotalEvents,
    int GuildWarCount,
    int ScrimCount,
    int OtherCount,
    int FormsCreatedCount,
    int PendingAutoCount);

public sealed record ScheduleUpsertRequest(
    string Title,
    string? Description,
    EventType EventType,
    DateTimeOffset ScheduledTimeUtc,
    DateTimeOffset? CloseTimeUtc,
    bool AutoCreateForm,
    DateTimeOffset? AutoCreateAtUtc,
    ulong? TargetChannelId,
    string? TargetChannelName,
    ulong? MentionRoleId,
    string? MentionRoleName);

public sealed record ScheduleActionResult(
    bool Success,
    string Message,
    long? ScheduledEventId = null);

public sealed record WarEventSignupRow(
    ulong DiscordUserId,
    string DisplayName,
    string? IngameNickname,
    string? AvatarUrl,
    GameClass? GameClass,
    string? ClassDisplayName,
    string? ClassIconFileName,
    string? ClassHexColor,
    ClassRole? ClassRole,
    SignupStatus Status,
    int SignupOrder,
    string? Note,
    DateTimeOffset SignedUpAtUtc);

public sealed record WarEventClassBreakdown(
    GameClass GameClass,
    string DisplayName,
    string IconFileName,
    string HexColor,
    ClassRole Role,
    int Count);

public sealed record WarEventSignupDetail(
    long WarEventId,
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    string Title,
    string? Description,
    EventType EventType,
    DateTimeOffset? EventTimeUtc,
    DateTimeOffset? CloseTimeUtc,
    bool IsClosed,
    int ConfirmedCount,
    int LateCount,
    int BenchCount,
    int AbsentCount,
    int NotParticipatingCount,
    int TankCount,
    int DpsCount,
    int HealerCount,
    IReadOnlyList<WarEventClassBreakdown> ClassBreakdowns,
    IReadOnlyList<WarEventSignupRow> Signups);

public sealed class ScheduleService(
    IDbContextFactory<GuildHubDbContext> dbFactory,
    MemberManagementService memberService)
{
    public async Task<ScheduleCalendarMonthResult> GetCalendarRangeAsync(
        ulong guildId,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        DateTimeOffset monthStartUtc,
        DateTimeOffset monthEndUtc,
        EventType? filterType = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // 1. Lấy tất cả ScheduledEvents trong khoảng lưới lịch
        var scheduledQuery = db.ScheduledEvents
            .AsNoTracking()
            .Include(s => s.CreatedWarEvent)
            .Where(s => s.GuildId == guildId
                        && s.ScheduledTimeUtc >= rangeStartUtc
                        && s.ScheduledTimeUtc < rangeEndUtc);

        if (filterType.HasValue)
        {
            scheduledQuery = scheduledQuery.Where(s => s.EventType == filterType.Value);
        }

        var scheduledList = await scheduledQuery
            .OrderBy(s => s.ScheduledTimeUtc)
            .ToListAsync(ct);

        // 2. Lấy toàn bộ ID war_events đã được liên kết bởi scheduled_events của guild để tránh trùng lặp
        var linkedWarEventIds = await db.ScheduledEvents
            .AsNoTracking()
            .Where(s => s.GuildId == guildId && s.CreatedWarEventId != null)
            .Select(s => s.CreatedWarEventId!.Value)
            .ToListAsync(ct);

        var linkedSet = linkedWarEventIds.ToHashSet();

        // 3. Lấy thêm các WarEvents tạo trực tiếp từ Bot (/tao-form) trong khoảng lưới lịch
        var standaloneWarQuery = db.WarEvents
            .AsNoTracking()
            .Where(w => w.GuildId == guildId
                        && !linkedSet.Contains(w.Id)
                        && ((w.EventTimeUtc != null && w.EventTimeUtc >= rangeStartUtc && w.EventTimeUtc < rangeEndUtc)
                            || (w.EventTimeUtc == null && w.CreatedAtUtc >= rangeStartUtc && w.CreatedAtUtc < rangeEndUtc)));

        if (filterType.HasValue)
        {
            standaloneWarQuery = standaloneWarQuery.Where(w => w.EventType == filterType.Value);
        }

        var standaloneWars = await standaloneWarQuery
            .OrderBy(w => w.EventTimeUtc ?? w.CreatedAtUtc)
            .ToListAsync(ct);

        // 4. Tổng hợp số lượng đăng ký theo từng trạng thái cho mọi WarEvent xuất hiện trên lịch
        var allWarEventIds = scheduledList
            .Where(s => s.CreatedWarEventId.HasValue)
            .Select(s => s.CreatedWarEventId!.Value)
            .Concat(standaloneWars.Select(w => w.Id))
            .Distinct()
            .ToList();

        var signupCountsByEvent = new Dictionary<long, (int Confirmed, int Late, int Bench, int Absent, int NotPart)>();
        if (allWarEventIds.Count > 0)
        {
            var grouped = await db.EventSignups
                .AsNoTracking()
                .Where(x => allWarEventIds.Contains(x.WarEventId))
                .GroupBy(x => new { x.WarEventId, x.Status })
                .Select(g => new { g.Key.WarEventId, g.Key.Status, Count = g.Count() })
                .ToListAsync(ct);

            foreach (var g in grouped)
            {
                signupCountsByEvent.TryGetValue(g.WarEventId, out var cur);
                cur = g.Status switch
                {
                    SignupStatus.Confirmed => (cur.Confirmed + g.Count, cur.Late, cur.Bench, cur.Absent, cur.NotPart),
                    SignupStatus.Late => (cur.Confirmed, cur.Late + g.Count, cur.Bench, cur.Absent, cur.NotPart),
                    SignupStatus.Bench => (cur.Confirmed, cur.Late, cur.Bench + g.Count, cur.Absent, cur.NotPart),
                    SignupStatus.Absent => (cur.Confirmed, cur.Late, cur.Bench, cur.Absent + g.Count, cur.NotPart),
                    SignupStatus.NotParticipating => (cur.Confirmed, cur.Late, cur.Bench, cur.Absent, cur.NotPart + g.Count),
                    _ => cur
                };
                signupCountsByEvent[g.WarEventId] = cur;
            }
        }

        var items = new List<CalendarEventItem>(scheduledList.Count + standaloneWars.Count);

        foreach (var s in scheduledList)
        {
            var warId = s.CreatedWarEventId;
            var counts = warId.HasValue && signupCountsByEvent.TryGetValue(warId.Value, out var c)
                ? c
                : default;

            var isClosed = s.CreatedWarEvent?.IsEffectivelyClosed(now) ?? false;
            ulong? msgId = s.CreatedWarEvent is not null && s.CreatedWarEvent.MessageId > 0
                ? s.CreatedWarEvent.MessageId
                : null;

            items.Add(new CalendarEventItem(
                Source: CalendarItemSource.Scheduled,
                ScheduledEventId: s.Id,
                WarEventId: warId,
                Title: s.Title,
                Description: s.Description,
                EventType: s.EventType,
                ScheduledTimeUtc: s.ScheduledTimeUtc,
                CloseTimeUtc: s.CreatedWarEvent?.CloseTimeUtc ?? s.CloseTimeUtc,
                AutoCreateForm: s.AutoCreateForm,
                AutoCreateAtUtc: s.AutoCreateAtUtc,
                TriggerFormNow: s.TriggerFormNow,
                TargetChannelId: s.TargetChannelId,
                TargetChannelName: s.TargetChannelName,
                MentionRoleId: s.MentionRoleId,
                MentionRoleName: s.MentionRoleName,
                FormCreatedAtUtc: s.FormCreatedAtUtc,
                LastFormError: s.LastFormError,
                IsCancelled: s.IsCancelled,
                IsWarEventClosed: isClosed,
                DiscordMessageId: msgId,
                ConfirmedCount: counts.Confirmed,
                LateCount: counts.Late,
                BenchCount: counts.Bench,
                AbsentCount: counts.Absent,
                NotParticipatingCount: counts.NotPart));
        }

        foreach (var w in standaloneWars)
        {
            var counts = signupCountsByEvent.TryGetValue(w.Id, out var c) ? c : default;

            items.Add(new CalendarEventItem(
                Source: CalendarItemSource.BotWarEvent,
                ScheduledEventId: null,
                WarEventId: w.Id,
                Title: w.Title,
                Description: w.Description,
                EventType: w.EventType,
                ScheduledTimeUtc: w.EventTimeUtc ?? w.CreatedAtUtc,
                CloseTimeUtc: w.CloseTimeUtc,
                AutoCreateForm: false,
                AutoCreateAtUtc: null,
                TriggerFormNow: false,
                TargetChannelId: w.ChannelId > 0 ? w.ChannelId : null,
                TargetChannelName: null,
                MentionRoleId: w.MentionRoleId,
                MentionRoleName: null,
                FormCreatedAtUtc: w.CreatedAtUtc,
                LastFormError: null,
                IsCancelled: false,
                IsWarEventClosed: w.IsEffectivelyClosed(now),
                DiscordMessageId: w.MessageId > 0 ? w.MessageId : null,
                ConfirmedCount: counts.Confirmed,
                LateCount: counts.Late,
                BenchCount: counts.Bench,
                AbsentCount: counts.Absent,
                NotParticipatingCount: counts.NotPart));
        }

        items.Sort((a, b) => a.ScheduledTimeUtc.CompareTo(b.ScheduledTimeUtc));

        // Thống kê chỉ tính trong tháng đang xem (không tính các ngày rìa tháng trước/sau)
        var monthActiveItems = items
            .Where(i => !i.IsCancelled && i.ScheduledTimeUtc >= monthStartUtc && i.ScheduledTimeUtc < monthEndUtc)
            .ToList();

        return new ScheduleCalendarMonthResult(
            Items: items,
            TotalEvents: monthActiveItems.Count,
            GuildWarCount: monthActiveItems.Count(i => i.EventType == EventType.GuildWar),
            ScrimCount: monthActiveItems.Count(i => i.EventType == EventType.Scrim),
            OtherCount: monthActiveItems.Count(i => i.EventType == EventType.Other),
            FormsCreatedCount: monthActiveItems.Count(i => i.WarEventId.HasValue),
            PendingAutoCount: monthActiveItems.Count(i => !i.WarEventId.HasValue && i.AutoCreateForm));
    }

    public async Task<ScheduleActionResult> CreateScheduleAsync(
        ulong guildId,
        ulong createdByDiscordId,
        ScheduleUpsertRequest request,
        CancellationToken ct = default)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return new ScheduleActionResult(false, validationError);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var entity = new ScheduledEvent
        {
            GuildId = guildId,
            Title = Truncate(request.Title.Trim(), 200),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : Truncate(request.Description.Trim(), 1500),
            EventType = request.EventType,
            ScheduledTimeUtc = request.ScheduledTimeUtc.ToUniversalTime(),
            CloseTimeUtc = request.CloseTimeUtc?.ToUniversalTime(),
            AutoCreateForm = request.AutoCreateForm,
            AutoCreateAtUtc = request.AutoCreateForm ? request.AutoCreateAtUtc?.ToUniversalTime() : null,
            TriggerFormNow = false,
            TargetChannelId = request.TargetChannelId,
            TargetChannelName = string.IsNullOrWhiteSpace(request.TargetChannelName) ? null : Truncate(request.TargetChannelName.Trim(), 100),
            MentionRoleId = request.MentionRoleId,
            MentionRoleName = string.IsNullOrWhiteSpace(request.MentionRoleName) ? null : Truncate(request.MentionRoleName.Trim(), 100),
            IsCancelled = false,
            CreatedByDiscordId = createdByDiscordId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.ScheduledEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        return new ScheduleActionResult(true, "Đã thêm lịch hoạt động mới.", entity.Id);
    }

    public async Task<ScheduleActionResult> UpdateScheduleAsync(
        ulong guildId,
        long scheduleId,
        ScheduleUpsertRequest request,
        CancellationToken ct = default)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return new ScheduleActionResult(false, validationError);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ScheduledEvents.FirstOrDefaultAsync(e => e.Id == scheduleId && e.GuildId == guildId, ct);
        if (entity is null)
        {
            return new ScheduleActionResult(false, "Không tìm thấy lịch hoạt động cần chỉnh sửa.");
        }

        entity.Title = Truncate(request.Title.Trim(), 200);
        entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : Truncate(request.Description.Trim(), 1500);
        entity.EventType = request.EventType;
        entity.ScheduledTimeUtc = request.ScheduledTimeUtc.ToUniversalTime();
        entity.CloseTimeUtc = request.CloseTimeUtc?.ToUniversalTime();

        if (!entity.CreatedWarEventId.HasValue)
        {
            entity.AutoCreateForm = request.AutoCreateForm;
            entity.AutoCreateAtUtc = request.AutoCreateForm ? request.AutoCreateAtUtc?.ToUniversalTime() : null;
            entity.TargetChannelId = request.TargetChannelId;
            entity.TargetChannelName = string.IsNullOrWhiteSpace(request.TargetChannelName) ? null : Truncate(request.TargetChannelName.Trim(), 100);
            entity.MentionRoleId = request.MentionRoleId;
            entity.MentionRoleName = string.IsNullOrWhiteSpace(request.MentionRoleName) ? null : Truncate(request.MentionRoleName.Trim(), 100);
            entity.LastFormError = null;
            entity.LastFormAttemptAtUtc = null;
        }

        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ScheduleActionResult(true, "Đã cập nhật lịch hoạt động.", entity.Id);
    }

    public async Task<ScheduleActionResult> ToggleCancelScheduleAsync(
        ulong guildId,
        long scheduleId,
        bool isCancelled,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ScheduledEvents.FirstOrDefaultAsync(e => e.Id == scheduleId && e.GuildId == guildId, ct);
        if (entity is null)
        {
            return new ScheduleActionResult(false, "Không tìm thấy lịch hoạt động.");
        }

        entity.IsCancelled = isCancelled;
        if (isCancelled)
        {
            entity.TriggerFormNow = false;
        }

        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ScheduleActionResult(true, isCancelled ? "Đã hủy lịch hoạt động." : "Đã khôi phục lịch hoạt động.", entity.Id);
    }

    public async Task<ScheduleActionResult> DeleteScheduleAsync(
        ulong guildId,
        long scheduleId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ScheduledEvents.FirstOrDefaultAsync(e => e.Id == scheduleId && e.GuildId == guildId, ct);
        if (entity is null)
        {
            return new ScheduleActionResult(false, "Không tìm thấy lịch hoạt động.");
        }

        db.ScheduledEvents.Remove(entity);
        await db.SaveChangesAsync(ct);

        return new ScheduleActionResult(true, "Đã xóa lịch hoạt động.");
    }

    public async Task<ScheduleActionResult> TriggerFormNowAsync(
        ulong guildId,
        long scheduleId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ScheduledEvents.FirstOrDefaultAsync(e => e.Id == scheduleId && e.GuildId == guildId, ct);
        if (entity is null)
        {
            return new ScheduleActionResult(false, "Không tìm thấy lịch hoạt động.");
        }

        if (entity.IsCancelled)
        {
            return new ScheduleActionResult(false, "Lịch đã bị hủy. Vui lòng khôi phục lịch trước khi tạo form.");
        }

        if (entity.CreatedWarEventId.HasValue)
        {
            return new ScheduleActionResult(false, "Sự kiện này đã có form đăng ký trên Discord.");
        }

        if (!entity.TargetChannelId.HasValue || entity.TargetChannelId.Value == 0)
        {
            return new ScheduleActionResult(false, "Vui lòng chỉnh sửa lịch và chọn kênh Discord trước khi tạo form.");
        }

        entity.TriggerFormNow = true;
        entity.LastFormError = null;
        entity.LastFormAttemptAtUtc = null;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ScheduleActionResult(true, "Đã gửi lệnh tạo form cho Bot. Form sẽ xuất hiện trên Discord sau vài giây.", entity.Id);
    }

    public async Task<ScheduledEvent?> GetScheduleByIdAsync(
        ulong guildId,
        long scheduleId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScheduledEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == scheduleId && e.GuildId == guildId, ct);
    }

    public async Task<WarEventSignupDetail?> GetWarEventSignupDetailAsync(
        ulong guildId,
        long warEventId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var warEvent = await db.WarEvents
            .AsNoTracking()
            .Include(w => w.Signups)
            .FirstOrDefaultAsync(w => w.Id == warEventId && w.GuildId == guildId, ct);

        if (warEvent is null)
        {
            return null;
        }

        var classConfigs = await memberService.GetResolvedClassConfigsAsync(guildId, ct);
        var classMap = classConfigs.ToDictionary(c => c.GameClass);

        var userIdSet = warEvent.Signups.Select(s => s.DiscordUserId).ToHashSet();
        var memberMap = new Dictionary<ulong, (string DisplayName, string? IngameNickname, string? AvatarUrl)>();
        if (userIdSet.Count > 0)
        {
            // Tránh dùng List<ulong>.Contains(m.DiscordUserId.Value) vì Npgsql ValueConverter<ulong?, long?> không hỗ trợ array conversion trên ulong không nullable
            var guildMembers = await db.GuildMembers
                .AsNoTracking()
                .Where(m => m.GuildId == guildId && m.DiscordUserId != null)
                .Select(m => new { m.DiscordUserId, m.DisplayName, m.IngameNickname, m.AvatarUrl })
                .ToListAsync(ct);

            foreach (var m in guildMembers)
            {
                if (m.DiscordUserId.HasValue && userIdSet.Contains(m.DiscordUserId.Value))
                {
                    memberMap[m.DiscordUserId.Value] = (m.DisplayName, m.IngameNickname, m.AvatarUrl);
                }
            }
        }

        var rows = new List<WarEventSignupRow>(warEvent.Signups.Count);
        var classCounts = new Dictionary<GameClass, int>();
        int confirmed = 0, late = 0, bench = 0, absent = 0, notPart = 0;
        int tank = 0, dps = 0, healer = 0;

        foreach (var s in warEvent.Signups.OrderBy(x => GetStatusSortOrder(x.Status)).ThenBy(x => x.SignupOrder))
        {
            switch (s.Status)
            {
                case SignupStatus.Confirmed: confirmed++; break;
                case SignupStatus.Late: late++; break;
                case SignupStatus.Bench: bench++; break;
                case SignupStatus.Absent: absent++; break;
                case SignupStatus.NotParticipating: notPart++; break;
            }

            var hasMember = memberMap.TryGetValue(s.DiscordUserId, out var member);

            string? className = null;
            string? classIcon = null;
            string? classHex = null;
            ClassRole? classRole = null;

            if (s.GameClass.HasValue)
            {
                if (classMap.TryGetValue(s.GameClass.Value, out var cfg))
                {
                    className = cfg.DisplayName;
                    classIcon = cfg.IconFileName;
                    classHex = cfg.HexColor;
                    classRole = cfg.Role;
                }
                else if (ClassMetadata.TryGet(s.GameClass.Value, out var meta))
                {
                    className = meta.DisplayName;
                    classIcon = meta.FileName;
                    classHex = meta.HexColor;
                    classRole = meta.Role;
                }
                else
                {
                    className = $"Hệ phái #{(int)s.GameClass.Value}";
                    classHex = "#6366F1";
                    classRole = ClassRole.Dps;
                }

                if (s.Status is SignupStatus.Confirmed or SignupStatus.Late or SignupStatus.Bench)
                {
                    classCounts[s.GameClass.Value] = classCounts.TryGetValue(s.GameClass.Value, out var prev) ? prev + 1 : 1;
                    switch (classRole)
                    {
                        case ClassRole.Tank: tank++; break;
                        case ClassRole.Healer: healer++; break;
                        default: dps++; break;
                    }
                }
            }

            rows.Add(new WarEventSignupRow(
                DiscordUserId: s.DiscordUserId,
                DisplayName: !string.IsNullOrWhiteSpace(s.DisplayName)
                    ? s.DisplayName
                    : (hasMember && !string.IsNullOrWhiteSpace(member.DisplayName) ? member.DisplayName : s.DiscordUserId.ToString()),
                IngameNickname: hasMember ? member.IngameNickname : null,
                AvatarUrl: hasMember ? member.AvatarUrl : null,
                GameClass: s.GameClass,
                ClassDisplayName: className,
                ClassIconFileName: classIcon,
                ClassHexColor: classHex,
                ClassRole: classRole,
                Status: s.Status,
                SignupOrder: s.SignupOrder,
                Note: s.Note,
                SignedUpAtUtc: s.SignedUpAtUtc));
        }

        var breakdowns = classCounts
            .Select(kv =>
            {
                if (classMap.TryGetValue(kv.Key, out var cfg))
                {
                    return new WarEventClassBreakdown(kv.Key, cfg.DisplayName, cfg.IconFileName, cfg.HexColor, cfg.Role, kv.Value);
                }

                if (ClassMetadata.TryGet(kv.Key, out var meta))
                {
                    return new WarEventClassBreakdown(kv.Key, meta.DisplayName, meta.FileName, meta.HexColor, meta.Role, kv.Value);
                }

                return new WarEventClassBreakdown(kv.Key, $"Hệ phái #{(int)kv.Key}", "thamgia.png", "#6366F1", ClassRole.Dps, kv.Value);
            })
            .OrderByDescending(b => b.Count)
            .ThenBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WarEventSignupDetail(
            WarEventId: warEvent.Id,
            GuildId: warEvent.GuildId,
            ChannelId: warEvent.ChannelId,
            MessageId: warEvent.MessageId,
            Title: warEvent.Title,
            Description: warEvent.Description,
            EventType: warEvent.EventType,
            EventTimeUtc: warEvent.EventTimeUtc,
            CloseTimeUtc: warEvent.CloseTimeUtc,
            IsClosed: warEvent.IsEffectivelyClosed(DateTimeOffset.UtcNow),
            ConfirmedCount: confirmed,
            LateCount: late,
            BenchCount: bench,
            AbsentCount: absent,
            NotParticipatingCount: notPart,
            TankCount: tank,
            DpsCount: dps,
            HealerCount: healer,
            ClassBreakdowns: breakdowns,
            Signups: rows);
    }

    private static string? ValidateRequest(ScheduleUpsertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
        {
            return "Vui lòng nhập tiêu đề hoạt động.";
        }

        if (req.CloseTimeUtc.HasValue && req.CloseTimeUtc.Value > req.ScheduledTimeUtc)
        {
            return "Giờ đóng đăng ký không thể sau giờ bắt đầu hoạt động.";
        }

        if (req.AutoCreateForm)
        {
            if (!req.TargetChannelId.HasValue || req.TargetChannelId.Value == 0)
            {
                return "Vui lòng chọn kênh Discord để Bot tự động đăng form.";
            }

            if (!req.AutoCreateAtUtc.HasValue)
            {
                return "Vui lòng chọn thời điểm Bot tự động mở form.";
            }

            if (req.CloseTimeUtc.HasValue && req.AutoCreateAtUtc.Value >= req.CloseTimeUtc.Value)
            {
                return "Thời điểm tự động mở form phải trước giờ đóng đăng ký.";
            }

            if (req.AutoCreateAtUtc.Value >= req.ScheduledTimeUtc)
            {
                return "Thời điểm tự động mở form phải trước giờ bắt đầu hoạt động.";
            }
        }

        return null;
    }

    private static int GetStatusSortOrder(SignupStatus status) => status switch
    {
        SignupStatus.Confirmed => 1,
        SignupStatus.Late => 2,
        SignupStatus.Bench => 3,
        SignupStatus.Absent => 4,
        SignupStatus.NotParticipating => 5,
        _ => 9
    };

    private static string Truncate(string input, int maxLen) =>
        input.Length <= maxLen ? input : input[..maxLen];
}
