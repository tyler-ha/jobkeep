using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Modules.Analytics;

// Module wiring for Analytics: DI registrations and the /stats route group, in
// one place, so Program.cs stays a list of Add*/Map* calls.
//
// Analytics is READ-ONLY. It owns no tables, has no mutations, and every one of
// its handlers is a single aggregate query. Until Phase 13.2 that property was
// what made the next section's exception acceptable; it is now enforced by
// AnalyticsDbContext, which has no SaveChangesAsync to call.
//
// ---------------------------------------------------------------------------
// The boundary tradeoff, rewritten in Phase 13.2 because the old one is now wrong
// ---------------------------------------------------------------------------
// This comment used to argue that a read-only reporting module should be allowed
// to read `job_applications`, `job_postings`, `companies` and `posting_skills`
// directly, and it recorded the cost honestly: "extracting Analytics into its own
// service later is no longer a pure code-move." architecture.md decision 13.
//
// The argument was sound and the decision was still wrong, which is worth being
// precise about rather than quietly deleting. It was answering "is this safe?"
// — and it is: a module that only reads can never leave another module's data in
// a state that module did not choose. Phase 13 is answering a different question,
// "can this module be lifted out?", and read-only buys nothing there. A SELECT
// across a boundary is exactly the thing that stops working when the boundary
// becomes a network.
//
// The three options were the same three as before; what changed is which one is
// available. Option 1, a contract with one method per analytics question, is
// still IJobApplicationRepository coming back — unbounded by construction,
// because there is no limit on how many questions a reporting module has, and
// this project has deleted that shape twice (decisions 5 and 13). Option 2,
// moving the funnel into Applications, still splits one feature across two
// modules on a technicality.
//
// Option 4, which the earlier version did not consider: Applications PUBLISHES
// three views and Analytics reads those. The aggregate still runs in Postgres,
// so nothing is loaded and counted in C#. The coupling is to a shape the owner
// chose to expose rather than to its table layout — so Applications can rename a
// column and fix its own view, and this module does not notice. And it is what a
// real service split does: at extraction the view becomes a read replica, a
// materialised view or an event feed, and none of the code below changes.
//
// The cost, which is real and smaller than the one it replaces: the views are
// hand-written SQL in a migration, so a schema change can break one without the
// compiler saying so. AnalyticsTests compares every stat against hand-written SQL
// over the base tables, which is what catches that.
//
// Views/AnalyticsViews.cs holds the view definitions and the per-view reasoning.
// AnalyticsDbContext has no SaveChangesAsync at all — read-only is now enforced
// by the type rather than asserted in a comment.
public static class AnalyticsModule
{
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services)
    {
        // PHASE 13.4 — the AddScoped<XHandler>() lines that were here are gone.
        // AddMediator() in Program.cs registers every IRequestHandler<,> it finds
        // in the referenced module assemblies, so this module's slices need no
        // registration at all and this method now registers only its context and
        // its contracts.
        return services;
    }
}
