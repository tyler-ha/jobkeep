using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Data;

// Analytics reads three PUBLISHED VIEWS and no tables at all. Views/AnalyticsViews.cs
// has the argument for why a view rather than a contract-per-question; see
// IApplicationsDbContext for why these six interfaces live in this project.
//
// No SaveChangesAsync, and that is the interesting line in this file. Analytics
// is read-only, the views are keyless, and an interface that cannot commit is a
// stronger statement of that than any comment — decision 13's whole
// justification was the read-only constraint, and this is the first time it has
// been enforced rather than asserted.
public interface IAnalyticsDbContext
{
    DbSet<ApplicationStatusCount> ApplicationStatusCounts { get; }
    DbSet<CompanyApplicationCount> CompanyApplicationCounts { get; }
    DbSet<PostingSkillDemand> PostingSkillDemands { get; }
}
