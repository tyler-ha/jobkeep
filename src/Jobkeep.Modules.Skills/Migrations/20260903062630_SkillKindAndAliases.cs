using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Skills.Migrations
{
    /// <inheritdoc />
    public partial class SkillKindAndAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "skills",
                table: "skills",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.CreateTable(
                name: "skill_aliases",
                schema: "skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AliasNormalized = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, computedColumnSql: "lower(\"Alias\")", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_aliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_skill_aliases_skills_SkillId",
                        column: x => x.SkillId,
                        principalSchema: "skills",
                        principalTable: "skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_aliases_AliasNormalized",
                schema: "skills",
                table: "skill_aliases",
                column: "AliasNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_skill_aliases_SkillId",
                schema: "skills",
                table: "skill_aliases",
                column: "SkillId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skill_aliases",
                schema: "skills");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "skills",
                table: "skills");
        }
    }
}
