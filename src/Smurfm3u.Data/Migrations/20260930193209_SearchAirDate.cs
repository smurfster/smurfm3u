using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smurfm3u.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchAirDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AirDate",
                table: "Searches",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AirDate",
                table: "Searches");
        }
    }
}
