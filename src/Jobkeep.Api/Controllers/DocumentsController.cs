using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.AspNetCore.Mvc;

namespace Jobkeep.Api.Controllers;

// Upload, review, correct, confirm, discard — plus the resume shelf. Phase 13.5
// replaced DocumentsEndpoints.cs with this; the URLs, the responses and the
// slices behind them are unchanged.
//
// Two prefixes, one controller, because a module owns its routes and Documents
// owns `resume_skills` as well as `document_imports`. The /resumes actions escape
// this controller's [Route] with a leading ~, which is what the second MapGroup
// used to do — and it keeps both halves under the Swagger tag the endpoint file
// gave them, "Documents".
[ApiController]
[Route("imports")]
public class DocumentsController : ControllerBase
{
    // POST /imports — multipart/form-data: file, kind, and optionally label
    // and sourceUrl.
    //
    // ---------------------------------------------------------------------
    // REST only, and why GraphQL does not get this one
    // ---------------------------------------------------------------------
    // Every other write in this app exists on both surfaces, and that rule is
    // deliberately broken here. GraphQL has no file type: uploading through it
    // means the GraphQL multipart request specification, a community
    // convention layered on top of the actual spec, which HotChocolate
    // supports through an `Upload` scalar. Adopting it would put a
    // non-standard extension into the published schema so that a single
    // mutation could do what a plain HTTP POST already does well.
    //
    // The line drawn instead: the BYTES arrive over REST, and everything
    // after that — reviewing the draft, correcting it, confirming it — is on
    // both surfaces. That keeps the rule where it matters, because the rule
    // is about business logic having one implementation, and "receive a file"
    // is transport, not logic.
    // -------------------------------------------------------------------
    // The missing [FromForm] on `file` is load-bearing. Do not add it back.
    // -------------------------------------------------------------------
    // It used to carry one. It was never needed — under [ApiController],
    // binding-source inference special-cases IFormFile to the form, exactly as a
    // minimal API bound it from the multipart body by parameter name — and
    // Swashbuckle 10 *refuses* an action that has both an IFormFile and a
    // [FromForm] parameter. It refuses by throwing rather than by skipping the
    // operation, so one unrepresentable route took down the entire document:
    // GET /swagger/v1/swagger.json answered 500 and Swagger UI showed "Fetch
    // error" for every endpoint in the app, not just this one.
    //
    // The three scalars below keep their [FromForm] and must: a simple type with
    // no attribute binds from the route or query string, not the form, so
    // removing them would quietly move `kind`, `label` and `sourceUrl` off the
    // multipart body. Only the file's attribute was ever redundant, and only the
    // file's attribute is the one Swashbuckle objects to.
    //
    // This shipped in Phase 4.5 and went unnoticed until the end of Phase 5,
    // because nothing was watching. The durable half of the fix is therefore
    // the test that now pins the document
    // (tests/Jobkeep.Tests/Documents/SwaggerDocumentTests.cs): a generated
    // artefact with no build step behind it goes stale silently, which is the
    // failure mode already recorded against the committed SVG diagrams.
    //
    // -------------------------------------------------------------------
    // Antiforgery, and why there is nothing here to switch off
    // -------------------------------------------------------------------
    // The minimal API needed an explicit .DisableAntiforgery(): it enables
    // antiforgery validation for form posts by default, and without that call the
    // endpoint answered 400 for every request with no token — which is every
    // request from a script, from Swagger and from the test suite. MVC's default
    // is the opposite way round: a controller validates only when
    // [ValidateAntiForgeryToken] or the auto-validate filter is present, and
    // neither is, so the call has no equivalent and needs none.
    //
    // That is a default, not a decision, so the decision is worth restating: CSRF
    // protection exists to stop a browser attaching ambient credentials to a
    // cross-site request, and this app has no authentication and no cookies for a
    // browser to attach. When auth lands, this is one of the things that has to
    // be revisited — which is why it is a paragraph and not a missing attribute
    // nobody notices.
    //
    // Where the size cap is enforced is in Program.cs, at MapControllers(), and
    // that block explains why it could not be an attribute here.
    [HttpPost]
    [EndpointSummary("Upload a resume or job ad; returns a draft to review.")]
    public async Task<IResult> Upload(
        IFormFile file,
        [FromForm] DocumentKind kind,
        [FromForm] string? label,
        [FromForm] string? sourceUrl,
        [FromServices] ISender sender,
        [FromServices] DocumentOptions options,
        CancellationToken ct)
    {
        // The friendly refusal, not the enforcing one. The limits attached
        // to this route (see Program.cs) are what stop an oversized body from
        // being read at all; by the time this line runs the form has already
        // been bound, so anything still oversized here is within those
        // limits and merely over the app's own cap. The extractor checks the
        // real length again - file.Length is a claim by the client, and the
        // authoritative check is over the bytes actually received.
        if (file.Length > options.MaxBytes)
            return Results.BadRequest(
                $"That file is {file.Length / 1024}KB. The limit is {options.MaxBytes / 1024}KB.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        return (await sender.Send(
            new ImportDocument(buffer.ToArray(), file.FileName, kind, label, sourceUrl), ct))
            .ToHttpResult(created => Results.Created($"/imports/{created.Id}", created));
    }

    // POST /imports/text - the same import, pasted rather than uploaded.
    //
    // A sibling route rather than making `file` optional above; ImportText.cs
    // argues why, and it is the only reason this is a second action instead of
    // a second parameter. Everything past the bytes is the same handler, so the
    // response, the status codes and the Location header are identical.
    [HttpPost("text")]
    [EndpointSummary("Paste a job ad or CV as text; returns a draft to review.")]
    public async Task<IResult> UploadText(
        [FromBody] ImportText body,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(body, ct))
            .ToHttpResult(created => Results.Created($"/imports/{created.Id}", created));

    // GET /imports?status=AwaitingReview — the review queue. Defaults to what
    // is still waiting on you.
    [HttpGet]
    public async Task<IResult> List(
        [FromQuery] ImportStatus? status,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new ListImports(status), ct)).ToHttpResult();

    // GET /imports/{id} — the review screen: draft plus the extracted text.
    [HttpGet("{id:guid}")]
    public async Task<IResult> Get(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new GetImport(id), ct)).ToHttpResult();

    // PUT /imports/{id} — the user's corrections. A full replace of the
    // draft; see ReviewImport.cs for why this is not a PATCH.
    [HttpPut("{id:guid}")]
    public async Task<IResult> Review(
        Guid id,
        [FromBody] ImportDraft draft,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new ReviewImport(id, draft), ct)).ToHttpResult();

    // POST /imports/{id}/reparse — run the model over the stored text again.
    // POST because it is neither safe nor idempotent in the HTTP sense: it
    // replaces the draft, and a second call can produce a different one.
    [HttpPost("{id:guid}/reparse")]
    public async Task<IResult> Reparse(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new RestructureImport(id), ct)).ToHttpResult();

    // POST /imports/{id}/confirm — the gate. Everything before this writes
    // one row in one table nothing else reads; this is where a resume, an
    // application, skills and requirements come into existence.
    [HttpPost("{id:guid}/confirm")]
    public async Task<IResult> Confirm(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new CommitImport(id), ct)).ToHttpResult();

    // DELETE /imports/{id} — discard. Marks the row rather than removing it;
    // DiscardImport.cs explains what that buys.
    [HttpDelete("{id:guid}")]
    public async Task<IResult> Discard(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new DiscardImport(id), ct)).ToHttpResult(_ => Results.NoContent());

    // POST /resumes/{id}/skills — add a skill to a resume by name.
    //
    // The mirror of POST /applications/{id}/skills, and the first write to
    // `resume_skills` outside the import cycle. See AddSkillToResume.cs for why
    // that asymmetry was worth closing.
    [HttpPost("~/resumes/{id:guid}/skills")]
    [EndpointSummary("Add a skill to a resume, reusing the shared skill row.")]
    public async Task<IResult> AddResumeSkill(
        Guid id,
        [FromBody] AddSkillToResumeRequest request,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new AddSkillToResume(id, request), ct)).ToHttpResult();

    // DELETE /resumes/{id}/skills/{skillName} — the inverse of the above, and
    // what makes the match check's drag undoable. 204: the resource is gone, and
    // there is nothing useful to say about it.
    //
    // The name is a path segment, so a skill containing a slash cannot be
    // addressed. Deliberate, and the same limitation
    // DELETE /applications/{id}/skills/{skillName} already carries: a skill
    // name is a short vocabulary token, `C#` and `.NET` survive URL encoding
    // fine, and a query parameter would make this route disagree in shape with
    // its posting-side mirror for a case that does not occur.
    [HttpDelete("~/resumes/{id:guid}/skills/{skillName}")]
    [EndpointSummary("Remove a skill from a resume; the shared skill row survives.")]
    public async Task<IResult> RemoveResumeSkill(
        Guid id,
        string skillName,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new RemoveSkillFromResume(id, skillName), ct)).ToHttpResult(_ => Results.NoContent());

    // GET /resumes — the shelf. Summaries only; ListResumes.cs explains why
    // the resume text is not in them.
    [HttpGet("~/resumes")]
    [EndpointSummary("List resume versions, newest-updated first.")]
    public async Task<IResult> ListResumes(
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new ListResumes(), ct)).ToHttpResult();

    // GET /resumes/{id} — one resume in full, text included.
    [HttpGet("~/resumes/{id:guid}")]
    [EndpointSummary("One resume: structured records plus the text they came from.")]
    public async Task<IResult> GetResume(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new GetResume(id), ct)).ToHttpResult();

    // DELETE /resumes/{id} — the endpoint DiscardImport's error message has
    // been naming since Phase 4.5. 400, not 409, when an application or a
    // stored match check still points at it: every other refusal on both
    // surfaces goes through SliceResult.Invalid, and one route inventing a
    // third status code is the surface-specific behaviour the parity suite
    // exists to stop. DeleteResume.cs carries the argument for the checks
    // themselves.
    [HttpDelete("~/resumes/{id:guid}")]
    [EndpointSummary("Delete a resume version, if nothing still points at it.")]
    public async Task<IResult> DeleteResume(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new DeleteResume(id), ct)).ToHttpResult(_ => Results.NoContent());
}
