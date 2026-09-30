using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smurfm3u.Data.Migrations
{
    /// <inheritdoc />
    public partial class AirDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AirDate",
                table: "Items",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_AirDate",
                table: "Items",
                column: "AirDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Items_AirDate",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "AirDate",
                table: "Items");
        }
    }
}
