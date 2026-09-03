using Jobkeep.Modules.Applications;
using Jobkeep.Shared;
using Mediator;

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
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new ListApplications(query), ct)).ToHttpResult());

        // GET /applications/{id}
        group.MapGet("/{id:guid}", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new GetApplication(id), ct)).ToHttpResult());

        // POST /applications — 201 with a Location header pointing at the new row.
        group.MapPost("/", async (
            CreateApplicationRequest request,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new CreateApplication(request), ct))
                .ToHttpResult(created => Results.Created($"/applications/{created.Id}", created)));

        // PATCH /applications/{id} — send only what changed.
        group.MapPatch("/{id:guid}", async (
            Guid id,
            UpdateApplicationRequest request,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new UpdateApplication(id, request), ct)).ToHttpResult());

        // DELETE /applications/{id}
        group.MapDelete("/{id:guid}", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new DeleteApplication(id), ct)).ToHttpResult(_ => Results.NoContent()));

        // POST /applications/{id}/skills — link a skill to this application's posting
        group.MapPost("/{id:guid}/skills", async (
            Guid id,
            AddSkillToPostingRequest request,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new AddSkillToPosting(id, request), ct)).ToHttpResult());

        // DELETE /applications/{id}/skills/{skillName} — unlink it again.
        // The skill NAME is the key rather than an id because that's what a caller
        // has in hand ("remove C#"), and the join row is identified by the pair.
        // The cost: names containing URL-significant characters (C#, .NET/6) must
        // be percent-encoded by the client. Accepted at personal volume; if it
        // bites, the alternative is DELETE .../skills?name= with a query string.
        group.MapDelete("/{id:guid}/skills/{skillName}", async (
            Guid id,
            string skillName,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new RemoveSkillFromPosting(id, skillName), ct)).ToHttpResult(_ => Results.NoContent()));

        // POST /applications/{id}/requirements — the table's first write path
        group.MapPost("/{id:guid}/requirements", async (
            Guid id,
            AddRequirementToPostingRequest request,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new AddRequirementToPosting(id, request), ct)).ToHttpResult());

        // DELETE /applications/{id}/requirements/{requirementId}
        group.MapDelete("/{id:guid}/requirements/{requirementId:guid}", async (
            Guid id,
            Guid requirementId,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new RemoveRequirement(id, requirementId), ct)).ToHttpResult(_ => Results.NoContent()));

        // PHASE 13.3c — a second group, under a second prefix, in the same
        // module. The URL follows the resource a caller is thinking about ("this
        // job ad") while the code follows the module that owns `job_postings`,
        // which is the same split AiEndpoints makes when it maps /applications
        // routes and DocumentsEndpoints makes when it maps /resumes.
        //
        // DELETE and nothing else, on purpose. A posting is still created
        // implicitly by logging an application and read through it, so a GET here
        // would be a second way to fetch something /applications/{id} already
        // returns. DeletePosting.cs says why the delete alone had to exist.
        var postings = app.MapGroup("/postings").WithTags("Applications");

        // DELETE /postings/{id} — 400 while any application still names it.
        postings.MapDelete("/{id:guid}", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new DeletePosting(id), ct)).ToHttpResult(_ => Results.NoContent()))
            .WithSummary("Delete a job ad, once no application is logged against it.");

        return app;
    }
}
