using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Match.Migrations
{
    /// <inheritdoc />
    public partial class RenameAtsResultsToMatchResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ats_results",
                schema: "ats",
                table: "ats_results");

            migrationBuilder.RenameTable(
                name: "ats_results",
                schema: "ats",
                newName: "match_results",
                newSchema: "ats");

            migrationBuilder.RenameIndex(
                name: "IX_ats_results_ApplicationId",
                schema: "ats",
                table: "match_results",
                newName: "IX_match_results_ApplicationId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_match_results",
                schema: "ats",
                table: "match_results",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_match_results",
                schema: "ats",
                table: "match_results");

            migrationBuilder.RenameTable(
                name: "match_results",
                schema: "ats",
                newName: "ats_results",
                newSchema: "ats");

            migrationBuilder.RenameIndex(
                name: "IX_match_results_ApplicationId",
                schema: "ats",
                table: "ats_results",
                newName: "IX_ats_results_ApplicationId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ats_results",
                schema: "ats",
                table: "ats_results",
                column: "Id");
        }
    }
}
