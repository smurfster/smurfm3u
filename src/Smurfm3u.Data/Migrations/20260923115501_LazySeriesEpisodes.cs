using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Smurfm3u.Data.Migrations
{
    /// <inheritdoc />
    public partial class LazySeriesEpisodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceId = table.Column<int>(type: "integer", nullable: false),
                    SeriesId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SearchTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    GroupTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Cover = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LastModified = table.Column<long>(type: "bigint", nullable: true),
                    EpisodesFetchedFor = table.Column<long>(type: "bigint", nullable: true),
                    EpisodesFetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Series_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Series_SearchTitle",
                table: "Series",
                column: "SearchTitle")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Series_SourceId_SeriesId",
                table: "Series",
                columns: new[] { "SourceId", "SeriesId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Series");
        }
    }
}
