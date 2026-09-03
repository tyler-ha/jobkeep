namespace Jobkeep.Models;

// How senior the ad reads. Lives here rather than with the other posting enums
// because it is the analyzer's OUTPUT, not something the ad states — Ai is the
// only module that writes it or reads it back.
public enum SeniorityLevel { Unknown, Junior, Mid, Senior, Lead, Principal }

// Phase 4 output — 1:1 with a posting (facts derived from its description).
// Extracted skills are written to PostingSkill with Source = AiExtracted,
// so they sit alongside human-entered skills instead of in a parallel list.
public class AiAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // 13.3b CUT THE NAVIGATION AND THE FOREIGN KEY. `job_postings` is
    // Applications' table in its own schema, so the CASCADE that used to delete
    // this row with its posting is gone.
    //
    // 13.3c REPLACED IT with a delete notification: Applications publishes
    // PostingDeleted and this module's OnPostingDeleted removes the row. The
    // outcome is identical and the enforcement is not — an orphan is now
    // prevented by a subscriber that ran, rather than by a constraint that
    // could not have been skipped. Between the two steps the orphan was real,
    // and DeleteBehaviourTests asserted it on purpose.
    //
    // The 1:1 itself is unaffected and still enforced here, by a unique index on
    // this column — it was previously a side effect of HasForeignKey<AiAnalysis>,
    // which is the sort of guarantee that disappears silently when the FK it rode
    // on is dropped.
    public Guid PostingId { get; set; }

    public SeniorityLevel Seniority { get; set; } = SeniorityLevel.Unknown;
    public string? Summary { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime AnalyzedAtUtc { get; set; } = DateTime.UtcNow;
}
