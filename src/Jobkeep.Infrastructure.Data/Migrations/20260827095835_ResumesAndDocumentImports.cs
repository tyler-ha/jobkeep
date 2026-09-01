using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Migrations
{
    /// <inheritdoc />
    public partial class ResumesAndDocumentImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------
            // This DROP LOSES DATA, deliberately. Read before re-running.
            // ---------------------------------------------------------------
            // job_applications.ResumeText held a whole resume per application.
            // Phase 4.5 replaced it with ResumeId pointing at the new `resumes`
            // table, because a resume is a property of the person, not of one
            // application — see Models/Resume.cs for the full argument.
            //
            // The text is dropped rather than migrated into a resume row. That is
            // safe HERE and would not be safe in general: this is a single-user
            // local database, the column was Phase 5 scaffolding that no endpoint
            // ever meaningfully filled, and the app has never been deployed. If
            // that stops being true, the honest version of this migration reads
            // each distinct non-null ResumeText into a resume row first and
            // back-fills ResumeId — perhaps twenty lines of SQL, and worth every
            // one of them at that point.
            migrationBuilder.DropColumn(
                name: "ResumeText",
                table: "job_applications");

            migrationBuilder.AddColumn<Guid>(
                name: "ResumeId",
                table: "job_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "document_imports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CommittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CommittedEntityId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_imports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "resumes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Headline = table.Column<string>(type: "text", nullable: true),
                    SourceText = table.Column<string>(type: "text", nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resumes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "resume_educations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
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
                        principalTable: "resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resume_experiences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
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
                        principalTable: "resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resume_skills",
                columns: table => new
                {
                    ResumeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resume_skills", x => new { x.ResumeId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_resume_skills_resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalTable: "resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_resume_skills_skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_ResumeId",
                table: "job_applications",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_document_imports_Status",
                table: "document_imports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_resume_educations_ResumeId",
                table: "resume_educations",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_resume_experiences_ResumeId",
                table: "resume_experiences",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_resume_skills_SkillId",
                table: "resume_skills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_resumes_Label",
                table: "resumes",
                column: "Label",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_job_applications_resumes_ResumeId",
                table: "job_applications",
                column: "ResumeId",
                principalTable: "resumes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_job_applications_resumes_ResumeId",
                table: "job_applications");

            migrationBuilder.DropTable(
                name: "document_imports");

            migrationBuilder.DropTable(
                name: "resume_educations");

            migrationBuilder.DropTable(
                name: "resume_experiences");

            migrationBuilder.DropTable(
                name: "resume_skills");

            migrationBuilder.DropTable(
                name: "resumes");

            migrationBuilder.DropIndex(
                name: "IX_job_applications_ResumeId",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "ResumeId",
                table: "job_applications");

            migrationBuilder.AddColumn<string>(
                name: "ResumeText",
                table: "job_applications",
                type: "text",
                nullable: true);
        }
    }
}
