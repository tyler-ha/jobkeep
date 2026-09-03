using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Applications.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `applications` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class PostingSkillConfiguration : IEntityTypeConfiguration<PostingSkill>
{
    public void Configure(EntityTypeBuilder<PostingSkill> e)
    {
        e.ToTable("posting_skills", "applications");
        // Composite key: a skill appears at most once per posting.
        e.HasKey(ps => new { ps.PostingId, ps.SkillId });
        e.Property(ps => ps.Source).HasConversion<string>().HasMaxLength(20);

        e.HasOne(ps => ps.Posting)
            .WithMany(p => p.PostingSkills)
            .HasForeignKey(ps => ps.PostingId)
            .OnDelete(DeleteBehavior.Cascade);   // deleting a posting drops its skill links

        // 13.3b DROPPED THE FK to `skills`: it moved to its own schema, so the
        // navigation and the foreign key both went and ISkillCatalog is the only
        // way to reach a skill row.
        //
        // The INDEX has to be restated, and this is the second thing the FK was
        // quietly doing. EF indexes a foreign-key column automatically; the
        // composite primary key does NOT cover it, because SkillId is the second
        // column and Postgres can only seek on a leading prefix. Dropping the
        // relationship therefore drops the only index on SkillId — silently, with
        // nothing failing — and ListApplications' skill filter is exactly a
        // lookup by SkillId, since 13.2 resolves the name through ISkillCatalog
        // first and then filters on the id.
        e.HasIndex(ps => ps.SkillId);
    }
}
