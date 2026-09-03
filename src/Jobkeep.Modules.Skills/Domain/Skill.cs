using Jobkeep.Contracts.Shared;
using Jobkeep.Persistence;
using Jobkeep.SharedKernel;
namespace Jobkeep.Modules.Skills.Domain;

// A normalized, SHARED skill. Stored once (unique Name) and linked to many
// postings via PostingSkill — this is what turns "top skills across all my
// tracked jobs" into a single GROUP BY instead of scanning every posting.
public class Skill : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;   // unique, e.g. "C#"
    public string? Category { get; set; }               // "Language", "Cloud", ...

    // PHASE 14 — the second axis. Category says which FAMILY ("Language",
    // "Practice"); Kind says whether it is a capability or a way of working.
    // They are independent: C# is Technical and a Language, Agile is Technical
    // and a Practice, Communication is Soft and Interpersonal.
    //
    // Not nullable, unlike Category, and the asymmetry is on purpose. Category
    // is genuinely optional — most skills never get one and nothing reads it as
    // a decision. Kind is a classification every skill has whether or not we
    // know it, so the missing case is a VALUE (Unknown) rather than a null, and
    // callers get an enum they can switch on without a null check. The default
    // is set database-side too, so a writer that is not EF gets it right.
    public SkillKind Kind { get; set; } = SkillKind.Unknown;

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
