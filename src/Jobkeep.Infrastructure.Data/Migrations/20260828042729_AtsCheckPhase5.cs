using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Migrations
{
    /// <inheritdoc />
    public partial class AtsCheckPhase5 : Migration
    {
        /// <inheritdoc />
        // The two text[] columns are added NOT NULL with no default, which would
        // fail on a table that already had rows. It does not here: ats_results has
        // been in the schema since InitialCreate as Phase 5 scaffolding and nothing
        // has ever inserted into it, so every environment applies this against an
        // empty table. Said out loud rather than left as luck.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceFormat",
                table: "resumes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "MissingNiceToHaveKeywords",
                table: "ats_results",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ResumeId",
                table: "ats_results",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "UnmetRequirements",
                table: "ats_results",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "Warning",
                table: "ats_results",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ats_results_ResumeId",
                table: "ats_results",
                column: "ResumeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ats_results_resumes_ResumeId",
                table: "ats_results",
                column: "ResumeId",
                principalTable: "resumes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ats_results_resumes_ResumeId",
                table: "ats_results");

            migrationBuilder.DropIndex(
                name: "IX_ats_results_ResumeId",
                table: "ats_results");

            migrationBuilder.DropColumn(
                name: "SourceFormat",
                table: "resumes");

            migrationBuilder.DropColumn(
                name: "MissingNiceToHaveKeywords",
                table: "ats_results");

            migrationBuilder.DropColumn(
                name: "ResumeId",
                table: "ats_results");

            migrationBuilder.DropColumn(
                name: "UnmetRequirements",
                table: "ats_results");

            migrationBuilder.DropColumn(
                name: "Warning",
                table: "ats_results");
        }
    }
}
