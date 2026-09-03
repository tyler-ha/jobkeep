using Jobkeep.Modules.Documents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Documents.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `documents` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class ResumeExperienceConfiguration : IEntityTypeConfiguration<ResumeExperience>
{
    public void Configure(EntityTypeBuilder<ResumeExperience> e)
    {
        e.ToTable("resume_experiences", "documents");
        e.Property(x => x.Employer).HasMaxLength(200);
        e.Property(x => x.Title).HasMaxLength(200);
        // Free text, not dates — see the comment on the model for why
        // "Mar 2021" is stored as "Mar 2021".
        e.Property(x => x.StartText).HasMaxLength(50);
        e.Property(x => x.EndText).HasMaxLength(50);
        // List<string> -> Postgres text[], the same mapping AtsResult uses.
        e.Property(x => x.Highlights).HasColumnType("text[]");

        e.HasOne(x => x.Resume)
            .WithMany(r => r.Experiences)
            .HasForeignKey(x => x.ResumeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
