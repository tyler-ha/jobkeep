using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Applications in 13.3b, where ToTable also gains its schema.
public class PostingSkillConfiguration : IEntityTypeConfiguration<PostingSkill>
{
    public void Configure(EntityTypeBuilder<PostingSkill> e)
    {
        e.ToTable("posting_skills");
        // Composite key: a skill appears at most once per posting.
        e.HasKey(ps => new { ps.PostingId, ps.SkillId });
        e.Property(ps => ps.Source).HasConversion<string>().HasMaxLength(20);

        e.HasOne(ps => ps.Posting)
            .WithMany(p => p.PostingSkills)
            .HasForeignKey(ps => ps.PostingId)
            .OnDelete(DeleteBehavior.Cascade);   // deleting a posting drops its skill links

        // 13.3b DROPS THIS ONE: `skills` moves to its own schema, so the
        // navigation and the FK both go and ISkillCatalog becomes the only way
        // to reach a skill row. See the phase doc's five-FK table.
        e.HasOne(ps => ps.Skill)
            .WithMany(s => s.PostingSkills)
            .HasForeignKey(ps => ps.SkillId)
            .OnDelete(DeleteBehavior.Restrict);  // but never delete the shared skill row
    }
}
