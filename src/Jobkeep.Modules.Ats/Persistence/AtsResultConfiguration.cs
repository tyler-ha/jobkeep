using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Ats.Persistence;

// Ats owns one table and, since 13.3b, the `ats` schema it lives in. It is also
// the module that lost the most to the split: it used to read five tables it did
// not own, all of which became contract calls in 13.2e, and both of this table's
// foreign keys pointed out of the module.
public class AtsResultConfiguration : IEntityTypeConfiguration<AtsResult>
{
    public void Configure(EntityTypeBuilder<AtsResult> e)
    {
        e.ToTable("ats_results", "ats");
        // List<string> -> Postgres text[] (Npgsql handles the array mapping).
        e.Property(r => r.MatchedKeywords).HasColumnType("text[]");
        e.Property(r => r.MissingMustHaveKeywords).HasColumnType("text[]");
        e.Property(r => r.MissingNiceToHaveKeywords).HasColumnType("text[]");
        e.Property(r => r.UnmetRequirements).HasColumnType("text[]");
        e.Property(r => r.FormattingRiskNotes).HasColumnType("text[]");
        e.Property(r => r.Warning).HasMaxLength(500);   // F13

        // 1:1 — one ATS result per application.
        //
        // 13.3b DROPPED BOTH FKs. `job_applications` and `resumes` belong to two
        // other modules and two other schemas, so the CASCADE on application
        // delete and the RESTRICT on resume delete are both gone; 13.3c replaces
        // the first with a delete notification and leaves the second as the
        // null-label case GetAtsResult.cs already describes.
        //
        // This index is the piece that would otherwise vanish unannounced: the
        // "one result per application" rule was a side effect of
        // HasForeignKey<AtsResult> on a one-to-one, not something anyone wrote
        // down. CheckAts overwrites the existing row rather than inserting a
        // second one, and that is only safe if a second one cannot exist.
        e.HasIndex(r => r.ApplicationId).IsUnique();
    }
}
