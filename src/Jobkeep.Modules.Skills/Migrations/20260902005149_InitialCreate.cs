using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jobkeep.Modules.Skills.Migrations
{
    /// <summary>
    /// Phase 13.3b. The shared taxonomy, alone in the `skills` schema.
    ///
    /// <para>
    /// One table and one migration history, which is the smallest this repo has and the
    /// point of the module: posting_skills (Applications) and resume_skills (Documents)
    /// both point at these rows, so the table is co-owned in practice and belongs to
    /// neither. Nothing in this migration references another schema, and nothing in
    /// another schema references this one — the two foreign keys that used to, on
    /// SkillId from both sides, are what 13.3b dropped.
    /// </para>
    ///
    /// <para>
    /// The generated NameNormalized column and its unique index come across unchanged
    /// from Phase 7. They are the natural key, and ISkillCatalog is the only code
    /// allowed to compute the C# half of it.
    /// </para>
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "skills");

            migrationBuilder.CreateTable(
                name: "skills",
                schema: "skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    NameNormalized = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, computedColumnSql: "lower(\"Name\")", stored: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now() at time zone 'utc'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skills_NameNormalized",
                schema: "skills",
                table: "skills",
                column: "NameNormalized",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skills",
                schema: "skills");
        }
    }
}
