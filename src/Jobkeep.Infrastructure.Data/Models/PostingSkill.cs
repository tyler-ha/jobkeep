using System.Text.Json.Serialization;

namespace Jobkeep.Models;

// Join row (many-to-many) between a posting and a shared skill.
//   IsRequired = must-have vs nice-to-have.
//   Source     = human-entered vs extracted by the Phase 4 AI analyzer,
//                so both kinds live together rather than in parallel lists.
public class PostingSkill
{
    public Guid PostingId { get; set; }
    [JsonIgnore] public JobPosting Posting { get; set; } = null!;   // back-ref

    public Guid SkillId { get; set; }
    public Skill Skill { get; set; } = null!;

    public bool IsRequired { get; set; }
    public SkillSource Source { get; set; } = SkillSource.Parsed;
}
