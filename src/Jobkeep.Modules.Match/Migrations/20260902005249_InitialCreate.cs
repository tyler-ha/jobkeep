using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Match.Migrations
{
    /// <summary>
    /// Phase 13.3b. One table in the `ats` schema, and the module that lost the most to
    /// the split.
    ///
    /// <para>
    /// Both of this table's foreign keys crossed a boundary — to job_applications and to
    /// resumes — so both are gone. The unique index on ApplicationId keeps the 1:1 that
    /// RunMatchCheck relies on when it overwrites rather than inserts; the RESTRICT on
    /// ResumeId has no replacement, which makes GetMatchResult's documented null-label
    /// case reachable for the first time.
    /// </para>
    ///
    /// <para>
    /// The five text[] columns are the reason this suite runs against real Postgres:
    /// no in-memory provider maps them.
    /// </para>
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ats");

            migrationBuilder.CreateTable(
                name: "ats_results",
                schema: "ats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResumeId = table.Column<Guid>(type: "uuid", nullable: true),
                    MatchedKeywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    MissingMustHaveKeywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    MissingNiceToHaveKeywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    UnmetRequirements = table.Column<List<string>>(type: "text[]", nullable: false),
                    FormattingRiskNotes = table.Column<List<string>>(type: "text[]", nullable: false),
                    Warning = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ats_results", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ats_results_ApplicationId",
                schema: "ats",
                table: "ats_results",
                column: "ApplicationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ats_results",
                schema: "ats");
        }
    }
}
