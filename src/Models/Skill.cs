using System.Text.Json.Serialization;

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


    [JsonIgnore] public List<PostingSkill> PostingSkills { get; set; } = new();   // back-ref

    // Phase 4.5: the same shared row is now reachable from the resume side too.
    // This is what makes "skills the postings ask for, minus skills my resume
    // mentions" a join rather than a string comparison across two vocabularies.
    [JsonIgnore] public List<ResumeSkill> ResumeSkills { get; set; } = new();     // back-ref
}
