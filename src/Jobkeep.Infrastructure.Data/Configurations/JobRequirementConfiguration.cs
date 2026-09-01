using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Applications in 13.3b, where ToTable also gains its schema.
public class JobRequirementConfiguration : IEntityTypeConfiguration<JobRequirement>
{
    public void Configure(EntityTypeBuilder<JobRequirement> e)
    {
        e.ToTable("job_requirements");
        e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
        e.Property(r => r.Text).HasMaxLength(1000);   // F13
        e.HasOne(r => r.Posting)
            .WithMany(p => p.Requirements)
            .HasForeignKey(r => r.PostingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
