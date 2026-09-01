using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Migrations
{
    /// <summary>
    /// Phase 13.2. The three read models Applications publishes to Analytics.
    ///
    /// Additive and reversible: no table, column, index or constraint moves, so
    /// this migration can be rolled back without touching a row. That is the
    /// property 13.2 is built around — the logical decoupling lands first and on
    /// its own, and 13.3 does the physical split without also being the step that
    /// changes behaviour.
    ///
    /// Hand-written, because EF does not scaffold a view. The keyless types are
    /// mapped with HasNoKey().ToView(...) in AppDbContext, which deliberately
    /// leaves them OUT of the migration model — so `migrations add` produced an
    /// empty Up/Down and the SQL below is the whole change. The consequence worth
    /// knowing: a later column rename will not break these automatically, and
    /// nothing in the build will say so. AnalyticsTests compares every stat
    /// against hand-written SQL over the base tables, which is what catches it.
    ///
    /// Every COUNT(*) is cast to int. Postgres counts in bigint and the CLR
    /// properties are int, so without the cast Npgsql refuses the mapping at
    /// read time rather than at build time.
    ///
    /// Column names are quoted PascalCase to match the entity properties, the
    /// same convention every table in this schema already uses.
    /// </summary>
    public partial class AnalyticsViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The funnel. Stages with no applications have no row to group and
            // so are absent here; StatusFunnel zero-fills from the enum, which is
            // O(stages) rather than O(rows).
            migrationBuilder.Sql("""
                CREATE VIEW v_application_status_counts AS
                SELECT a."Status"      AS "Status",
                       COUNT(*)::int   AS "Count"
                FROM job_applications a
                GROUP BY a."Status";
                """);

            // Grouped from the application side, not by walking companies: one
            // GROUP BY with two joins, where starting from `companies` would be a
            // correlated subquery per company row. A company with postings but no
            // applications therefore does not appear — unreachable today, since
            // companies are only ever created by CreateApplication's
            // find-or-create, and a LEFT JOIN here is the fix if that changes.
            migrationBuilder.Sql("""
                CREATE VIEW v_company_application_counts AS
                SELECT c."Name"        AS "CompanyName",
                       COUNT(*)::int   AS "ApplicationCount"
                FROM job_applications a
                JOIN job_postings p ON p."Id" = a."PostingId"
                JOIN companies    c ON c."Id" = p."CompanyId"
                GROUP BY c."Name";
                """);

            // Stops at SkillId ON PURPOSE. `skills` is another module's table, so
            // joining it here would move the cross-module read out of C#, where a
            // compiler can see it, into SQL where nothing can. Analytics resolves
            // the ids through ISkillCatalog — see SkillDemand.cs for what that
            // costs, which is one bounded second query and a tiebreak that is now
            // within-page.
            //
            // The composite PK on posting_skills makes this a count of POSTINGS:
            // one posting contributes one row however many times you applied.
            migrationBuilder.Sql("""
                CREATE VIEW v_posting_skill_demand AS
                SELECT ps."SkillId"    AS "SkillId",
                       COUNT(*)::int   AS "PostingCount"
                FROM posting_skills ps
                GROUP BY ps."SkillId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS v_posting_skill_demand;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS v_company_application_counts;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS v_application_status_counts;");
        }
    }
}
