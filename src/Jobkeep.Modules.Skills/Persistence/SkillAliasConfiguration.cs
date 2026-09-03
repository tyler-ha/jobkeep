using Jobkeep.Modules.Skills.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Skills.Persistence;

// PHASE 14. Mirrors SkillConfiguration deliberately — same generated column,
// same stored:true, same unique index on the generated column and not on the
// raw one. Two tables normalising a human-typed name the same way should not
// normalise it two different ways.
public class SkillAliasConfiguration : IEntityTypeConfiguration<SkillAlias>
{
    public void Configure(EntityTypeBuilder<SkillAlias> e)
    {
        e.ToTable("skill_aliases", "skills");
        e.Property(a => a.Alias).HasMaxLength(100);

        e.Property(a => a.AliasNormalized)
            .HasMaxLength(100)
            .HasComputedColumnSql("lower(\"Alias\")", stored: true);

        // One spelling resolves to at most one skill. Without this the catalog's
        // alias lookup would have to pick between rows, and "whichever the query
        // planner returned first" is not a rule anyone can reason about.
        e.HasIndex(a => a.AliasNormalized).IsUnique();

        // An INTRA-SCHEMA foreign key, and the first new one since 13.3b dropped
        // six. It is allowed for the reason those six were not: both tables are
        // in `skills` and both belong to this module, so it survives a service
        // extraction — the whole schema leaves together. CASCADE because an alias
        // for a deleted skill is not a fact about anything.
        e.HasOne<Skill>()
            .WithMany()
            .HasForeignKey(a => a.SkillId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
