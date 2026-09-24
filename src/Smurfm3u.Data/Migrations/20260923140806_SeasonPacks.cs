using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Smurfm3u.Data.Migrations
{
    /// <summary>
    /// Moves everything that varies per file off a grab and onto files belonging to it, so one
    /// grab can carry a whole season. Existing grabs become one file each, which is exactly
    /// what they already were.
    /// </summary>
    public partial class SeasonPacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DownloadFiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DownloadId = table.Column<long>(type: "bigint", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Extension = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    M3uItemId = table.Column<long>(type: "bigint", nullable: true),
                    StreamUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    DownloadedBytes = table.Column<long>(type: "bigint", nullable: false),
                    IncompletePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadFiles_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DownloadFiles_Items_M3uItemId",
                        column: x => x.M3uItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadFiles_DownloadId_Position",
                table: "DownloadFiles",
                columns: new[] { "DownloadId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadFiles_M3uItemId",
                table: "DownloadFiles",
                column: "M3uItemId");

            // Carried across before the columns go, because they are the only record of what
            // every existing grab was downloading. A grab in the queue right now keeps its
            // partial file: the path it resumes from is derived the same way either side of
            // this migration.
            //
            // Status 3 is DownloadStatus.Completed and 1 is DownloadFileStatus.Completed;
            // anything else becomes Pending, which is what a retry would want.
            migrationBuilder.Sql(
                """
                INSERT INTO "DownloadFiles"
                    ("DownloadId", "Position", "Name", "Extension", "M3uItemId", "StreamUrl",
                     "Status", "TotalBytes", "DownloadedBytes", "IncompletePath", "FailureMessage")
                SELECT d."Id",
                       0,
                       d."Name",
                       COALESCE(NULLIF(i."Extension", ''), 'mp4'),
                       d."M3uItemId",
                       d."StreamUrl",
                       CASE WHEN d."Status" = 3 THEN 1 ELSE 0 END,
                       d."TotalBytes",
                       d."DownloadedBytes",
                       d."IncompletePath",
                       NULL
                FROM "Downloads" d
                LEFT JOIN "Items" i ON i."Id" = d."M3uItemId"
                WHERE d."StreamUrl" <> '';
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Downloads_Items_M3uItemId",
                table: "Downloads");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_M3uItemId",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "M3uItemId",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "StreamUrl",
                table: "Downloads");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "M3uItemId",
                table: "Downloads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StreamUrl",
                table: "Downloads",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            // Back the way it came. A grab that carried several files keeps only its first,
            // because the old shape has nowhere to put the rest.
            migrationBuilder.Sql(
                """
                UPDATE "Downloads" d
                SET "M3uItemId" = f."M3uItemId",
                    "StreamUrl" = f."StreamUrl"
                FROM (
                    SELECT DISTINCT ON ("DownloadId") "DownloadId", "M3uItemId", "StreamUrl"
                    FROM "DownloadFiles"
                    ORDER BY "DownloadId", "Position"
                ) f
                WHERE f."DownloadId" = d."Id";
                """);

            migrationBuilder.DropTable(
                name: "DownloadFiles");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_M3uItemId",
                table: "Downloads",
                column: "M3uItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_Downloads_Items_M3uItemId",
                table: "Downloads",
                column: "M3uItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
