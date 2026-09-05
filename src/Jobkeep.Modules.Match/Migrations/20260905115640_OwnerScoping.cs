using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Match.Migrations
{
    /// <inheritdoc />
    public partial class OwnerScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                schema: "ats",
                table: "match_results",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "ats",
                table: "match_results");
        }
    }
}
