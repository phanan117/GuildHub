using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuildHub.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    scheduled_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    close_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    auto_create_form = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    auto_create_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trigger_form_now = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    target_channel_id = table.Column<long>(type: "bigint", nullable: true),
                    target_channel_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    mention_role_id = table.Column<long>(type: "bigint", nullable: true),
                    mention_role_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_war_event_id = table.Column<long>(type: "bigint", nullable: true),
                    form_created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_form_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_form_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_by_discord_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_scheduled_events_war_events_created_war_event_id",
                        column: x => x.created_war_event_id,
                        principalTable: "war_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_events_created_war_event_id",
                table: "scheduled_events",
                column: "created_war_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_events_guild_id_scheduled_time_utc",
                table: "scheduled_events",
                columns: new[] { "guild_id", "scheduled_time_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_events_is_cancelled_created_war_event_id_auto_cre~",
                table: "scheduled_events",
                columns: new[] { "is_cancelled", "created_war_event_id", "auto_create_form", "auto_create_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_events_is_cancelled_created_war_event_id_trigger_~",
                table: "scheduled_events",
                columns: new[] { "is_cancelled", "created_war_event_id", "trigger_form_now" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_events");
        }
    }
}
