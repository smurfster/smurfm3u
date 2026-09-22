using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smurfm3u.Data.Migrations
{
    /// <inheritdoc />
    public partial class XtreamIncrementalSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastFullRefreshAt",
                table: "Sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesId",
                table: "Items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SeriesLastModified",
                table: "Items",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_SourceId_SeriesId",
                table: "Items",
                columns: new[] { "SourceId", "SeriesId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Items_SourceId_SeriesId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "LastFullRefreshAt",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "SeriesLastModified",
                table: "Items");
        }
    }
}
