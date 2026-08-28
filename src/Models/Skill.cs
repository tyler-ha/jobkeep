using System.Text.Json.Serialization;

namespace Jobkeep.Models;

// A normalized, SHARED skill. Stored once (unique Name) and linked to many
// postings via PostingSkill — this is what turns "top skills across all my
// tracked jobs" into a single GROUP BY instead of scanning every posting.
public class Skill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;   // unique, e.g. "C#"
    public string? Category { get; set; }               // "Language", "Cloud", ...

    [JsonIgnore] public List<PostingSkill> PostingSkills { get; set; } = new();   // back-ref

    // Phase 4.5: the same shared row is now reachable from the resume side too.
    // This is what makes "skills the postings ask for, minus skills my resume
    // mentions" a join rather than a string comparison across two vocabularies.
    [JsonIgnore] public List<ResumeSkill> ResumeSkills { get; set; } = new();     // back-ref
}
