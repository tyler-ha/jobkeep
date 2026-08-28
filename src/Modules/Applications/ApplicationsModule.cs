using Jobkeep.Shared;

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
        return services;
    }

    public static IEndpointRouteBuilder MapApplicationsModule(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/applications").WithTags("Applications");

        // GET /applications?status=&company=&title=&skill=&appliedFrom=&appliedTo=
        //                   &sort=&direction=&page=&pageSize=
        //
        // [AsParameters] binds the whole query object from the query string, so
        // the filter's shape is declared once in the slice and both surfaces —
        // and Swagger — read it from there, instead of a ten-parameter lambda
        // that has to be kept in step by hand.
        group.MapGet("/", async (
            [AsParameters] ApplicationQuery query,
            ListApplicationsHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(query, ct)).ToHttpResult());

        // GET /applications/{id}
        group.MapGet("/{id:guid}", async (
            Guid id,
            GetApplicationHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, ct)).ToHttpResult());

        // POST /applications — 201 with a Location header pointing at the new row.
        group.MapPost("/", async (
            CreateApplicationRequest request,
            CreateApplicationHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(request, ct))
                .ToHttpResult(created => Results.Created($"/applications/{created.Id}", created)));

        // PATCH /applications/{id} — send only what changed.
        group.MapPatch("/{id:guid}", async (
            Guid id,
            UpdateApplicationRequest request,
            UpdateApplicationHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, request, ct)).ToHttpResult());

        // DELETE /applications/{id}
        group.MapDelete("/{id:guid}", async (
            Guid id,
            DeleteApplicationHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, ct)).ToHttpResult(_ => Results.NoContent()));

        // POST /applications/{id}/skills — link a skill to this application's posting
        group.MapPost("/{id:guid}/skills", async (
            Guid id,
            AddSkillToPostingRequest request,
            AddSkillToPostingHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, request, ct)).ToHttpResult());

        // DELETE /applications/{id}/skills/{skillName} — unlink it again.
        // The skill NAME is the key rather than an id because that's what a caller
        // has in hand ("remove C#"), and the join row is identified by the pair.
        // The cost: names containing URL-significant characters (C#, .NET/6) must
        // be percent-encoded by the client. Accepted at personal volume; if it
        // bites, the alternative is DELETE .../skills?name= with a query string.
        group.MapDelete("/{id:guid}/skills/{skillName}", async (
            Guid id,
            string skillName,
            RemoveSkillFromPostingHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, skillName, ct)).ToHttpResult(_ => Results.NoContent()));

        // POST /applications/{id}/requirements — the table's first write path
        group.MapPost("/{id:guid}/requirements", async (
            Guid id,
            AddRequirementToPostingRequest request,
            AddRequirementToPostingHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, request, ct)).ToHttpResult());

        // DELETE /applications/{id}/requirements/{requirementId}
        group.MapDelete("/{id:guid}/requirements/{requirementId:guid}", async (
            Guid id,
            Guid requirementId,
            RemoveRequirementHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, requirementId, ct)).ToHttpResult(_ => Results.NoContent()));

        return app;
    }
}
