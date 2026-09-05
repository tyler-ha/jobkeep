using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Applications.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// PHASE 11.2b — OwnerUserId on the three tables Applications owns, and the
    /// three published views re-cut to carry it.
    ///
    /// EXISTING ROWS ARE ORPHANED, DELIBERATELY. The column lands NOT NULL with
    /// a default of the empty Guid, which no user id can ever be, so anything
    /// written before this migration belongs to nobody and is invisible to
    /// everyone. There is no honest backfill available: rows created before
    /// authentication existed have no owner to recover, and guessing one — "the
    /// only account in the table" — would be a rule that silently hands one
    /// user's data to whoever happened to register first. The dev database is
    /// disposable (docker compose down -v) and there is no deployment, so the
    /// cost of orphaning is zero and the cost of guessing is a bad habit.
    ///
    /// THE VIEWS ARE THE HALF THAT MATTERS, exactly as in Phase 8's SoftDelete:
    /// a query filter is an EF construct and a view is SQL Postgres runs on its
    /// own. Without this block every Insights figure would silently aggregate
    /// across all users. CREATE OR REPLACE still works because a new column is
    /// APPENDED to each column list, which is the one shape change Postgres
    /// allows a replace to make.
    /// </summary>
    public partial class OwnerScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_companies_NameNormalized",
                schema: "applications",
                table: "companies");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                schema: "applications",
                table: "job_postings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                schema: "applications",
                table: "job_applications",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                schema: "applications",
                table: "companies",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_companies_OwnerUserId_NameNormalized",
                schema: "applications",
                table: "companies",
                columns: new[] { "OwnerUserId", "NameNormalized" },
                unique: true);

            // -----------------------------------------------------------------
            // The three published views, re-cut to group by owner
            // -----------------------------------------------------------------
            // Each gains OwnerUserId in the SELECT and the GROUP BY, and
            // AnalyticsDbContext filters on it — the same division of labour the
            // tables have, with the column standing in for the query filter that
            // cannot reach raw SQL.
            //
            // The Analytics SLICES did not change, and that is the view
            // abstraction paying for itself a second time: Phase 8 changed which
            // rows are applications and needed no consumer edit; this changes
            // whose they are, and needs none either.

            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW applications.v_application_status_counts AS
                SELECT a."Status"        AS "Status",
                       COUNT(*)::int     AS "Count",
                       a."OwnerUserId"   AS "OwnerUserId"
                FROM applications.job_applications a
                WHERE NOT a."IsDeleted"
                GROUP BY a."Status", a."OwnerUserId";
                """);

            // Grouped on the APPLICATION's owner, not the company's. They are
            // always the same today — a company row is created by the person who
            // first applied there — but the question this view answers is "how
            // many applications did I send", so the application is the row whose
            // owner decides.
            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW applications.v_company_application_counts AS
                SELECT c."Name"          AS "CompanyName",
                       COUNT(*)::int     AS "ApplicationCount",
                       a."OwnerUserId"   AS "OwnerUserId"
                FROM applications.job_applications a
                JOIN applications.job_postings p ON p."Id" = a."PostingId"
                JOIN applications.companies    c ON c."Id" = p."CompanyId"
                WHERE NOT a."IsDeleted" AND NOT p."IsDeleted"
                GROUP BY c."Name", a."OwnerUserId";
                """);

            // The POSTING's owner here, because this view has always measured
            // demand as expressed by the ads rather than by how many times you
            // applied — the join deliberately stops at job_postings, so the
            // posting is the only owned row in scope.
            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW applications.v_posting_skill_demand AS
                SELECT ps."SkillId"      AS "SkillId",
                       COUNT(*)::int     AS "PostingCount",
                       p."OwnerUserId"   AS "OwnerUserId"
                FROM applications.posting_skills ps
                JOIN applications.job_postings p ON p."Id" = ps."PostingId"
                WHERE NOT p."IsDeleted"
                GROUP BY ps."SkillId", p."OwnerUserId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Views first — they name the column dropped below. Bodies copied
            // from the SoftDelete migration rather than shared, for the reason
            // that one gives: a migration has to keep working when the file it
            // copied from is a hundred migrations away.
            //
            // DROP + CREATE here, where Up used CREATE OR REPLACE, and the
            // asymmetry is Postgres': a replace may APPEND a column and may not
            // remove one. Going up adds OwnerUserId to the end of each list;
            // coming down takes it away, so each view has to be rebuilt.
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS applications.v_application_status_counts;
                DROP VIEW IF EXISTS applications.v_company_application_counts;
                DROP VIEW IF EXISTS applications.v_posting_skill_demand;
                """);
            migrationBuilder.Sql("""
                CREATE VIEW applications.v_application_status_counts AS
                SELECT a."Status"      AS "Status",
                       COUNT(*)::int   AS "Count"
                FROM applications.job_applications a
                WHERE NOT a."IsDeleted"
                GROUP BY a."Status";
                """);

            migrationBuilder.Sql("""
                CREATE VIEW applications.v_company_application_counts AS
                SELECT c."Name"        AS "CompanyName",
                       COUNT(*)::int   AS "ApplicationCount"
                FROM applications.job_applications a
                JOIN applications.job_postings p ON p."Id" = a."PostingId"
                JOIN applications.companies    c ON c."Id" = p."CompanyId"
                WHERE NOT a."IsDeleted" AND NOT p."IsDeleted"
                GROUP BY c."Name";
                """);

            migrationBuilder.Sql("""
                CREATE VIEW applications.v_posting_skill_demand AS
                SELECT ps."SkillId"    AS "SkillId",
                       COUNT(*)::int   AS "PostingCount"
                FROM applications.posting_skills ps
                JOIN applications.job_postings p ON p."Id" = ps."PostingId"
                WHERE NOT p."IsDeleted"
                GROUP BY ps."SkillId";
                """);

            migrationBuilder.DropIndex(
                name: "IX_companies_OwnerUserId_NameNormalized",
                schema: "applications",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "applications",
                table: "job_postings");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "applications",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "applications",
                table: "companies");

            migrationBuilder.CreateIndex(
                name: "IX_companies_NameNormalized",
                schema: "applications",
                table: "companies",
                column: "NameNormalized",
                unique: true);
        }
    }
}
