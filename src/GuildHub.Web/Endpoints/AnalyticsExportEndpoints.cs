using System.Security.Claims;
using System.Text.Json;
using GuildHub.Core.Domain.Enums;
using GuildHub.Web.Auth;
using GuildHub.Web.Services;

namespace GuildHub.Web.Endpoints;

public static class AnalyticsExportEndpoints
{
    public static void MapAnalyticsExportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/analytics/export-excel", async (
            HttpContext http,
            AnalyticsService analyticsService,
            CancellationToken ct) =>
        {
            var guildIdStr = http.Request.Query["guildId"].ToString();
            if (!ulong.TryParse(guildIdStr, out var guildId) || guildId == 0)
            {
                return Results.BadRequest("Thiếu guildId hợp lệ.");
            }

            var guildsJson = http.User.FindFirstValue(DiscordClaimTypes.AuthorizedGuilds);
            var authorizedGuilds = string.IsNullOrWhiteSpace(guildsJson)
                ? []
                : JsonSerializer.Deserialize<List<AuthorizedGuildInfo>>(guildsJson) ?? [];

            var targetGuild = authorizedGuilds.FirstOrDefault(g => g.GuildId == guildId);
            if (targetGuild is null)
            {
                return Results.Forbid();
            }

            var range = http.Request.Query["range"].ToString() switch
            {
                "90" => AnalyticsRange.Last90Days,
                "0" or "all" => AnalyticsRange.AllTime,
                _ => AnalyticsRange.Last30Days
            };

            EventType? eventType = int.TryParse(http.Request.Query["eventType"], out var etInt) && etInt is 1 or 2 or 3
                ? (EventType)etInt
                : null;

            // KHÔNG dùng Enum.IsDefined vì hỗ trợ hệ phái tùy chỉnh (id 9, 10, ...)
            GameClass? gameClass = int.TryParse(http.Request.Query["gameClass"], out var gcInt) && gcInt > 0
                ? (GameClass)gcInt
                : null;

            var search = http.Request.Query["search"].ToString();
            var tier = http.Request.Query["tier"].ToString();

            var file = await analyticsService.ExportAttendanceExcelAsync(
                guildId: guildId,
                guildName: targetGuild.Name,
                range: range,
                eventTypeFilter: eventType,
                classFilter: gameClass,
                search: string.IsNullOrWhiteSpace(search) ? null : search,
                tierFilter: string.IsNullOrWhiteSpace(tier) ? null : tier,
                ct: ct);

            return Results.File(
                file.Content,
                contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileDownloadName: file.FileName);
        })
        .RequireAuthorization();
    }
}
