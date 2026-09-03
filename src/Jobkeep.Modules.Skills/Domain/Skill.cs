namespace Jobkeep.Models;

// A normalized, SHARED skill. Stored once (unique Name) and linked to many
// postings via PostingSkill — this is what turns "top skills across all my
// tracked jobs" into a single GROUP BY instead of scanning every posting.
public class Skill : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;   // unique, e.g. "C#"
    public string? Category { get; set; }               // "Language", "Cloud", ...

    // Phase 7 — the case-insensitive natural key. A STORED generated column:
    // Postgres computes lower("Name") on write, so it cannot drift from the
    // value it normalises and no C# writer can forget to set it. The unique
    // index lives on THIS column, not on Name, which is what makes "C#"
    // and "c#" one row instead of two.
    public string NameNormalized { get; private set; } = string.Empty;

    // Phase 7 — maintained by AuditSaveChangesInterceptor, never by hand.
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;


    // PHASE 13.3b — this class had two back-references, to PostingSkill
    // (Applications) and ResumeSkill (Documents), and both are gone along with
    // the foreign keys that backed them. It now has no navigation properties at
    // all, which is the correct shape for a shared vocabulary row: `skills` is
    // pointed AT by two other modules and points at nothing.
    //
    // The claim those back-refs supported is unchanged and still true — the same
    // row is reachable from both sides, so "skills the postings ask for, minus
    // skills my resume mentions" is still a comparison of ids rather than of two
    // vocabularies. What changed is that the comparison happens in a module that
    // holds both id sets (Ats, via two contract calls) instead of in a SQL join
    // that would not survive the split.
}
