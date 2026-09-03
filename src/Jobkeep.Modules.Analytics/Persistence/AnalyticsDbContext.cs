using Jobkeep.Contracts.Applications;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Analytics;

// Analytics reads three PUBLISHED VIEWS and no tables at all. Contracts'
// PublishedViews.cs has the argument for a view rather than a
// contract-per-question; the three configurations in this folder have the
// argument for why the MAPPING is owned here while the shape and the SQL are
// owned by Applications.
//
// Two things this context deliberately does not have:
//
//   * No SaveChanges worth the name. It is a DbContext, so the method exists,
//     but every type it maps is keyless — EF will not track or write them, so
//     calling it does nothing. The 13.2 interface made that a compile error by
//     omitting the method, which was a stronger statement; the trade is
//     deliberate, and it is the only place the interfaces were doing something a
//     concrete context cannot. Decision 13's whole justification was the
//     read-only constraint, so it is worth keeping visible: no handler in this
//     module calls SaveChanges, and adding one would be the review moment.
//
//   * No migrations and no migration history table. This context owns nothing to
//     create. Program.cs migrates the five table-owning contexts and not this
//     one, which is what makes "Analytics owns no tables" a fact about the
//     deployment rather than a claim in a comment.
public class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options)
    : DbContext(options)
{
    public DbSet<ApplicationStatusCount> ApplicationStatusCounts => Set<ApplicationStatusCount>();
    public DbSet<CompanyApplicationCount> CompanyApplicationCounts => Set<CompanyApplicationCount>();
    public DbSet<PostingSkillDemand> PostingSkillDemands => Set<PostingSkillDemand>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // ModelConventions.ApplyDatabaseDefaults is deliberately NOT called. It
        // sets column defaults for primary keys and audit timestamps, and a
        // keyless view type has neither. Applying it here would be harmless and
        // meaningless, which is a worse combination than leaving it out.
        model.ApplyConfigurationsFromAssembly(typeof(AnalyticsDbContext).Assembly);
    }
}
