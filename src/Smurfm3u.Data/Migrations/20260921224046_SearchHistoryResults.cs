using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smurfm3u.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchHistoryResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The default is required rather than cosmetic: the column is NOT NULL and the
            // table already holds rows recorded before there was anywhere to put results.
            // They read as an empty array instead of blocking the migration.
            migrationBuilder.AddColumn<List<long>>(
                name: "ResultItemIds",
                table: "Searches",
                type: "bigint[]",
                nullable: false,
                defaultValueSql: "'{}'::bigint[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultItemIds",
                table: "Searches");
        }
    }
}
