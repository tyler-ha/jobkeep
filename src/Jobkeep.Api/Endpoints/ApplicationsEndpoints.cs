using Jobkeep.Modules.Applications;
using Jobkeep.Shared;

namespace Jobkeep.Api.Endpoints;

// Route mapping for the Applications module, lifted out of ApplicationsModule.cs by
// Phase 13.1 so the module project can stay a plain class library with no
// ASP.NET dependency -- the handlers never knew about HTTP, only this half did.
//
// TEMPORARY SHAPE. CLAUDE.md forbids an Endpoints/ file and it is right to: this
// is the layout Phase 2.3 deleted. It is back for one step only, because 13.5
// replaces every route here with an attribute-routed controller. Do not add a
// route to this file -- add the slice, and map it in the controller.
public static class ApplicationsEndpoints
{

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
