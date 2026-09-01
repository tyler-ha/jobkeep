using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Website = table.Column<string>(type: "text", nullable: true),
                    Industry = table.Column<string>(type: "text", nullable: true),
                    HqLocation = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "job_postings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Location = table.Column<string>(type: "text", nullable: true),
                    EmploymentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SalaryMin = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    SalaryMax = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    SalaryCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SalaryPeriod = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    PostedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SourceUrl = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_postings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_postings_companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_analyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seniority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    ModelUsed = table.Column<string>(type: "text", nullable: true),
                    AnalyzedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_analyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_analyses_job_postings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "job_postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "job_applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DateApplied = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    ResumeText = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_applications_job_postings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "job_postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_requirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsMustHave = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_requirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_requirements_job_postings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "job_postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "posting_skills",
                columns: table => new
                {
                    PostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_posting_skills", x => new { x.PostingId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_posting_skills_job_postings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "job_postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_posting_skills_skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ats_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchedKeywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    MissingMustHaveKeywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    FormattingRiskNotes = table.Column<List<string>>(type: "text[]", nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ats_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ats_results_job_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "job_applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_analyses_PostingId",
                table: "ai_analyses",
                column: "PostingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ats_results_ApplicationId",
                table: "ats_results",
                column: "ApplicationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_companies_Name",
                table: "companies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_PostingId",
                table: "job_applications",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_job_postings_CompanyId",
                table: "job_postings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_job_requirements_PostingId",
                table: "job_requirements",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_posting_skills_SkillId",
                table: "posting_skills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_skills_Name",
                table: "skills",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_analyses");

            migrationBuilder.DropTable(
                name: "ats_results");

            migrationBuilder.DropTable(
                name: "job_requirements");

            migrationBuilder.DropTable(
                name: "posting_skills");

            migrationBuilder.DropTable(
                name: "job_applications");

            migrationBuilder.DropTable(
                name: "skills");

            migrationBuilder.DropTable(
                name: "job_postings");

            migrationBuilder.DropTable(
                name: "companies");
        }
    }
}
