using Jobkeep.Modules.Applications;
using Mediator;
using Microsoft.AspNetCore.Mvc;

namespace Jobkeep.Api.Controllers;

// The core module's routes. Phase 13.5 replaced ApplicationsEndpoints.cs with
// this; the URLs, the responses and the slices behind them are unchanged.
[ApiController]
[Route("applications")]
public class ApplicationsController : ControllerBase
{
    // GET /applications?status=&company=&title=&skill=&appliedFrom=&appliedTo=
    //                   &sort=&direction=&page=&pageSize=
    //
    // [FromQuery] on the whole object binds every property from the query string,
    // so the filter's shape is declared once in the slice and both surfaces — and
    // Swagger — read it from there, instead of a ten-parameter signature that has
    // to be kept in step by hand. It is the minimal API's [AsParameters] under
    // another name; ApplicationQuery needed no change to serve both.
    [HttpGet]
    public async Task<IResult> List(
        [FromQuery] ApplicationQuery query,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new ListApplications(query), ct)).ToHttpResult();

    // GET /applications/board — PHASE 9, gap 3. Every card at once.
    //
    // Declared before the {id:guid} route for readability only; it cannot be
    // shadowed by it, because "board" is not a Guid and the constraint says so.
    [HttpGet("board")]
    [EndpointSummary("Every application as a board card, newest first, capped.")]
    public async Task<IResult> Board(
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new GetBoard(), ct)).ToHttpResult();

    // GET /applications/{id}
    [HttpGet("{id:guid}")]
    public async Task<IResult> Get(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new GetApplication(id), ct)).ToHttpResult();

    // POST /applications — 201 with a Location header pointing at the new row.
    [HttpPost]
    public async Task<IResult> Create(
        [FromBody] CreateApplicationRequest request,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new CreateApplication(request), ct))
            .ToHttpResult(created => Results.Created($"/applications/{created.Id}", created));

    // PATCH /applications/{id} — send only what changed.
    [HttpPatch("{id:guid}")]
    public async Task<IResult> Update(
        Guid id,
        [FromBody] UpdateApplicationRequest request,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new UpdateApplication(id, request), ct)).ToHttpResult();

    // DELETE /applications/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IResult> Delete(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new DeleteApplication(id), ct)).ToHttpResult(_ => Results.NoContent());

    // POST /applications/{id}/restore — PHASE 8, the undo behind the archive.
    //
    // POST rather than PATCH, and rather than a body on the DELETE. A restore is
    // a named operation on a resource, not a partial update of its fields: the
    // caller does not say WHICH fields, and `PATCH {"isDeleted": false}` would
    // publish two storage columns as part of the wire contract for no gain. The
    // shape matches the other verbs this API already has — /confirm, /reparse,
    // /match-check — all of which are things you DO to a row rather than edits
    // you make to one.
    //
    // 404 when the row is live or absent; RestoreApplication.cs argues why those
    // are the same answer.
    [HttpPost("{id:guid}/restore")]
    [EndpointSummary("Bring an archived application back.")]
    public async Task<IResult> Restore(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new RestoreApplication(id), ct)).ToHttpResult(_ => Results.NoContent());

    // POST /applications/{id}/skills — link a skill to this application's posting
    [HttpPost("{id:guid}/skills")]
    public async Task<IResult> AddSkill(
        Guid id,
        [FromBody] AddSkillToPostingRequest request,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new AddSkillToPosting(id, request), ct)).ToHttpResult();

    // DELETE /applications/{id}/skills/{skillName} — unlink it again.
    // The skill NAME is the key rather than an id because that's what a caller
    // has in hand ("remove C#"), and the join row is identified by the pair.
    // The cost: names containing URL-significant characters (C#, .NET/6) must
    // be percent-encoded by the client. Accepted at personal volume; if it
    // bites, the alternative is DELETE .../skills?name= with a query string.
    [HttpDelete("{id:guid}/skills/{skillName}")]
    public async Task<IResult> RemoveSkill(
        Guid id,
        string skillName,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new RemoveSkillFromPosting(id, skillName), ct)).ToHttpResult(_ => Results.NoContent());

    // POST /applications/{id}/requirements — the table's first write path
    [HttpPost("{id:guid}/requirements")]
    public async Task<IResult> AddRequirement(
        Guid id,
        [FromBody] AddRequirementToPostingRequest request,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new AddRequirementToPosting(id, request), ct)).ToHttpResult();

    // DELETE /applications/{id}/requirements/{requirementId}
    [HttpDelete("{id:guid}/requirements/{requirementId:guid}")]
    public async Task<IResult> RemoveRequirement(
        Guid id,
        Guid requirementId,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new RemoveRequirement(id, requirementId), ct)).ToHttpResult(_ => Results.NoContent());

    // PHASE 13.3c — a second prefix, in the same module. The URL follows the
    // resource a caller is thinking about ("this job ad") while the code follows
    // the module that owns `job_postings`, which is the same split AiController
    // makes when it maps /applications routes and DocumentsController makes when
    // it maps /resumes.
    //
    // The leading ~ escapes this controller's [Route] rather than appending to
    // it. That is what the second MapGroup used to buy, and keeping it here
    // rather than in a PostingsController keeps the Swagger tag on "Applications",
    // where the endpoint file deliberately put it.
    //
    // DELETE and nothing else, on purpose. A posting is still created implicitly
    // by logging an application and read through it, so a GET here would be a
    // second way to fetch something /applications/{id} already returns.
    // DeletePosting.cs says why the delete alone had to exist.
    //
    // DELETE /postings/{id} — 400 while any application still names it.
    [HttpDelete("~/postings/{id:guid}")]
    [EndpointSummary("Delete a job ad, once no application is logged against it.")]
    public async Task<IResult> DeletePosting(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new DeletePosting(id), ct)).ToHttpResult(_ => Results.NoContent());

    // POST /postings/{id}/restore — PHASE 8. Same shape, same schema, same tag.
    [HttpPost("~/postings/{id:guid}/restore")]
    [EndpointSummary("Bring an archived job ad back.")]
    public async Task<IResult> RestorePosting(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new RestorePosting(id), ct)).ToHttpResult(_ => Results.NoContent());
}
