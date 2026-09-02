using Jobkeep.Modules.Applications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Analytics.Persistence;

// One of the three views Applications publishes to Analytics. See
// ApplicationStatusCountConfiguration for why the mapping is owned by the
// CONSUMER while the shape and the SQL are owned by the publisher.
//
// This is the view that stops at SkillId. Since 13.3b the join it refuses to
// make is not merely discouraged but unavailable: `skills` is a different schema
// with its own migration history, so a future edit to the SQL could not quietly
// reach it. Analytics resolves the ids through ISkillCatalog — SkillDemand.cs
// records what that costs.
public class PostingSkillDemandConfiguration : IEntityTypeConfiguration<PostingSkillDemand>
{
    public void Configure(EntityTypeBuilder<PostingSkillDemand> e)
    {
        e.HasNoKey().ToView("v_posting_skill_demand", "applications");
    }
}
