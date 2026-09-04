using Jobkeep.Contracts.Shared;
using Jobkeep.Modules.Skills.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Skills.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `skills` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> e)
    {
        e.ToTable("skills", "skills");
        e.Property(s => s.Name).HasMaxLength(100);
        e.Property(s => s.Category).HasMaxLength(50);

        // PHASE 14. Stored as a string like every other enum in this schema, so
        // the table still reads as English in psql. The database-side default
        // matters more here than it looks: Kind is non-nullable in C#, so
        // without it the migration would have to invent a value for existing
        // rows anyway — this way the same answer is written once, in the place
        // that is also correct for a writer that is not EF (Phase 7, F11).
        e.Property(s => s.Kind)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(SkillKind.Unknown);

        // Phase 7 — see companies above. This is the table where the defect
        // was costing something measurable: a duplicate row split one
        // skill's count in /stats/skill-demand, and the Phase 5 ATS check
        // matches skill ROWS, so a difference of case read as a gap.
        e.Property(s => s.NameNormalized)
            .HasMaxLength(100)
            .HasComputedColumnSql("lower(\"Name\")", stored: true);
        e.HasIndex(s => s.NameNormalized).IsUnique();
    }
}
