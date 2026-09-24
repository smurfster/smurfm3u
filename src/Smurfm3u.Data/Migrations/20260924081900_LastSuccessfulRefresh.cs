using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smurfm3u.Data.Migrations
{
    /// <inheritdoc />
    public partial class LastSuccessfulRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSuccessfulRefreshAt",
                table: "Sources",
                type: "timestamp with time zone",
                nullable: true);

            // A playlist whose last run succeeded already knows when that was, so it starts
            // with the right answer rather than with "never" until it next refreshes. Status 2
            // is RefreshStatus.Success. One that last failed is left null, which is honest: we
            // cannot know when it last worked, only that this was not it.
            migrationBuilder.Sql(
                """
                UPDATE "Sources"
                SET "LastSuccessfulRefreshAt" = "LastRefreshCompletedAt"
                WHERE "LastRefreshStatus" = 2 AND "LastRefreshCompletedAt" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSuccessfulRefreshAt",
                table: "Sources");
        }
    }
}
