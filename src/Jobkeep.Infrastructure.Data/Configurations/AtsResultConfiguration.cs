using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Ats in 13.3b, where ToTable also gains its schema.
public class AtsResultConfiguration : IEntityTypeConfiguration<AtsResult>
{
    public void Configure(EntityTypeBuilder<AtsResult> e)
    {
        e.ToTable("ats_results");
        // List<string> -> Postgres text[] (Npgsql handles the array mapping).
        e.Property(r => r.MatchedKeywords).HasColumnType("text[]");
        e.Property(r => r.MissingMustHaveKeywords).HasColumnType("text[]");
        e.Property(r => r.MissingNiceToHaveKeywords).HasColumnType("text[]");
        e.Property(r => r.UnmetRequirements).HasColumnType("text[]");
        e.Property(r => r.FormattingRiskNotes).HasColumnType("text[]");
        e.Property(r => r.Warning).HasMaxLength(500);   // F13

        // 1:1 — one ATS result per application; cascade on application delete.
        //
        // 13.3b DROPS THIS FK: the CASCADE becomes a notification on
        // application delete.
        e.HasOne(r => r.Application)
            .WithOne(a => a.AtsResult)
            .HasForeignKey<AtsResult>(r => r.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 5 — which resume was judged. Restrict, matching
        // job_applications.ResumeId: deleting a resume must not silently
        // delete the evidence of what it was checked against. The result
        // stays 1:1 with the application (re-checking overwrites, latest
        // wins, the shape ai_analyses already uses), so this column is what
        // tells you which resume the surviving row read.
        //
        // 13.3b DROPS THIS FK too, and GetAtsResult.cs already says what
        // happens then: the label comes back null rather than the read failing.
        e.HasOne(r => r.Resume)
            .WithMany()
            .HasForeignKey(r => r.ResumeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
