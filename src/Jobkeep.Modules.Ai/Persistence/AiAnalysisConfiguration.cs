using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Ai.Persistence;

// Ai owns exactly one table, and since 13.3b it owns the `ai` schema it lives in.
public class AiAnalysisConfiguration : IEntityTypeConfiguration<AiAnalysis>
{
    public void Configure(EntityTypeBuilder<AiAnalysis> e)
    {
        e.ToTable("ai_analyses", "ai");
        e.Property(a => a.Seniority).HasConversion<string>().HasMaxLength(20);
        // F13. ModelUsed holds an identifier like "llama3.2:3b" — the
        // clearest case in the audit that a bound was simply forgotten.
        e.Property(a => a.Summary).HasMaxLength(4000);
        e.Property(a => a.ModelUsed).HasMaxLength(100);

        // 1:1 — one analysis per posting.
        //
        // 13.3b DROPPED THE FK to `job_postings`: it is Applications' table in
        // Applications' schema, and the CASCADE it carried is replaced by a
        // notification on posting delete in 13.3c.
        //
        // The unique index is what the FK was also silently doing.
        // HasForeignKey<AiAnalysis> declares a one-to-ONE, and EF implements the
        // "one" half with a unique index on the dependent's key column. Delete
        // the relationship and that index goes with it — quietly, since nothing
        // in the build mentions it — and the table would then happily hold two
        // analyses for one posting, which AnalyzePosting's update-or-insert
        // assumes cannot happen. Stated explicitly now that nothing else states
        // it.
        e.HasIndex(a => a.PostingId).IsUnique();
    }
}
