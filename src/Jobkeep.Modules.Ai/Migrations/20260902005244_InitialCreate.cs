using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Ai.Migrations
{
    /// <summary>
    /// Phase 13.3b. One table in the `ai` schema.
    ///
    /// <para>
    /// The FK to job_postings is gone, and the unique index on PostingId is what
    /// replaces the half of it that mattered locally: the 1:1 used to be a side effect
    /// of HasForeignKey on a one-to-one relationship, and would otherwise have vanished
    /// with it, silently. AnalyzePosting's update-or-insert assumes at most one row per
    /// posting.
    /// </para>
    ///
    /// <para>
    /// What is NOT replaced here is the CASCADE. Deleting a posting no longer deletes
    /// its analysis, and 13.3c adds the notification that does. DeleteBehaviourTests
    /// pins the orphan in the meantime rather than leaving it undocumented.
    /// </para>
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.CreateTable(
                name: "ai_analyses",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seniority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ModelUsed = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AnalyzedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_analyses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_analyses_PostingId",
                schema: "ai",
                table: "ai_analyses",
                column: "PostingId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_analyses",
                schema: "ai");
        }
    }
}
