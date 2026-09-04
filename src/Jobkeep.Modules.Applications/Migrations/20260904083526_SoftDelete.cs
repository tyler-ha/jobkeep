using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Applications.Migrations
{
    /// <summary>
    /// PHASE 8 — soft delete for job_applications and job_postings.
    ///
    /// The four columns are scaffolded. The three CREATE OR REPLACE VIEW
    /// statements at the bottom are hand-written, and they are the half of this
    /// migration that matters: a global query filter is an EF construct, and a
    /// view is SQL that Postgres executes on its own. Without this block every
    /// Insights figure would keep counting archived applications, and it would do
    /// so silently — the C# reads the view, the view reads the table, and there
    /// is no point in between where a filter could have been visibly forgotten.
    ///
    /// The general trap, worth stating once: HasQueryFilter protects LINQ. Raw
    /// SQL, views, functions and ExecuteUpdate/ExecuteDelete all walk past it.
    /// </summary>
    public partial class SoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                schema: "applications",
                table: "job_postings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "applications",
                table: "job_postings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                schema: "applications",
                table: "job_applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "applications",
                table: "job_applications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // -----------------------------------------------------------------
            // The three published views, re-cut to exclude archived rows
            // -----------------------------------------------------------------
            // CREATE OR REPLACE rather than DROP + CREATE: the column list and
            // the types are unchanged, which is exactly the case REPLACE allows,
            // and it keeps each view's identity (and any grants on it) instead of
            // rebuilding it. A shape change would have needed the DROP.
            //
            // The PAYLOAD SHAPES in Jobkeep.Contracts/Applications/PublishedViews.cs
            // are untouched, deliberately. Nothing about Analytics' contract
            // changed — "applications per company" still means what it said. What
            // changed is which rows are applications, and that is the publisher's
            // business. It is also the argument for a published view over a
            // contract method per question, demonstrating itself: this is a
            // semantic change to three read models that required no consumer edit.

            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW applications.v_application_status_counts AS
                SELECT a."Status"      AS "Status",
                       COUNT(*)::int   AS "Count"
                FROM applications.job_applications a
                WHERE NOT a."IsDeleted"
                GROUP BY a."Status";
                """);

            // Two predicates, not one, and the second is the easy one to miss: an
            // application can be live while the ad it names is archived. Archiving
            // an ad is refused only while a LIVE application names it — so archive
            // the application first and the ad becomes archivable, and that pair
            // is a legal state. Counting through an archived ad here would credit
            // a company for an ad the user has put away.
            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW applications.v_company_application_counts AS
                SELECT c."Name"        AS "CompanyName",
                       COUNT(*)::int   AS "ApplicationCount"
                FROM applications.job_applications a
                JOIN applications.job_postings p ON p."Id" = a."PostingId"
                JOIN applications.companies    c ON c."Id" = p."CompanyId"
                WHERE NOT a."IsDeleted" AND NOT p."IsDeleted"
                GROUP BY c."Name";
                """);

            // This one gains a JOIN it did not have. posting_skills carries no
            // IsDeleted of its own — link rows are not archivable, on purpose —
            // so the only way to ask whether the ad is still live is to go and
            // look at it.
            //
            // The join stops at job_postings and does NOT continue to
            // job_applications, which preserves what this view has always
            // measured: demand as expressed by the ADS, not by how many times you
            // applied. An ad you never applied to still counts here, and that is
            // the point of the skill-demand chart.
            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW applications.v_posting_skill_demand AS
                SELECT ps."SkillId"    AS "SkillId",
                       COUNT(*)::int   AS "PostingCount"
                FROM applications.posting_skills ps
                JOIN applications.job_postings p ON p."Id" = ps."PostingId"
                WHERE NOT p."IsDeleted"
                GROUP BY ps."SkillId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Views first, back to their InitialCreate text — they reference the
            // columns dropped below, so replacing them afterwards would fail.
            //
            // The bodies are copied from InitialCreate rather than shared, and
            // that is correct rather than lazy: a migration has to keep working
            // when the file it copied from is a hundred migrations away, so a
            // migration that called into shared SQL would change meaning whenever
            // that SQL did. Copy-paste is the right answer in exactly this place.
            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW applications.v_application_status_counts AS
                SELECT a."Status"      AS "Status",
                       COUNT(*)::int   AS "Count"
                FROM applications.job_applications a
                GROUP BY a."Status";
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW applications.v_company_application_counts AS
                SELECT c."Name"        AS "CompanyName",
                       COUNT(*)::int   AS "ApplicationCount"
                FROM applications.job_applications a
                JOIN applications.job_postings p ON p."Id" = a."PostingId"
                JOIN applications.companies    c ON c."Id" = p."CompanyId"
                GROUP BY c."Name";
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW applications.v_posting_skill_demand AS
                SELECT ps."SkillId"    AS "SkillId",
                       COUNT(*)::int   AS "PostingCount"
                FROM applications.posting_skills ps
                GROUP BY ps."SkillId";
                """);

            // Archived rows become live again on the way down, because there is
            // nowhere else for them to go: the flag is the only record that they
            // were ever archived, and dropping it without them reappearing would
            // leave the user's list quietly short with no way to find what is
            // missing. A down migration should lose the FEATURE, not the data.
            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                schema: "applications",
                table: "job_postings");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "applications",
                table: "job_postings");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                schema: "applications",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "applications",
                table: "job_applications");
        }
    }
}
