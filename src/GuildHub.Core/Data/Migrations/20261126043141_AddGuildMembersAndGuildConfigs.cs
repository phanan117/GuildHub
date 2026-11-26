using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuildHub.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildMembersAndGuildConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guild_configs",
                columns: table => new
                {
                    guild_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    icon_url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    member_role_id = table.Column<long>(type: "bigint", nullable: true),
                    member_role_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_synced_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_synced_member_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_configs", x => x.guild_id);
                });

            migrationBuilder.CreateTable(
                name: "guild_members",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    discord_user_id = table.Column<long>(type: "bigint", nullable: true),
                    discord_username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ingame_nickname = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    avatar_url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    game_class = table.Column<int>(type: "integer", nullable: true),
                    has_guild_role = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    is_removed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_external = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    removed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_members", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_guild_members_guild_id_discord_user_id",
                table: "guild_members",
                columns: new[] { "guild_id", "discord_user_id" },
                unique: true,
                filter: "discord_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_guild_members_guild_id_game_class",
                table: "guild_members",
                columns: new[] { "guild_id", "game_class" });

            migrationBuilder.CreateIndex(
                name: "IX_guild_members_guild_id_is_removed",
                table: "guild_members",
                columns: new[] { "guild_id", "is_removed" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_configs");

            migrationBuilder.DropTable(
                name: "guild_members");
        }
    }
}
