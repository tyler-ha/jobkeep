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
        // delete and the RESTRICT on resume delete both went.
        //
        // 13.3c REPLACED BOTH, and the two replacements are different in kind
        // because the two keys were:
        //
        //   * the CASCADE became a notification. Applications publishes
        //     ApplicationDeleted after it commits; Application/OnApplicationDeleted
        //     deletes this row. An announcement after the fact, which is what a
        //     cascade is.
        //   * the RESTRICT became a question. Documents asks
        //     IAtsContract.CountResultsForResumeAsync before deleting a résumé and
        //     refuses while the answer is not zero. Asked before the fact, which
        //     is what a restrict is.
        //
        // This comment used to say the second would be left as the null-label
        // case GetAtsResult.cs describes. That handling STAYS — it is what makes
        // the check's time-of-check-to-time-of-use race survivable, and the state
        // is still reachable by editing the database directly — but it is no
        // longer the whole answer. DeleteResume.cs argues the gap in full.
        //
        // This index is the piece that would otherwise vanish unannounced: the
        // "one result per application" rule was a side effect of
        // HasForeignKey<AtsResult> on a one-to-one, not something anyone wrote
        // down. CheckAts overwrites the existing row rather than inserting a
        // second one, and that is only safe if a second one cannot exist.
        e.HasIndex(r => r.ApplicationId).IsUnique();
    }
}
