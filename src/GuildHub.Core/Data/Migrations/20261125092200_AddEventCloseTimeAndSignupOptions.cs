using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuildHub.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventCloseTimeAndSignupOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "close_time_utc",
                table: "war_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "mention_role_id",
                table: "war_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "options_enabled",
                table: "event_signups",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_war_events_is_closed_close_time_utc",
                table: "war_events",
                columns: new[] { "is_closed", "close_time_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_war_events_is_closed_close_time_utc",
                table: "war_events");

            migrationBuilder.DropColumn(
                name: "close_time_utc",
                table: "war_events");

            migrationBuilder.DropColumn(
                name: "mention_role_id",
                table: "war_events");

            migrationBuilder.DropColumn(
                name: "options_enabled",
                table: "event_signups");
        }
    }
}

