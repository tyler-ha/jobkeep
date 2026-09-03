using Jobkeep.Modules.Applications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Applications.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `applications` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class JobRequirementConfiguration : IEntityTypeConfiguration<JobRequirement>
{
    public void Configure(EntityTypeBuilder<JobRequirement> e)
    {
        e.ToTable("job_requirements", "applications");
        e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
        e.Property(r => r.Text).HasMaxLength(1000);   // F13
        e.HasOne(r => r.Posting)
            .WithMany(p => p.Requirements)
            .HasForeignKey(r => r.PostingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
