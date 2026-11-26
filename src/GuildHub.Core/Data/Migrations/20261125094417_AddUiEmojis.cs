using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuildHub.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUiEmojis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ui_emojis",
                columns: table => new
                {
                    ui_icon = table.Column<int>(type: "integer", nullable: false),
                    emoji_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    emoji_id = table.Column<long>(type: "bigint", nullable: false),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    synced_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ui_emojis", x => x.ui_icon);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ui_emojis");
        }
    }
}

