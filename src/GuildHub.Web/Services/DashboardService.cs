using GuildHub.Core.Data;
using GuildHub.Core.Domain.Entities;
using GuildHub.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace GuildHub.Web.Services;

public sealed record ClassCountItem(ResolvedClassConfig Info, int Count);

public sealed record LatestEventSummary(
    long EventId,
    string Title,
    int ActiveCount,
    int BusyOrAbsentCount,
    int TotalResponses,
    int ParticipationPercent);

public sealed record DashboardMetrics(
    GuildConfig? Config,
    int TotalActiveMembers,
    int LinkedMembers,
    int UnlinkedMembers,
    int RemovedMembers,
    int HasRoleCount,
    int LinkRatePercent,
    int RolePresencePercent,
    IReadOnlyList<ClassCountItem> ClassDistribution,
    LatestEventSummary? LatestEvent);

public sealed class DashboardService(
    IDbContextFactory<GuildHubDbContext> dbFactory,
    MemberManagementService memberService)
{
    public async Task<DashboardMetrics> GetDashboardAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var config = await db.GuildConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.GuildId == guildId, ct);

        var members = await db.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .ToListAsync(ct);

        var active = members.Where(m => !m.IsRemoved).ToList();
        var removedCount = members.Count - active.Count;
        var linkedCount = active.Count(m => m.GameClass.HasValue);
        var unlinkedCount = active.Count - linkedCount;
        var hasRoleCount = active.Count(m => m.HasGuildRole);

        var linkRate = active.Count > 0 ? (int)Math.Round(linkedCount * 100.0 / active.Count) : 0;
        var roleRate = active.Count > 0 ? (int)Math.Round(hasRoleCount * 100.0 / active.Count) : 0;

        var resolvedClasses = await memberService.GetResolvedClassConfigsAsync(guildId, ct);
        var classCounts = resolvedClasses
            .Select(info => new ClassCountItem(info, active.Count(m => m.GameClass == info.GameClass)))
            .ToList();

        var latestEventEntity = await db.WarEvents
            .AsNoTracking()
            .Include(e => e.Signups)
            .Where(e => e.GuildId == guildId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        LatestEventSummary? latestEvent = null;
        if (latestEventEntity is not null)
        {
            var activeSignups = latestEventEntity.Signups.Count(s =>
                s.Status is SignupStatus.Confirmed or SignupStatus.Late or SignupStatus.Bench);
            var busySignups = latestEventEntity.Signups.Count(s =>
                s.Status is SignupStatus.Absent or SignupStatus.NotParticipating);
            var totalResponses = activeSignups + busySignups;
            var participationPct = totalResponses > 0
                ? (int)Math.Round(activeSignups * 100.0 / totalResponses)
                : 0;

            latestEvent = new LatestEventSummary(
                EventId: latestEventEntity.Id,
                Title: latestEventEntity.Title,
                ActiveCount: activeSignups,
                BusyOrAbsentCount: busySignups,
                TotalResponses: totalResponses,
                ParticipationPercent: participationPct);
        }

        return new DashboardMetrics(
            Config: config,
            TotalActiveMembers: active.Count,
            LinkedMembers: linkedCount,
            UnlinkedMembers: unlinkedCount,
            RemovedMembers: removedCount,
            HasRoleCount: hasRoleCount,
            LinkRatePercent: linkRate,
            RolePresencePercent: roleRate,
            ClassDistribution: classCounts,
            LatestEvent: latestEvent);
    }
}
