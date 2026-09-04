using System.Text.Json.Serialization;
using Jobkeep.Contracts.Shared;
using Jobkeep.Contracts.Skills;

namespace Jobkeep.Modules.Applications.Domain;

// Join row (many-to-many) between a posting and a shared skill.
//   IsRequired = must-have vs nice-to-have.
//   Source     = human-entered vs extracted by the Phase 4 AI analyzer,
//                so both kinds live together rather than in parallel lists.
public class PostingSkill
{
    public Guid PostingId { get; set; }
    [JsonIgnore] public JobPosting Posting { get; set; } = null!;   // back-ref

    // 13.3b CUT THE NAVIGATION AND THE FOREIGN KEY. `skills` is the Skills
    // module's table in its own schema, so this is a bare Guid that Postgres does
    // not check, and the RESTRICT that used to stop a shared skill row being
    // deleted out from under a link is gone.
    //
    // What replaces it is the rule that was already true: a link row is only ever
    // created through ISkillCatalog.FindOrCreateAsync, which returns the id of a
    // row it has just saved. The guarantee moved from the database to the one
    // code path allowed to make it. 13.2 removed every query that traversed this
    // property, which is why cutting it changes nothing to read.
    public Guid SkillId { get; set; }

    public bool IsRequired { get; set; }
    public SkillSource Source { get; set; } = SkillSource.Parsed;
}
