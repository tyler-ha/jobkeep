using Jobkeep.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Modules.Analytics;

// Module wiring for Analytics: DI registrations and the /stats route group, in
// one place, so Program.cs stays a list of Add*/Map* calls.
//
// Analytics is READ-ONLY. It owns no tables, has no mutations, and every one of
// its handlers is a single aggregate query. That is the property to hold on to,
// because it is what makes the next paragraph acceptable.
//
// ---------------------------------------------------------------------------
// The boundary tradeoff, said out loud
// ---------------------------------------------------------------------------
// architecture.md rule 2 says a module only queries the tables it owns, and
// cross-module reads go through a contract on the owning module. This module
// reads `job_applications`, `job_postings` and `companies`, all of which belong
// to Applications. So it bends the rule on its first outing, and pretending
// otherwise would be worse than saying so.
//
// The three options were:
//
//   1. Put a contract on Applications — an interface it exposes and Analytics
//      calls. The problem is what that contract would contain: one method per
//      analytics question, each one a query. That is IJobApplicationRepository
//      coming back under a new name, six weeks after Phase 2.3 deleted it for
//      growing exactly that way. The rule would be satisfied and the code worse.
//   2. Move the funnel into Applications, leaving Analytics only the skill
//      query. Defensible, but it splits one feature across two modules on a
//      technicality and gives the caller /applications/funnel next to
//      /stats/skill-demand.
//   3. Let a read-only reporting module read across boundaries, and write down
//      what that costs.
//
// This is option 3, which is also the ordinary answer in practice: reporting is
// the classic exception to a module boundary, because a report is by definition
// a question about several modules at once. The constraint that keeps it honest
// is the read-only one — Analytics can never leave another module's data in a
// state that module didn't choose, so the coupling is to a *shape*, not to a
// lifecycle.
//
// The cost, which is real: extracting Analytics into its own service later is no
// longer a pure code-move. It would need those tables reachable — a read replica,
// a view, or an event feed rebuilding a local read model. That is a known and
// bounded migration rather than a redesign, and it is the trade this module
// accepts. architecture.md decision 13 records it.
public static class AnalyticsModule
{
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services)
    {
        // Scoped, matching AppDbContext — a singleton handler holding a scoped
        // context is the captive-dependency bug ApplicationsModule calls out.
        services.AddScoped<SkillDemandHandler>();
        services.AddScoped<StatusFunnelHandler>();
        services.AddScoped<CompanyRollupHandler>();
        return services;
    }
}
