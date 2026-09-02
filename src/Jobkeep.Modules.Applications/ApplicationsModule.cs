using Jobkeep.Shared;
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
        // Scoped, matching AppDbContext's lifetime — a handler holds the
        // request's context, so a singleton here would be a captive dependency.
        services.AddScoped<ListApplicationsHandler>();
        services.AddScoped<GetApplicationHandler>();
        services.AddScoped<CreateApplicationHandler>();
        services.AddScoped<UpdateApplicationHandler>();
        services.AddScoped<DeleteApplicationHandler>();
        // PHASE 13.3c. The first way to remove a job ad; DeletePosting.cs says
        // why it arrived with the delete notifications rather than before them.
        services.AddScoped<DeletePostingHandler>();
        services.AddScoped<AddSkillToPostingHandler>();
        services.AddScoped<RemoveSkillFromPostingHandler>();
        services.AddScoped<AddRequirementToPostingHandler>();
        services.AddScoped<RemoveRequirementHandler>();

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
