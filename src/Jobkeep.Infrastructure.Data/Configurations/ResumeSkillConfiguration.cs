using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Documents in 13.3b, where ToTable also gains its schema.
public class ResumeSkillConfiguration : IEntityTypeConfiguration<ResumeSkill>
{
    public void Configure(EntityTypeBuilder<ResumeSkill> e)
    {
        e.ToTable("resume_skills");
        // Composite key: a skill appears at most once per resume. The exact
        // mirror of posting_skills, so a retried write is a no-op rather than
        // a duplicate row.
        e.HasKey(rs => new { rs.ResumeId, rs.SkillId });
        e.Property(rs => rs.Source).HasConversion<string>().HasMaxLength(20);

        e.HasOne(rs => rs.Resume)
            .WithMany(r => r.ResumeSkills)
            .HasForeignKey(rs => rs.ResumeId)
            .OnDelete(DeleteBehavior.Cascade);    // deleting a resume drops its links

        // 13.3b DROPS THIS ONE, the mirror of posting_skills.SkillId. Its
        // replacement is ISkillCatalog: a link is only ever created through
        // FindOrCreateAsync, which is what guarantees the row it points at.
        e.HasOne(rs => rs.Skill)
            .WithMany(s => s.ResumeSkills)
            .HasForeignKey(rs => rs.SkillId)
            .OnDelete(DeleteBehavior.Restrict);   // but never the shared skill row
    }
}
