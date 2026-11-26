using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuildHub.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildClassConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guild_class_configs",
                columns: table => new
                {
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    game_class = table.Column<int>(type: "integer", nullable: false),
                    display_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    icon_file_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    discord_role_id = table.Column<long>(type: "bigint", nullable: true),
                    discord_role_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_class_configs", x => new { x.guild_id, x.game_class });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_class_configs");
        }
    }
}
