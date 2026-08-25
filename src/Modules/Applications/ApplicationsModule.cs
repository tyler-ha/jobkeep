using Jobkeep.Shared;

namespace Jobkeep.Modules.Applications;

// Module wiring for Applications: DI registrations and REST routes, in one place
// so Program.cs stays a list of Add*/Map* calls and the slice files stay pure use
// case (request + handler + response). Adding a slice means adding a file and two
// lines here — not editing five layers.
//
// The REST routes below share the "/applications" prefix with the Phase 2
// endpoints in Endpoints/ApplicationEndpoints.cs. Two route groups on one prefix
// is fine, and the split is the migration made visible: the old file holds the
// routes still going through the retiring repository, this one holds the routes
// that go through slices. The old file shrinks as later phases move its routes.
public static class ApplicationsModule
{
    public static IServiceCollection AddApplicationsModule(this IServiceCollection services)
    {
        // Scoped, matching AppDbContext's lifetime — a handler holds the
        // request's context, so a singleton here would be a captive dependency.
        services.AddScoped<AddSkillToPostingHandler>();
        services.AddScoped<RemoveSkillFromPostingHandler>();
        services.AddScoped<AddRequirementToPostingHandler>();
        services.AddScoped<RemoveRequirementHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapApplicationsModule(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/applications").WithTags("Applications");

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
