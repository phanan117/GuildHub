using System.Text;
using ClosedXML.Excel;
using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;
using GuildHub.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace GuildHub.Web.Services;

public enum AnalyticsRange
{
    Last30Days = 30,
    Last90Days = 90,
    AllTime = 0
}

public sealed record WeeklyAttendancePoint(
    DateOnly WeekStartVn,
    string WeekLabel,
    string WeekRangeLabel,
    int EventCount,
    int TotalAttended,
    double AverageAttended,
    int TotalBench,
    int TotalBusy);

public sealed record EventTypeMetrics(
    EventType EventType,
    string Label,
    int EventCount,
    int TotalAttended,
    double AverageAttended,
    double AverageBench,
    double AverageBusy,
    int PeakAttended);

public sealed record CombatClassStat(
    GameClass GameClass,
    string DisplayName,
    string IconFileName,
    string HexColor,
    ClassRole Role,
    int TotalSignups,
    double AveragePerEvent,
    double SharePercent);

public sealed record MemberAttendanceRow(
    ulong DiscordUserId,
    string DisplayName,
    string? IngameNickname,
    string? AvatarUrl,
    GameClass? GameClass,
    string? ClassDisplayName,
    string? ClassIconFileName,
    string? ClassHexColor,
    ClassRole? ClassRole,
    bool IsActiveGuildMember,
    int ConfirmedCount,
    int LateCount,
    int AttendedCount,
    int BenchCount,
    int BusyCount,
    int NoResponseCount,
    int TotalEvents,
    double AttendanceRatePercent,
    DateTimeOffset? LastAttendedUtc)
{
    public string PrimaryName => !string.IsNullOrWhiteSpace(IngameNickname) ? IngameNickname : DisplayName;
}

public sealed record AnalyticsEventSummaryRow(
    long WarEventId,
    string Title,
    EventType EventType,
    DateTimeOffset EffectiveTimeUtc,
    bool IsClosed,
    int ConfirmedCount,
    int LateCount,
    int BenchCount,
    int BusyCount);

public sealed record AnalyticsDashboardResult(
    AnalyticsRange Range,
    EventType? EventTypeFilter,
    int TotalEvents,
    int ActiveGuildMemberCount,
    int ActiveParticipantsCount,
    int TotalAttendedSignups,
    int TotalBenchSignups,
    int TotalBusySignups,
    double AverageAttendanceRatePercent,
    double AverageParticipantsPerEvent,
    IReadOnlyList<WeeklyAttendancePoint> WeeklyTrend,
    EventTypeMetrics GuildWarComparison,
    EventTypeMetrics ScrimComparison,
    int TankCombatTotal,
    int DpsCombatTotal,
    int HealerCombatTotal,
    IReadOnlyList<CombatClassStat> CombatClassStats,
    IReadOnlyList<MemberAttendanceRow> TopActiveMembers,
    IReadOnlyList<MemberAttendanceRow> AttendanceRows,
    IReadOnlyList<AnalyticsEventSummaryRow> Events,
    IReadOnlyList<ResolvedClassConfig> ClassConfigs);

public sealed record AnalyticsExcelResult(byte[] Content, string FileName);

public sealed class AnalyticsService(
    IDbContextFactory<GuildHubDbContext> dbFactory,
    MemberManagementService memberService)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task<AnalyticsDashboardResult> GetAnalyticsAsync(
        ulong guildId,
        AnalyticsRange range = AnalyticsRange.Last30Days,
        EventType? eventTypeFilter = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset? cutoffUtc = range switch
        {
            AnalyticsRange.Last30Days => nowUtc.AddDays(-30),
            AnalyticsRange.Last90Days => nowUtc.AddDays(-90),
            _ => null
        };

        // 1. Lấy cấu hình hệ phái của server (bao gồm hệ phái tùy chỉnh)
        var classConfigs = await memberService.GetResolvedClassConfigsAsync(guildId, ct);
        var classMap = classConfigs.ToDictionary(c => c.GameClass);

        // 2. Lấy toàn bộ sự kiện trong khoảng thời gian đã chọn
        var eventsQuery = db.WarEvents
            .AsNoTracking()
            .Include(w => w.Signups)
            .Where(w => w.GuildId == guildId);

        if (cutoffUtc.HasValue)
        {
            var cut = cutoffUtc.Value;
            eventsQuery = eventsQuery.Where(w =>
                (w.EventTimeUtc != null && w.EventTimeUtc >= cut) ||
                (w.EventTimeUtc == null && w.CreatedAtUtc >= cut));
        }

        var allRangeEvents = await eventsQuery.ToListAsync(ct);

        // So sánh Bang chiến vs Giao hữu trên toàn bộ khoảng thời gian chọn
        var guildWarComparison = BuildEventTypeMetrics(
            EventType.GuildWar,
            "Bang chiến",
            allRangeEvents.Where(e => e.EventType == EventType.GuildWar).ToList());

        var scrimComparison = BuildEventTypeMetrics(
            EventType.Scrim,
            "Giao hữu (Scrim)",
            allRangeEvents.Where(e => e.EventType == EventType.Scrim).ToList());

        // Áp dụng bộ lọc EventType cho các KPI, biểu đồ và bảng chuyên cần
        var filteredEvents = eventTypeFilter.HasValue
            ? allRangeEvents.Where(e => e.EventType == eventTypeFilter.Value).ToList()
            : allRangeEvents;

        filteredEvents = filteredEvents
            .OrderByDescending(GetEffectiveTimeUtc)
            .ToList();

        // 3. Lấy danh sách thành viên bang đang hoạt động
        var activeGuildMembers = await db.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId && !m.IsRemoved && m.DiscordUserId != null)
            .ToListAsync(ct);

        var memberByDiscordId = new Dictionary<ulong, GuildMember>();
        foreach (var m in activeGuildMembers)
        {
            if (m.DiscordUserId.HasValue)
            {
                memberByDiscordId[m.DiscordUserId.Value] = m;
            }
        }

        // 4. Tổng hợp thống kê theo từng sự kiện & từng thành viên
        var totalEvents = filteredEvents.Count;
        int totalAttendedAll = 0;
        int totalBenchAll = 0;
        int totalBusyAll = 0;
        int activeMemberAttendedTotal = 0;

        int tankCombat = 0;
        int dpsCombat = 0;
        int healerCombat = 0;
        var combatClassCounts = new Dictionary<GameClass, int>();

        var userStats = new Dictionary<ulong, UserAccumulator>();
        foreach (var (uid, member) in memberByDiscordId)
        {
            userStats[uid] = new UserAccumulator
            {
                DiscordUserId = uid,
                DisplayName = member.DisplayName,
                IngameNickname = member.IngameNickname,
                AvatarUrl = member.AvatarUrl,
                GameClass = member.GameClass,
                IsActiveGuildMember = true
            };
        }

        var eventSummaries = new List<AnalyticsEventSummaryRow>(totalEvents);
        var weekBuckets = new Dictionary<DateOnly, WeekAccumulator>();

        foreach (var ev in filteredEvents)
        {
            var effectiveTime = GetEffectiveTimeUtc(ev);
            var weekStartVn = GetMondayOfWeekVn(effectiveTime);

            if (!weekBuckets.TryGetValue(weekStartVn, out var wk))
            {
                wk = new WeekAccumulator(weekStartVn);
                weekBuckets[weekStartVn] = wk;
            }
            wk.EventCount++;

            int evConfirmed = 0;
            int evLate = 0;
            int evBench = 0;
            int evBusy = 0;

            foreach (var signup in ev.Signups)
            {
                if (!userStats.TryGetValue(signup.DiscordUserId, out var acc))
                {
                    acc = new UserAccumulator
                    {
                        DiscordUserId = signup.DiscordUserId,
                        DisplayName = signup.DisplayName,
                        GameClass = signup.GameClass,
                        IsActiveGuildMember = false
                    };
                    userStats[signup.DiscordUserId] = acc;
                }
                else if (acc.GameClass is null && signup.GameClass.HasValue)
                {
                    acc.GameClass = signup.GameClass;
                }

                switch (signup.Status)
                {
                    case SignupStatus.Confirmed:
                        evConfirmed++;
                        acc.ConfirmedCount++;
                        if (acc.LastAttendedUtc is null || effectiveTime > acc.LastAttendedUtc.Value)
                        {
                            acc.LastAttendedUtc = effectiveTime;
                        }
                        if (acc.IsActiveGuildMember) activeMemberAttendedTotal++;
                        break;

                    case SignupStatus.Late:
                        evLate++;
                        acc.LateCount++;
                        if (acc.LastAttendedUtc is null || effectiveTime > acc.LastAttendedUtc.Value)
                        {
                            acc.LastAttendedUtc = effectiveTime;
                        }
                        if (acc.IsActiveGuildMember) activeMemberAttendedTotal++;
                        break;

                    case SignupStatus.Bench:
                        evBench++;
                        acc.BenchCount++;
                        break;

                    case SignupStatus.Absent:
                    case SignupStatus.NotParticipating:
                        evBusy++;
                        acc.BusyCount++;
                        break;
                }

                // Hệ phái thực chiến (tính các lượt tham gia, đi trễ, dự bị có chọn phái)
                if (signup.GameClass.HasValue &&
                    signup.Status is SignupStatus.Confirmed or SignupStatus.Late or SignupStatus.Bench)
                {
                    var gc = signup.GameClass.Value;
                    combatClassCounts[gc] = combatClassCounts.TryGetValue(gc, out var prev) ? prev + 1 : 1;

                    var role = ResolveClassRole(gc, classMap);
                    switch (role)
                    {
                        case ClassRole.Tank: tankCombat++; break;
                        case ClassRole.Healer: healerCombat++; break;
                        default: dpsCombat++; break;
                    }
                }
            }

            var evAttended = evConfirmed + evLate;
            totalAttendedAll += evAttended;
            totalBenchAll += evBench;
            totalBusyAll += evBusy;

            wk.TotalAttended += evAttended;
            wk.TotalBench += evBench;
            wk.TotalBusy += evBusy;

            eventSummaries.Add(new AnalyticsEventSummaryRow(
                WarEventId: ev.Id,
                Title: ev.Title,
                EventType: ev.EventType,
                EffectiveTimeUtc: effectiveTime,
                IsClosed: ev.IsEffectivelyClosed(nowUtc),
                ConfirmedCount: evConfirmed,
                LateCount: evLate,
                BenchCount: evBench,
                BusyCount: evBusy));
        }

        // 5. Biểu đồ theo tuần (tối đa 12 tuần gần nhất có dữ liệu, sắp xếp tăng dần theo thời gian)
        var weeklyTrend = weekBuckets.Values
            .OrderBy(w => w.WeekStartVn)
            .TakeLast(12)
            .Select(w =>
            {
                var weekEnd = w.WeekStartVn.AddDays(6);
                return new WeeklyAttendancePoint(
                    WeekStartVn: w.WeekStartVn,
                    WeekLabel: w.WeekStartVn.ToString("dd/MM"),
                    WeekRangeLabel: $"{w.WeekStartVn:dd/MM} – {weekEnd:dd/MM}",
                    EventCount: w.EventCount,
                    TotalAttended: w.TotalAttended,
                    AverageAttended: w.EventCount > 0 ? Math.Round(w.TotalAttended / (double)w.EventCount, 1) : 0,
                    TotalBench: w.TotalBench,
                    TotalBusy: w.TotalBusy);
            })
            .ToList();

        // 6. Phân bố hệ phái thực chiến
        var totalCombatWithClass = combatClassCounts.Values.Sum();
        var combatClassStats = combatClassCounts
            .Select(kv =>
            {
                var (name, icon, hex, role) = ResolveClassDisplay(kv.Key, classMap);
                return new CombatClassStat(
                    GameClass: kv.Key,
                    DisplayName: name,
                    IconFileName: icon,
                    HexColor: hex,
                    Role: role,
                    TotalSignups: kv.Value,
                    AveragePerEvent: totalEvents > 0 ? Math.Round(kv.Value / (double)totalEvents, 1) : 0,
                    SharePercent: totalCombatWithClass > 0 ? Math.Round(kv.Value * 100.0 / totalCombatWithClass, 1) : 0);
            })
            .OrderByDescending(c => c.TotalSignups)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 7. Bảng chuyên cần chi tiết từng thành viên
        var attendanceRows = new List<MemberAttendanceRow>(userStats.Count);
        foreach (var acc in userStats.Values)
        {
            var attended = acc.ConfirmedCount + acc.LateCount;
            var registered = attended + acc.BenchCount + acc.BusyCount;
            var noResponse = Math.Max(0, totalEvents - registered);
            var rate = totalEvents > 0
                ? Math.Min(100.0, Math.Round(attended * 100.0 / totalEvents, 1))
                : 0.0;

            string? className = null;
            string? classIcon = null;
            string? classHex = null;
            ClassRole? classRole = null;

            if (acc.GameClass.HasValue)
            {
                var resolved = ResolveClassDisplay(acc.GameClass.Value, classMap);
                className = resolved.DisplayName;
                classIcon = resolved.IconFileName;
                classHex = resolved.HexColor;
                classRole = resolved.Role;
            }

            attendanceRows.Add(new MemberAttendanceRow(
                DiscordUserId: acc.DiscordUserId,
                DisplayName: acc.DisplayName,
                IngameNickname: acc.IngameNickname,
                AvatarUrl: acc.AvatarUrl,
                GameClass: acc.GameClass,
                ClassDisplayName: className,
                ClassIconFileName: classIcon,
                ClassHexColor: classHex,
                ClassRole: classRole,
                IsActiveGuildMember: acc.IsActiveGuildMember,
                ConfirmedCount: acc.ConfirmedCount,
                LateCount: acc.LateCount,
                AttendedCount: attended,
                BenchCount: acc.BenchCount,
                BusyCount: acc.BusyCount,
                NoResponseCount: noResponse,
                TotalEvents: totalEvents,
                AttendanceRatePercent: rate,
                LastAttendedUtc: acc.LastAttendedUtc));
        }

        attendanceRows = attendanceRows
            .OrderByDescending(r => r.AttendanceRatePercent)
            .ThenByDescending(r => r.AttendedCount)
            .ThenByDescending(r => r.BenchCount)
            .ThenBy(r => r.PrimaryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var topActiveMembers = attendanceRows
            .Where(r => r.AttendedCount > 0)
            .OrderByDescending(r => r.AttendedCount)
            .ThenByDescending(r => r.AttendanceRatePercent)
            .ThenByDescending(r => r.BenchCount)
            .ThenByDescending(r => r.LastAttendedUtc)
            .ThenBy(r => r.PrimaryName, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var activeGuildCount = memberByDiscordId.Count;
        var activeParticipantsCount = attendanceRows.Count(r => r.IsActiveGuildMember && r.AttendedCount > 0);

        double avgRate = 0;
        if (totalEvents > 0)
        {
            if (activeGuildCount > 0)
            {
                avgRate = Math.Min(100.0, Math.Round(activeMemberAttendedTotal * 100.0 / (totalEvents * activeGuildCount), 1));
            }
            else if (attendanceRows.Count > 0)
            {
                avgRate = Math.Round(attendanceRows.Average(r => r.AttendanceRatePercent), 1);
            }
        }

        var avgParticipantsPerEvent = totalEvents > 0
            ? Math.Round(totalAttendedAll / (double)totalEvents, 1)
            : 0;

        return new AnalyticsDashboardResult(
            Range: range,
            EventTypeFilter: eventTypeFilter,
            TotalEvents: totalEvents,
            ActiveGuildMemberCount: activeGuildCount,
            ActiveParticipantsCount: activeParticipantsCount,
            TotalAttendedSignups: totalAttendedAll,
            TotalBenchSignups: totalBenchAll,
            TotalBusySignups: totalBusyAll,
            AverageAttendanceRatePercent: avgRate,
            AverageParticipantsPerEvent: avgParticipantsPerEvent,
            WeeklyTrend: weeklyTrend,
            GuildWarComparison: guildWarComparison,
            ScrimComparison: scrimComparison,
            TankCombatTotal: tankCombat,
            DpsCombatTotal: dpsCombat,
            HealerCombatTotal: healerCombat,
            CombatClassStats: combatClassStats,
            TopActiveMembers: topActiveMembers,
            AttendanceRows: attendanceRows,
            Events: eventSummaries,
            ClassConfigs: classConfigs);
    }

    public async Task<AnalyticsExcelResult> ExportAttendanceExcelAsync(
        ulong guildId,
        string guildName,
        AnalyticsRange range = AnalyticsRange.Last30Days,
        EventType? eventTypeFilter = null,
        GameClass? classFilter = null,
        string? search = null,
        string? tierFilter = null,
        CancellationToken ct = default)
    {
        var data = await GetAnalyticsAsync(guildId, range, eventTypeFilter, ct);

        IEnumerable<MemberAttendanceRow> rows = data.AttendanceRows;

        if (classFilter.HasValue)
        {
            rows = rows.Where(r => r.GameClass == classFilter.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(r =>
                r.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(r.IngameNickname) && r.IngameNickname.Contains(term, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(r.ClassDisplayName) && r.ClassDisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        rows = tierFilter switch
        {
            "high" => rows.Where(r => r.AttendanceRatePercent >= 75),
            "mid" => rows.Where(r => r.AttendanceRatePercent >= 40 && r.AttendanceRatePercent < 75),
            "low" => rows.Where(r => r.AttendanceRatePercent < 40),
            _ => rows
        };

        var filteredRows = rows.ToList();
        var nowVn = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);

        using var workbook = new XLWorkbook();

        // Sheet 1: Bảng chuyên cần thành viên
        var sheet = workbook.Worksheets.Add("Bang chuyen can");
        WriteAttendanceSheet(sheet, guildName, data, filteredRows, nowVn);

        // Sheet 2: Danh sách sự kiện trong kỳ
        var eventsSheet = workbook.Worksheets.Add("Danh sach su kien");
        WriteEventsSheet(eventsSheet, data.Events);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);

        var slug = BuildSafeFileSlug(guildName);
        var rangeSlug = range switch
        {
            AnalyticsRange.Last30Days => "30ngay",
            AnalyticsRange.Last90Days => "90ngay",
            _ => "toanthoigian"
        };
        var fileName = $"chuyen-can-{slug}-{rangeSlug}-{nowVn:yyyyMMdd-HHmm}.xlsx";

        return new AnalyticsExcelResult(ms.ToArray(), fileName);
    }

    private static void WriteAttendanceSheet(
        IXLWorksheet sheet,
        string guildName,
        AnalyticsDashboardResult data,
        List<MemberAttendanceRow> rows,
        DateTimeOffset nowVn)
    {
        var rangeLabel = data.Range switch
        {
            AnalyticsRange.Last30Days => "30 ngày gần nhất",
            AnalyticsRange.Last90Days => "90 ngày gần nhất",
            _ => "Toàn thời gian"
        };

        var typeLabel = data.EventTypeFilter switch
        {
            EventType.GuildWar => "Bang chiến",
            EventType.Scrim => "Giao hữu (Scrim)",
            EventType.Other => "Hoạt động khác",
            _ => "Tất cả hoạt động"
        };

        sheet.Range("A1:K1").Merge();
        sheet.Cell("A1").Value = $"BÁO CÁO CHUYÊN CẦN BANG HỘI — {guildName.ToUpperInvariant()}";
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 13;
        sheet.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#1E293B");
        sheet.Row(1).Height = 26;

        sheet.Range("A2:K2").Merge();
        sheet.Cell("A2").Value = $"Kỳ thống kê: {rangeLabel}  |  Loại: {typeLabel}  |  Tổng số buổi: {data.TotalEvents}  |  Xuất lúc: {nowVn:dd/MM/yyyy HH:mm} (GMT+7)";
        sheet.Cell("A2").Style.Font.FontSize = 10;
        sheet.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#475569");
        sheet.Row(2).Height = 20;

        string[] headers =
        [
            "STT",
            "Tên / Nickname",
            "Tên Discord",
            "Môn phái",
            "Vai trò",
            "Đi",
            "Đi trễ",
            "Dự bị",
            "Bận / Vắng",
            "Không ĐK",
            "Chuyên cần (%)",
            "Lần tham gia gần nhất"
        ];

        const int headerRow = 4;
        for (var col = 0; col < headers.Length; col++)
        {
            var cell = sheet.Cell(headerRow, col + 1);
            cell.Value = headers[col];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4F46E5");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        sheet.Row(headerRow).Height = 24;

        for (var i = 0; i < rows.Count; i++)
        {
            var r = headerRow + 1 + i;
            var item = rows[i];

            sheet.Cell(r, 1).Value = i + 1;
            sheet.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            sheet.Cell(r, 2).Value = item.PrimaryName;
            sheet.Cell(r, 3).Value = item.DisplayName;
            sheet.Cell(r, 4).Value = item.ClassDisplayName ?? "Chưa liên kết";
            sheet.Cell(r, 5).Value = item.ClassRole switch
            {
                ClassRole.Tank => "Tank",
                ClassRole.Healer => "Healer",
                ClassRole.Dps => "DPS",
                _ => "—"
            };
            sheet.Cell(r, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            sheet.Cell(r, 6).Value = item.AttendedCount;
            sheet.Cell(r, 7).Value = item.LateCount;
            sheet.Cell(r, 8).Value = item.BenchCount;
            sheet.Cell(r, 9).Value = item.BusyCount;
            sheet.Cell(r, 10).Value = item.NoResponseCount;

            for (var c = 6; c <= 10; c++)
            {
                sheet.Cell(r, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            var rateCell = sheet.Cell(r, 11);
            rateCell.Value = item.AttendanceRatePercent / 100.0;
            rateCell.Style.NumberFormat.Format = "0.0%";
            rateCell.Style.Font.Bold = true;
            rateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            rateCell.Style.Font.FontColor = item.AttendanceRatePercent >= 75
                ? XLColor.FromHtml("#047857")
                : item.AttendanceRatePercent >= 40
                    ? XLColor.FromHtml("#B45309")
                    : XLColor.FromHtml("#BE123C");

            sheet.Cell(r, 12).Value = item.LastAttendedUtc.HasValue
                ? item.LastAttendedUtc.Value.ToOffset(VietnamOffset).ToString("dd/MM/yyyy HH:mm")
                : "Chưa tham gia";
            sheet.Cell(r, 12).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            if (i % 2 == 1)
            {
                sheet.Range(r, 1, r, 12).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
            }
        }

        var lastRow = Math.Max(headerRow + rows.Count, headerRow);
        var tableRange = sheet.Range(headerRow, 1, lastRow, 12);
        tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        tableRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
        tableRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#E2E8F0");

        if (rows.Count > 0)
        {
            tableRange.SetAutoFilter();
        }

        sheet.SheetView.FreezeRows(headerRow);

        // Độ rộng cột cố định — KHÔNG dùng AdjustToContents() để an toàn trên Linux Docker
        sheet.Column(1).Width = 7;
        sheet.Column(2).Width = 24;
        sheet.Column(3).Width = 22;
        sheet.Column(4).Width = 18;
        sheet.Column(5).Width = 11;
        sheet.Column(6).Width = 10;
        sheet.Column(7).Width = 10;
        sheet.Column(8).Width = 10;
        sheet.Column(9).Width = 12;
        sheet.Column(10).Width = 12;
        sheet.Column(11).Width = 16;
        sheet.Column(12).Width = 20;
    }

    private static void WriteEventsSheet(IXLWorksheet sheet, IReadOnlyList<AnalyticsEventSummaryRow> events)
    {
        string[] headers =
        [
            "STT",
            "Event ID",
            "Tiêu đề sự kiện",
            "Loại",
            "Thời gian (GMT+7)",
            "Tham gia",
            "Đi trễ",
            "Tổng Đi",
            "Dự bị",
            "Bận / Vắng",
            "Trạng thái"
        ];

        for (var col = 0; col < headers.Length; col++)
        {
            var cell = sheet.Cell(1, col + 1);
            cell.Value = headers[col];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#334155");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        sheet.Row(1).Height = 24;

        for (var i = 0; i < events.Count; i++)
        {
            var r = i + 2;
            var ev = events[i];

            sheet.Cell(r, 1).Value = i + 1;
            sheet.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            sheet.Cell(r, 2).Value = ev.WarEventId;
            sheet.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            sheet.Cell(r, 3).Value = ev.Title;
            sheet.Cell(r, 4).Value = ev.EventType switch
            {
                EventType.GuildWar => "Bang chiến",
                EventType.Scrim => "Giao hữu",
                _ => "Khác"
            };
            sheet.Cell(r, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            sheet.Cell(r, 5).Value = ev.EffectiveTimeUtc.ToOffset(VietnamOffset).ToString("dd/MM/yyyy HH:mm");
            sheet.Cell(r, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            sheet.Cell(r, 6).Value = ev.ConfirmedCount;
            sheet.Cell(r, 7).Value = ev.LateCount;
            sheet.Cell(r, 8).Value = ev.ConfirmedCount + ev.LateCount;
            sheet.Cell(r, 9).Value = ev.BenchCount;
            sheet.Cell(r, 10).Value = ev.BusyCount;

            for (var c = 6; c <= 10; c++)
            {
                sheet.Cell(r, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            sheet.Cell(r, 11).Value = ev.IsClosed ? "Đã đóng" : "Đang mở";
            sheet.Cell(r, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            if (i % 2 == 1)
            {
                sheet.Range(r, 1, r, 11).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
            }
        }

        var lastRow = Math.Max(events.Count + 1, 1);
        var tableRange = sheet.Range(1, 1, lastRow, 11);
        tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        tableRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
        tableRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#E2E8F0");
        sheet.SheetView.FreezeRows(1);

        sheet.Column(1).Width = 7;
        sheet.Column(2).Width = 11;
        sheet.Column(3).Width = 34;
        sheet.Column(4).Width = 14;
        sheet.Column(5).Width = 18;
        sheet.Column(6).Width = 11;
        sheet.Column(7).Width = 10;
        sheet.Column(8).Width = 11;
        sheet.Column(9).Width = 10;
        sheet.Column(10).Width = 12;
        sheet.Column(11).Width = 13;
    }

    private static EventTypeMetrics BuildEventTypeMetrics(EventType type, string label, List<WarEvent> events)
    {
        if (events.Count == 0)
        {
            return new EventTypeMetrics(type, label, 0, 0, 0, 0, 0, 0);
        }

        int totalAttended = 0;
        int totalBench = 0;
        int totalBusy = 0;
        int peak = 0;

        foreach (var ev in events)
        {
            var att = ev.Signups.Count(s => s.Status is SignupStatus.Confirmed or SignupStatus.Late);
            var ben = ev.Signups.Count(s => s.Status == SignupStatus.Bench);
            var busy = ev.Signups.Count(s => s.Status is SignupStatus.Absent or SignupStatus.NotParticipating);

            totalAttended += att;
            totalBench += ben;
            totalBusy += busy;
            if (att > peak) peak = att;
        }

        return new EventTypeMetrics(
            EventType: type,
            Label: label,
            EventCount: events.Count,
            TotalAttended: totalAttended,
            AverageAttended: Math.Round(totalAttended / (double)events.Count, 1),
            AverageBench: Math.Round(totalBench / (double)events.Count, 1),
            AverageBusy: Math.Round(totalBusy / (double)events.Count, 1),
            PeakAttended: peak);
    }

    private static DateTimeOffset GetEffectiveTimeUtc(WarEvent ev) => ev.EventTimeUtc ?? ev.CreatedAtUtc;

    private static DateOnly GetMondayOfWeekVn(DateTimeOffset utc)
    {
        var vnDate = DateOnly.FromDateTime(utc.ToOffset(VietnamOffset).DateTime);
        var offsetFromMonday = ((int)vnDate.DayOfWeek + 6) % 7;
        return vnDate.AddDays(-offsetFromMonday);
    }

    private static ClassRole ResolveClassRole(GameClass gc, Dictionary<GameClass, ResolvedClassConfig> classMap)
    {
        if (classMap.TryGetValue(gc, out var cfg))
        {
            return cfg.Role;
        }

        return ClassMetadata.TryGet(gc, out var meta) ? meta.Role : ClassRole.Dps;
    }

    private static (string DisplayName, string IconFileName, string HexColor, ClassRole Role) ResolveClassDisplay(
        GameClass gc,
        Dictionary<GameClass, ResolvedClassConfig> classMap)
    {
        if (classMap.TryGetValue(gc, out var cfg))
        {
            return (cfg.DisplayName, cfg.IconFileName, cfg.HexColor, cfg.Role);
        }

        if (ClassMetadata.TryGet(gc, out var meta))
        {
            return (meta.DisplayName, meta.FileName, meta.HexColor, meta.Role);
        }

        return ($"Hệ phái #{(int)gc}", "thamgia.png", "#6366F1", ClassRole.Dps);
    }

    private static string BuildSafeFileSlug(string input)
    {
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var prevDash = false;

        foreach (var ch in normalized)
        {
            var mapped = ch switch
            {
                'đ' or 'Đ' => 'd',
                _ => char.ToLowerInvariant(ch)
            };

            if ((mapped >= 'a' && mapped <= 'z') || (mapped >= '0' && mapped <= '9'))
            {
                sb.Append(mapped);
                prevDash = false;
            }
            else if (!prevDash && sb.Length > 0 && (char.IsWhiteSpace(mapped) || mapped is '-' or '_'))
            {
                sb.Append('-');
                prevDash = true;
            }
        }

        var result = sb.ToString().Trim('-');
        if (result.Length > 36) result = result[..36].Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "bang-hoi" : result;
    }

    private sealed class UserAccumulator
    {
        public ulong DiscordUserId { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string? IngameNickname { get; init; }
        public string? AvatarUrl { get; init; }
        public GameClass? GameClass { get; set; }
        public bool IsActiveGuildMember { get; init; }
        public int ConfirmedCount { get; set; }
        public int LateCount { get; set; }
        public int BenchCount { get; set; }
        public int BusyCount { get; set; }
        public DateTimeOffset? LastAttendedUtc { get; set; }
    }

    private sealed class WeekAccumulator(DateOnly weekStartVn)
    {
        public DateOnly WeekStartVn { get; } = weekStartVn;
        public int EventCount { get; set; }
        public int TotalAttended { get; set; }
        public int TotalBench { get; set; }
        public int TotalBusy { get; set; }
    }
}
