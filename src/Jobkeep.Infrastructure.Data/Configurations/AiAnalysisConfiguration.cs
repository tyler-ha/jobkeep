using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Ai in 13.3b, where ToTable also gains its schema.
public class AiAnalysisConfiguration : IEntityTypeConfiguration<AiAnalysis>
{
    public void Configure(EntityTypeBuilder<AiAnalysis> e)
    {
        e.ToTable("ai_analyses");
        e.Property(a => a.Seniority).HasConversion<string>().HasMaxLength(20);
        // F13. ModelUsed holds an identifier like "llama3.2:3b" — the
        // clearest case in the audit that a bound was simply forgotten.
        e.Property(a => a.Summary).HasMaxLength(4000);
        e.Property(a => a.ModelUsed).HasMaxLength(100);
        // 1:1 — one analysis per posting.
        //
        // 13.3b DROPS THIS FK: `ai_analyses` and `job_postings` land in
        // different schemas, and the CASCADE is replaced by a notification on
        // posting delete.
        e.HasOne(a => a.Posting)
            .WithOne(p => p.AiAnalysis)
            .HasForeignKey<AiAnalysis>(a => a.PostingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
