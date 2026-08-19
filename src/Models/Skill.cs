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
}
