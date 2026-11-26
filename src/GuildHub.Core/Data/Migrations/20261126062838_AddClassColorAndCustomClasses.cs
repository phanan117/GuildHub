using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuildHub.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClassColorAndCustomClasses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "hex_color",
                table: "guild_class_configs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "#6366F1");

            migrationBuilder.AddColumn<bool>(
                name: "is_custom",
                table: "guild_class_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "role",
                table: "guild_class_configs",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "sort_order",
                table: "guild_class_configs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hex_color",
                table: "guild_class_configs");

            migrationBuilder.DropColumn(
                name: "is_custom",
                table: "guild_class_configs");

            migrationBuilder.DropColumn(
                name: "role",
                table: "guild_class_configs");

            migrationBuilder.DropColumn(
                name: "sort_order",
                table: "guild_class_configs");
        }
    }
}
