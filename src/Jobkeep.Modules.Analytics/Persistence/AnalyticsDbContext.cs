using Jobkeep.Contracts.Applications;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Jobkeep.SharedKernel;

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
public class AnalyticsDbContext : DbContext
{
    // PHASE 11.2b — see ApplicationsDbContext for why this is captured in the
    // constructor rather than read per query.
    //
    // This context owns no tables and never migrates, but it reads three VIEWS,
    // and Phase 8 already learned what that means: raw SQL walks straight past a
    // query filter. The views were re-cut to carry OwnerUserId, so the filter
    // has a column to stand on and the three slices needed no edit — the same
    // property the view abstraction bought in Phase 8, demonstrating itself a
    // second time.
    private readonly Guid? _ownerId;

    public AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options, ICurrentUser currentUser)
        : base(options) => _ownerId = currentUser.UserId;

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

        model.Entity<ApplicationStatusCount>().HasQueryFilter(
            QueryFilters.Owner, x => x.OwnerUserId == _ownerId);
        model.Entity<CompanyApplicationCount>().HasQueryFilter(
            QueryFilters.Owner, x => x.OwnerUserId == _ownerId);
        model.Entity<PostingSkillDemand>().HasQueryFilter(
            QueryFilters.Owner, x => x.OwnerUserId == _ownerId);
    }
}
