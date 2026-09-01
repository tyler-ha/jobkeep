using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Migrations
{
    /// <inheritdoc />
    public partial class DataIntegrityAndNaturalKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ================================================================
            // Phase 7, step 0 — merge the duplicates BEFORE the unique indexes
            // ================================================================
            // Everything below this block is scaffolded. This block is not, and
            // it has to run first: the new unique indexes are on lower(name),
            // and any database that already holds "C#" and "c#" would fail to
            // create them. That is the whole reason this phase was scheduled
            // ahead of the feature work — the merge is not a fixed cost. Every
            // day of real use adds rows it has to reconcile.
            //
            // The merge is deliberately conservative and does three things per
            // table: pick one surviving row, repoint everything that referenced
            // a loser at the survivor, then delete the losers.
            //
            // WHICH ROW SURVIVES: the oldest by id ordering within the group —
            // stable, and independent of the order Postgres happens to return.
            // The surviving row keeps its own spelling. There is no attempt to
            // guess whether "C#" or "c#" is the "right" one; the user can rename
            // afterwards, and a wrong guess here would be silent.
            //
            // WHY RAW SQL: this is a set-based data fix over rows EF has no
            // entities for at migration time. Doing it in C# would mean loading
            // every skill and company into memory to compare strings, which is
            // the "aggregate in SQL, not in memory" rule inverted.

            // ---- skills -----------------------------------------------------
            // Link tables first, then the skill rows. `ON CONFLICT DO NOTHING`
            // matters: a posting that somehow linked BOTH "C#" and "c#" would
            // otherwise violate the composite primary key when both collapse to
            // the same SkillId.
            migrationBuilder.Sql(@"
                CREATE TEMP TABLE skill_merge AS
                SELECT s.""Id"" AS loser_id, w.keep_id
                FROM   ""skills"" s
                JOIN  (SELECT lower(""Name"") AS k, MIN(""Id""::text) AS keep_id
                       FROM   ""skills"" GROUP BY lower(""Name"")
                       HAVING COUNT(*) > 1) w
                  ON   lower(s.""Name"") = w.k
                WHERE  s.""Id""::text <> w.keep_id;

                INSERT INTO ""posting_skills"" (""PostingId"", ""SkillId"", ""Source"")
                SELECT ps.""PostingId"", m.keep_id::uuid, ps.""Source""
                FROM   ""posting_skills"" ps JOIN skill_merge m ON ps.""SkillId"" = m.loser_id
                ON CONFLICT DO NOTHING;

                INSERT INTO ""resume_skills"" (""ResumeId"", ""SkillId"", ""Source"")
                SELECT rs.""ResumeId"", m.keep_id::uuid, rs.""Source""
                FROM   ""resume_skills"" rs JOIN skill_merge m ON rs.""SkillId"" = m.loser_id
                ON CONFLICT DO NOTHING;

                DELETE FROM ""posting_skills"" WHERE ""SkillId"" IN (SELECT loser_id FROM skill_merge);
                DELETE FROM ""resume_skills""  WHERE ""SkillId"" IN (SELECT loser_id FROM skill_merge);
                DELETE FROM ""skills""         WHERE ""Id""      IN (SELECT loser_id FROM skill_merge);

                DROP TABLE skill_merge;
            ");

            // ---- companies --------------------------------------------------
            // Only job_postings reference a company, and the FK is Restrict, so
            // the repoint has to happen before the delete or Postgres refuses.
            migrationBuilder.Sql(@"
                CREATE TEMP TABLE company_merge AS
                SELECT c.""Id"" AS loser_id, w.keep_id
                FROM   ""companies"" c
                JOIN  (SELECT lower(""Name"") AS k, MIN(""Id""::text) AS keep_id
                       FROM   ""companies"" GROUP BY lower(""Name"")
                       HAVING COUNT(*) > 1) w
                  ON   lower(c.""Name"") = w.k
                WHERE  c.""Id""::text <> w.keep_id;

                UPDATE ""job_postings"" p
                SET    ""CompanyId"" = m.keep_id::uuid
                FROM   company_merge m
                WHERE  p.""CompanyId"" = m.loser_id;

                DELETE FROM ""companies"" WHERE ""Id"" IN (SELECT loser_id FROM company_merge);
                DROP TABLE company_merge;
            ");

            // ---- resumes ----------------------------------------------------
            // Resumes are NOT merged. A résumé is a document with its own
            // skills, experiences and educations, and two files that happen to
            // be labelled "Backend" and "backend" are two different documents —
            // collapsing them would destroy content, which no migration should
            // do silently. Instead the losing labels are made unique by
            // suffixing, so the index can be created and the user renames them
            // deliberately. Loud and reversible beats clever and lossy.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT ""Id"",
                           ROW_NUMBER() OVER (PARTITION BY lower(""Label"") ORDER BY ""Id""::text) AS rn
                    FROM   ""resumes""
                )
                UPDATE ""resumes"" r
                SET    ""Label"" = left(r.""Label"", 88) || ' (' || ranked.rn || ')'
                FROM   ranked
                WHERE  r.""Id"" = ranked.""Id"" AND ranked.rn > 1;
            ");

            migrationBuilder.DropIndex(
                name: "IX_skills_Name",
                table: "skills");

            migrationBuilder.DropIndex(
                name: "IX_resumes_Label",
                table: "resumes");

            migrationBuilder.DropIndex(
                name: "IX_companies_Name",
                table: "companies");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "skills",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "skills",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "skills",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AlterColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "resumes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "resumes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "resumes",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "SkillId",
                table: "resume_skills",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "ResumeId",
                table: "resume_skills",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "resume_experiences",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "resume_educations",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "SkillId",
                table: "posting_skills",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "PostingId",
                table: "posting_skills",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "Text",
                table: "job_requirements",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "job_requirements",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "job_requirements",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "job_requirements",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AlterColumn<string>(
                name: "SourceUrl",
                table: "job_postings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Location",
                table: "job_postings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "job_postings",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "job_postings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "job_postings",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "job_postings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "job_postings",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AlterColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "job_applications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<string>(
                name: "Notes",
                table: "job_applications",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "job_applications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "job_applications",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "job_applications",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AlterColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "document_imports",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "document_imports",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "document_imports",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "Website",
                table: "companies",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Industry",
                table: "companies",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "HqLocation",
                table: "companies",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "companies",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "companies",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "companies",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "companies",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AlterColumn<string>(
                name: "Warning",
                table: "ats_results",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "ats_results",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "Summary",
                table: "ai_analyses",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModelUsed",
                table: "ai_analyses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "ai_analyses",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()",
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "NameNormalized",
                table: "skills",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                computedColumnSql: "lower(\"Name\")",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelNormalized",
                table: "resumes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                computedColumnSql: "lower(\"Label\")",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "NameNormalized",
                table: "companies",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                computedColumnSql: "lower(\"Name\")",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_skills_NameNormalized",
                table: "skills",
                column: "NameNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resumes_LabelNormalized",
                table: "resumes",
                column: "LabelNormalized",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_postings_currency_iso4217",
                table: "job_postings",
                sql: "\"SalaryCurrency\" ~ '^[A-Z]{3}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_postings_salary_range",
                table: "job_postings",
                sql: "\"SalaryMin\" IS NULL OR \"SalaryMax\" IS NULL OR \"SalaryMin\" <= \"SalaryMax\"");

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_DateApplied",
                table: "job_applications",
                column: "DateApplied",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_Status",
                table: "job_applications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_companies_NameNormalized",
                table: "companies",
                column: "NameNormalized",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_skills_NameNormalized",
                table: "skills");

            migrationBuilder.DropIndex(
                name: "IX_resumes_LabelNormalized",
                table: "resumes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_postings_currency_iso4217",
                table: "job_postings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_postings_salary_range",
                table: "job_postings");

            migrationBuilder.DropIndex(
                name: "IX_job_applications_DateApplied",
                table: "job_applications");

            migrationBuilder.DropIndex(
                name: "IX_job_applications_Status",
                table: "job_applications");

            migrationBuilder.DropIndex(
                name: "IX_companies_NameNormalized",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "NameNormalized",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "LabelNormalized",
                table: "resumes");

            migrationBuilder.DropColumn(
                name: "NameNormalized",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "job_requirements");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "job_requirements");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "job_postings");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "job_postings");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "companies");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "skills",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "resumes",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "resumes",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "resumes",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<Guid>(
                name: "SkillId",
                table: "resume_skills",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<Guid>(
                name: "ResumeId",
                table: "resume_skills",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "resume_experiences",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "resume_educations",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<Guid>(
                name: "SkillId",
                table: "posting_skills",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<Guid>(
                name: "PostingId",
                table: "posting_skills",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<string>(
                name: "Text",
                table: "job_requirements",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "job_requirements",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<string>(
                name: "SourceUrl",
                table: "job_postings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Location",
                table: "job_postings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "job_postings",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20000)",
                oldMaxLength: 20000,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "job_postings",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "job_postings",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "job_applications",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AlterColumn<string>(
                name: "Notes",
                table: "job_applications",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10000)",
                oldMaxLength: 10000,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "job_applications",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "job_applications",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "document_imports",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "document_imports",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "document_imports",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<string>(
                name: "Website",
                table: "companies",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Industry",
                table: "companies",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "HqLocation",
                table: "companies",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "companies",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<string>(
                name: "Warning",
                table: "ats_results",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "ats_results",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AlterColumn<string>(
                name: "Summary",
                table: "ai_analyses",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModelUsed",
                table: "ai_analyses",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "ai_analyses",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateIndex(
                name: "IX_skills_Name",
                table: "skills",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resumes_Label",
                table: "resumes",
                column: "Label",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_companies_Name",
                table: "companies",
                column: "Name",
                unique: true);
        }
    }
}
