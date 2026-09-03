using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Applications.Migrations
{
    /// <summary>
    /// Phase 13.3b. Applications' schema, from scratch.
    ///
    /// <para>
    /// This replaces five migrations that described ONE schema for thirteen tables and
    /// could not be split. Nothing is deployed (Phase 10 is parked), so the reset was
    /// the cheap option and the dev database was dropped deliberately rather than
    /// carried over. What it buys is the thing 13.3 is for: five histories that can be
    /// applied in any order, because the split removed every foreign key that crossed
    /// a schema.
    /// </para>
    ///
    /// <para>
    /// Everything above the views is scaffolded. The three CREATE VIEW statements at
    /// the bottom are HAND-WRITTEN, because EF does not scaffold a view: the keyless
    /// types are mapped with HasNoKey().ToView(...) in AnalyticsDbContext, which leaves
    /// them out of the migration model entirely. The consequence worth knowing is that
    /// a later column rename will not update them and nothing in the build will say so.
    /// AnalyticsTests compares every stat against hand-written SQL over the base
    /// tables, which is what catches it.
    /// </para>
    ///
    /// <para>
    /// The views are in APPLICATIONS' migration and not in Analytics', and that is the
    /// publisher-owns-the-definition half of the split: Analytics decides how it reads
    /// them, Applications decides what they mean. Analytics owns no tables and has no
    /// migrations at all.
    /// </para>
    ///
    /// <para>
    /// Every COUNT(*) is cast to int. Postgres counts in bigint and the CLR properties
    /// are int, so without the cast Npgsql refuses the mapping at read time rather than
    /// at build time.
    /// </para>
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "applications");

            migrationBuilder.CreateTable(
                name: "companies",
                schema: "applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Website = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Industry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    HqLocation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NameNormalized = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, computedColumnSql: "lower(\"Name\")", stored: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "job_postings",
                schema: "applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EmploymentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SalaryMin = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    SalaryMax = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    SalaryCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SalaryPeriod = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Description = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    PostedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_postings", x => x.Id);
                    table.CheckConstraint("ck_job_postings_currency_iso4217", "\"SalaryCurrency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_job_postings_salary_range", "\"SalaryMin\" IS NULL OR \"SalaryMax\" IS NULL OR \"SalaryMin\" <= \"SalaryMax\"");
                    table.ForeignKey(
                        name: "FK_job_postings_companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "applications",
                        principalTable: "companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_applications",
                schema: "applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DateApplied = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    ResumeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_applications_job_postings_PostingId",
                        column: x => x.PostingId,
                        principalSchema: "applications",
                        principalTable: "job_postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_requirements",
                schema: "applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsMustHave = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_requirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_requirements_job_postings_PostingId",
                        column: x => x.PostingId,
                        principalSchema: "applications",
                        principalTable: "job_postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "posting_skills",
                schema: "applications",
                columns: table => new
                {
                    PostingId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_posting_skills", x => new { x.PostingId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_posting_skills_job_postings_PostingId",
                        column: x => x.PostingId,
                        principalSchema: "applications",
                        principalTable: "job_postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_companies_NameNormalized",
                schema: "applications",
                table: "companies",
                column: "NameNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_DateApplied",
                schema: "applications",
                table: "job_applications",
                column: "DateApplied",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_PostingId",
                schema: "applications",
                table: "job_applications",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_Status",
                schema: "applications",
                table: "job_applications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_job_postings_CompanyId",
                schema: "applications",
                table: "job_postings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_job_requirements_PostingId",
                schema: "applications",
                table: "job_requirements",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_posting_skills_SkillId",
                schema: "applications",
                table: "posting_skills",
                column: "SkillId");

            // ---------------------------------------------------------------
            // The three read models Applications publishes to Analytics
            // ---------------------------------------------------------------
            // Both the view and the tables it reads are schema-qualified. They
            // happen to be the same schema here, and saying so anyway is what
            // makes the dependency visible: a view that reached into `skills`
            // or `documents` would be a cross-boundary join hidden in SQL,
            // which is exactly what the published-view shape exists to prevent.

            // The funnel. Stages with no applications have no row to group and
            // so are absent here; StatusFunnel zero-fills from the enum, which is
            // O(stages) rather than O(rows).
            migrationBuilder.Sql("""
                CREATE VIEW applications.v_application_status_counts AS
                SELECT a."Status"      AS "Status",
                       COUNT(*)::int   AS "Count"
                FROM applications.job_applications a
                GROUP BY a."Status";
                """);

            // Grouped from the application side, not by walking companies: one
            // GROUP BY with two joins, where starting from `companies` would be a
            // correlated subquery per company row. A company with postings but no
            // applications therefore does not appear — unreachable today, since
            // companies are only ever created by CreateApplication's
            // find-or-create, and a LEFT JOIN here is the fix if that changes.
            migrationBuilder.Sql("""
                CREATE VIEW applications.v_company_application_counts AS
                SELECT c."Name"        AS "CompanyName",
                       COUNT(*)::int   AS "ApplicationCount"
                FROM applications.job_applications a
                JOIN applications.job_postings p ON p."Id" = a."PostingId"
                JOIN applications.companies    c ON c."Id" = p."CompanyId"
                GROUP BY c."Name";
                """);

            // Stops at SkillId ON PURPOSE, and since 13.3b it could not do
            // otherwise: `skills` is another module's schema with its own
            // migration history. Analytics resolves the ids through
            // ISkillCatalog — see SkillDemand.cs for what that costs, which is
            // one bounded second query and a tiebreak that is now within-page.
            //
            // The composite PK on posting_skills makes this a count of POSTINGS:
            // one posting contributes one row however many times you applied.
            migrationBuilder.Sql("""
                CREATE VIEW applications.v_posting_skill_demand AS
                SELECT ps."SkillId"    AS "SkillId",
                       COUNT(*)::int   AS "PostingCount"
                FROM applications.posting_skills ps
                GROUP BY ps."SkillId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Views first: they depend on the tables below.
            migrationBuilder.Sql("DROP VIEW IF EXISTS applications.v_posting_skill_demand;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS applications.v_company_application_counts;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS applications.v_application_status_counts;");

            migrationBuilder.DropTable(
                name: "job_applications",
                schema: "applications");

            migrationBuilder.DropTable(
                name: "job_requirements",
                schema: "applications");

            migrationBuilder.DropTable(
                name: "posting_skills",
                schema: "applications");

            migrationBuilder.DropTable(
                name: "job_postings",
                schema: "applications");

            migrationBuilder.DropTable(
                name: "companies",
                schema: "applications");
        }
    }
}
