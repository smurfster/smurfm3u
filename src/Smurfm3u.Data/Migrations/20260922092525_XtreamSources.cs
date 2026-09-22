using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smurfm3u.Data.Migrations
{
    /// <inheritdoc />
    public partial class XtreamSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeSeries",
                table: "Sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Password",
                table: "Sources",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "Sources",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludeSeries",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Password",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "Sources");
        }
    }
}
