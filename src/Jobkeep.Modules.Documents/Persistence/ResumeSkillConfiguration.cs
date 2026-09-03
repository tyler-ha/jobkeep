using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Documents.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `documents` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class ResumeSkillConfiguration : IEntityTypeConfiguration<ResumeSkill>
{
    public void Configure(EntityTypeBuilder<ResumeSkill> e)
    {
        e.ToTable("resume_skills", "documents");
        // Composite key: a skill appears at most once per resume. The exact
        // mirror of posting_skills, so a retried write is a no-op rather than
        // a duplicate row.
        e.HasKey(rs => new { rs.ResumeId, rs.SkillId });
        e.Property(rs => rs.Source).HasConversion<string>().HasMaxLength(20);

        e.HasOne(rs => rs.Resume)
            .WithMany(r => r.ResumeSkills)
            .HasForeignKey(rs => rs.ResumeId)
            .OnDelete(DeleteBehavior.Cascade);    // deleting a resume drops its links

        // 13.3b DROPPED THE FK to `skills`, the mirror of posting_skills.SkillId.
        // Its replacement is ISkillCatalog: a link is only ever created through
        // FindOrCreateAsync, which is what guarantees the row it points at.
        //
        // And, as on the posting side, the index it carried has to be restated —
        // the composite key leads with ResumeId, so it cannot answer a lookup by
        // SkillId, and dropping a foreign key drops its index without saying so.
        e.HasIndex(rs => rs.SkillId);
    }
}
