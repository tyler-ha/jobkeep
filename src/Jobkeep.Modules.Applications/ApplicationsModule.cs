using Jobkeep.Contracts.Applications;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Modules.Applications;

// Module wiring for Applications: DI registrations and REST routes, in one place
// so Program.cs stays a list of Add*/Map* calls and the slice files stay pure use
// case (request + handler + response). Adding a slice means adding a file and two
// lines here — not editing five layers.
//
// As of Phase 2.3 this is the ONLY route group on "/applications". Through
// Phases 2.1-2.2 it shared the prefix with Endpoints/ApplicationEndpoints.cs,
// the routes still going through IJobApplicationRepository; that file and that
// interface are gone, and the two lanes are one again.
public static class ApplicationsModule
{
    public static IServiceCollection AddApplicationsModule(this IServiceCollection services)
    {
        // PHASE 13.4 — the AddScoped<XHandler>() lines that were here are gone.
        // AddMediator() in Program.cs registers every IRequestHandler<,> and
        // INotificationHandler<> it finds in the referenced module assemblies, so
        // a new slice is now a file and a route, with nothing to remember to
        // register. What stays below is what a mediator cannot know about.

        // The contract other modules use to reach Applications-owned tables.
        // Registered by the owning module rather than by its callers, now that
        // there are two of them (Ai in Phase 4, Documents in Phase 4.5) — a
        // contract registered by whichever consumer happens to be wired first is
        // a dependency nothing in Program.cs shows.
        services.AddScoped<IPostingContract, PostingContract>();

        // Phase 13.2 — the second contract, kept separate from the first rather
        // than merged into an Applications facade. IApplicationContract.cs says why.
        services.AddScoped<IApplicationContract, ApplicationContract>();
        return services;
    }
}
