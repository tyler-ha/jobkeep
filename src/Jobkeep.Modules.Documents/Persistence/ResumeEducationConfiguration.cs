using Jobkeep.Modules.Documents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Documents.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `documents` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class ResumeEducationConfiguration : IEntityTypeConfiguration<ResumeEducation>
{
    public void Configure(EntityTypeBuilder<ResumeEducation> e)
    {
        e.ToTable("resume_educations", "documents");
        e.Property(x => x.Institution).HasMaxLength(200);
        e.Property(x => x.Qualification).HasMaxLength(200);
        e.Property(x => x.YearText).HasMaxLength(50);

        e.HasOne(x => x.Resume)
            .WithMany(r => r.Educations)
            .HasForeignKey(x => x.ResumeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
