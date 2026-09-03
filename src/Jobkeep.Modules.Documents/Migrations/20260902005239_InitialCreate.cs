using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Documents.Migrations
{
    /// <summary>
    /// Phase 13.3b. The import review cycle and the four tables a confirmed draft
    /// becomes, in the `documents` schema.
    ///
    /// <para>
    /// Five tables, four foreign keys, and all four stay inside this schema — resume
    /// skills, experiences and educations belong to their résumé. The two that left
    /// are resume_skills.SkillId, which pointed at the shared taxonomy, and the inbound
    /// job_applications.ResumeId, which was Applications' to drop.
    /// </para>
    ///
    /// <para>
    /// document_imports.CommittedEntityId is still a bare Guid with no foreign key, and
    /// still for its original reason rather than this phase's: it points into two
    /// different tables depending on Kind. It is a receipt, not a relationship.
    /// </para>
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.CreateTable(
                name: "document_imports",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ByteCount = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExtractedText = table.Column<string>(type: "text", nullable: false),
                    DraftJson = table.Column<string>(type: "jsonb", nullable: false),
                    ModelUsed = table.Column<string>(type: "text", nullable: true),
                    Warning = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    CommittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CommittedEntityId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_imports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "resumes",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Headline = table.Column<string>(type: "text", nullable: true),
                    SourceText = table.Column<string>(type: "text", nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourceFormat = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LabelNormalized = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, computedColumnSql: "lower(\"Label\")", stored: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resumes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "resume_educations",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ResumeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Institution = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Qualification = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    YearText = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resume_educations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_resume_educations_resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "documents",
                        principalTable: "resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resume_experiences",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ResumeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Employer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StartText = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EndText = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Highlights = table.Column<List<string>>(type: "text[]", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resume_experiences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_resume_experiences_resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "documents",
                        principalTable: "resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resume_skills",
                schema: "documents",
                columns: table => new
                {
                    ResumeId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resume_skills", x => new { x.ResumeId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_resume_skills_resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "documents",
                        principalTable: "resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_imports_Status",
                schema: "documents",
                table: "document_imports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_resume_educations_ResumeId",
                schema: "documents",
                table: "resume_educations",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_resume_experiences_ResumeId",
                schema: "documents",
                table: "resume_experiences",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_resume_skills_SkillId",
                schema: "documents",
                table: "resume_skills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_resumes_LabelNormalized",
                schema: "documents",
                table: "resumes",
                column: "LabelNormalized",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_imports",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "resume_educations",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "resume_experiences",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "resume_skills",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "resumes",
                schema: "documents");
        }
    }
}
