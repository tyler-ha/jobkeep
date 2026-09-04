using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Documents.Migrations
{
    /// <summary>
    /// PHASE 8 — soft delete for resumes, and the index change that forces.
    ///
    /// Fully scaffolded, and EF got the ORDER right on its own, which is the part
    /// worth checking rather than assuming: drop the unique index, add the two
    /// columns, recreate the index with its predicate. Recreating it before
    /// "IsDeleted" existed would fail on a column the filter names.
    ///
    /// The unique index becomes PARTIAL because archiving must free the label.
    /// Without the filter, an archived "backend" would hold that name forever and
    /// the next import under it would be refused by a constraint naming a document
    /// the user can no longer see. ResumeConfiguration.cs and RestoreResume.cs
    /// carry the argument and the price — the price being that a restore can now
    /// be refused, because a live résumé may have taken the label meanwhile.
    ///
    /// THE DOWN CAN FAIL ON REAL DATA, and that is honest rather than broken.
    /// It drops "IsDeleted" — making every archived row live again, as the
    /// Applications down migration does for the same reason — and then rebuilds
    /// the index without a predicate. If an archived résumé and a live one share a
    /// label, the rebuild is refused. There is no correct automatic answer to
    /// that: silently renaming one is the behaviour RestoreResume.cs explicitly
    /// rejects, and dropping one destroys a document. Reversing this migration on
    /// a database with archived résumés is a manual step.
    /// </summary>
    public partial class SoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_resumes_LabelNormalized",
                schema: "documents",
                table: "resumes");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                schema: "documents",
                table: "resumes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "documents",
                table: "resumes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_resumes_LabelNormalized",
                schema: "documents",
                table: "resumes",
                column: "LabelNormalized",
                unique: true,
                filter: "NOT \"IsDeleted\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_resumes_LabelNormalized",
                schema: "documents",
                table: "resumes");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                schema: "documents",
                table: "resumes");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "documents",
                table: "resumes");

            migrationBuilder.CreateIndex(
                name: "IX_resumes_LabelNormalized",
                schema: "documents",
                table: "resumes",
                column: "LabelNormalized",
                unique: true);
        }
    }
}
