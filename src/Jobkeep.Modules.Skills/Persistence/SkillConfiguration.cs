using Jobkeep.Models;
using Jobkeep.Persistence;
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
