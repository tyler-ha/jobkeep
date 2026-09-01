using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Documents in 13.3b, where ToTable also gains its schema.
public class ResumeEducationConfiguration : IEntityTypeConfiguration<ResumeEducation>
{
    public void Configure(EntityTypeBuilder<ResumeEducation> e)
    {
        e.ToTable("resume_educations");
        e.Property(x => x.Institution).HasMaxLength(200);
        e.Property(x => x.Qualification).HasMaxLength(200);
        e.Property(x => x.YearText).HasMaxLength(50);

        e.HasOne(x => x.Resume)
            .WithMany(r => r.Educations)
            .HasForeignKey(x => x.ResumeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
