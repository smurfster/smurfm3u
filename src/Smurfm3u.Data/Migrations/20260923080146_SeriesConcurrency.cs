using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smurfm3u.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeriesConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SeriesConcurrency",
                table: "Sources",
                type: "integer",
                nullable: false,
                // Four, not zero. A playlist that predates this column was asking for the old
                // fixed rate of four, and zero clamps to one - which would quietly turn every
                // existing panel refresh into the slowest kind it can do.
                defaultValue: 4);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeriesConcurrency",
                table: "Sources");
        }
    }
}
