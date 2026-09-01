using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Documents in 13.3b, where ToTable also gains its schema.
public class ResumeExperienceConfiguration : IEntityTypeConfiguration<ResumeExperience>
{
    public void Configure(EntityTypeBuilder<ResumeExperience> e)
    {
        e.ToTable("resume_experiences");
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
