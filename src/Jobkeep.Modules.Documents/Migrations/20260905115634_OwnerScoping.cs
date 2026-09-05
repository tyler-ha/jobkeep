using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Documents.Migrations
{
    /// <inheritdoc />
    public partial class OwnerScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_resumes_LabelNormalized",
                schema: "documents",
                table: "resumes");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                schema: "documents",
                table: "resumes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                schema: "documents",
                table: "document_imports",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_resumes_OwnerUserId_LabelNormalized",
                schema: "documents",
                table: "resumes",
                columns: new[] { "OwnerUserId", "LabelNormalized" },
                unique: true,
                filter: "NOT \"IsDeleted\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_resumes_OwnerUserId_LabelNormalized",
                schema: "documents",
                table: "resumes");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "documents",
                table: "resumes");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "documents",
                table: "document_imports");

            migrationBuilder.CreateIndex(
                name: "IX_resumes_LabelNormalized",
                schema: "documents",
                table: "resumes",
                column: "LabelNormalized",
                unique: true,
                filter: "NOT \"IsDeleted\"");
        }
    }
}
