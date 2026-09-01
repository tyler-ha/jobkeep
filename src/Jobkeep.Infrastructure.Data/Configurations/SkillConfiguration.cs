using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Skills in 13.3b, where ToTable also gains its schema.
public class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> e)
    {
        e.ToTable("skills");
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
