using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuildHub.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "class_emojis",
                columns: table => new
                {
                    game_class = table.Column<int>(type: "integer", nullable: false),
                    emoji_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    emoji_id = table.Column<long>(type: "bigint", nullable: false),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    synced_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_class_emojis", x => x.game_class);
                });

            migrationBuilder.CreateTable(
                name: "war_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    event_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    leader_discord_id = table.Column<long>(type: "bigint", nullable: false),
                    is_closed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    next_signup_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_war_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "event_signups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    war_event_id = table.Column<long>(type: "bigint", nullable: false),
                    discord_user_id = table.Column<long>(type: "bigint", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    game_class = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    signup_order = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    signed_up_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_signups", x => x.id);
                    table.ForeignKey(
                        name: "FK_event_signups_war_events_war_event_id",
                        column: x => x.war_event_id,
                        principalTable: "war_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_event_signups_war_event_id_discord_user_id",
                table: "event_signups",
                columns: new[] { "war_event_id", "discord_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_signups_war_event_id_status_signup_order",
                table: "event_signups",
                columns: new[] { "war_event_id", "status", "signup_order" });

            migrationBuilder.CreateIndex(
                name: "IX_war_events_guild_id_created_at_utc",
                table: "war_events",
                columns: new[] { "guild_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_war_events_message_id",
                table: "war_events",
                column: "message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "class_emojis");

            migrationBuilder.DropTable(
                name: "event_signups");

            migrationBuilder.DropTable(
                name: "war_events");
        }
    }
}

