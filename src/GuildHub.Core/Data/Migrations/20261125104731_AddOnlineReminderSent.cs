using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuildHub.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOnlineReminderSent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "online_reminder_sent",
                table: "war_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_war_events_online_reminder_sent_event_time_utc",
                table: "war_events",
                columns: new[] { "online_reminder_sent", "event_time_utc" });

            migrationBuilder.Sql(
                "UPDATE war_events SET online_reminder_sent = true WHERE event_time_utc IS NOT NULL AND event_time_utc <= NOW();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_war_events_online_reminder_sent_event_time_utc",
                table: "war_events");

            migrationBuilder.DropColumn(
                name: "online_reminder_sent",
                table: "war_events");
        }
    }
}

