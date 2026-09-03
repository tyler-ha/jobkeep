using Jobkeep.Models;
using Jobkeep.Modules.Documents;
using Jobkeep.Shared;
using Mediator;
using Microsoft.AspNetCore.Mvc;

namespace Jobkeep.Api.Endpoints;

// Route mapping for the Documents module, lifted out of DocumentsModule.cs by
// Phase 13.1 so the module project can stay a plain class library with no
// ASP.NET dependency -- the handlers never knew about HTTP, only this half did.
//
// TEMPORARY SHAPE. CLAUDE.md forbids an Endpoints/ file and it is right to: this
// is the layout Phase 2.3 deleted. It is back for one step only, because 13.5
// replaces every route here with an attribute-routed controller. Do not add a
// route to this file -- add the slice, and map it in the controller.
public static class DocumentsEndpoints
{

    public static IEndpointRouteBuilder MapDocumentsModule(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/imports").WithTags("Documents");

        // Resolved here rather than injected into the upload lambda because the
        // size limits below are endpoint METADATA: they have to be known when the
        // route is mapped, not when a request arrives. The lambda still takes
        // DocumentOptions separately, for the check it makes on its own.
        var uploadOptions = app.ServiceProvider.GetRequiredService<DocumentOptions>();

        // The envelope a multipart body carries on top of the file itself:
        // boundaries, part headers, and the three small text fields. 16 KB is far
        // more than that costs and far less than a second file would.
        const long MultipartEnvelopeSlack = 16 * 1024;

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
        // It used to carry one. Minimal APIs never needed it — an IFormFile binds
        // from the multipart body by parameter name on its own — and Swashbuckle
        // 10 *refuses* an action that has both an IFormFile and a [FromForm]
        // parameter. It refuses by throwing rather than by skipping the operation,
        // so one unrepresentable route took down the entire document:
        // GET /swagger/v1/swagger.json answered 500 and Swagger UI showed "Fetch
        // error" for every endpoint in the app, not just this one.
        //
        // The three scalars below keep their [FromForm] and must: without it a
        // minimal API binds a simple type from the route or query string, not the
        // form, so removing them would quietly move `kind`, `label` and `sourceUrl`
        // off the multipart body. Only the file's attribute was ever redundant,
        // and only the file's attribute is the one Swashbuckle objects to.
        //
        // This shipped in Phase 4.5 and went unnoticed until the end of Phase 5,
        // because nothing was watching. The durable half of the fix is therefore
        // the test that now pins the document
        // (tests/Jobkeep.Tests/Documents/SwaggerDocumentTests.cs): a generated
        // artefact with no build step behind it goes stale silently, which is the
        // failure mode already recorded against the committed SVG diagrams.
        group.MapPost("/", async (
            IFormFile file,
            [FromForm] DocumentKind kind,
            [FromForm] string? label,
            [FromForm] string? sourceUrl,
            ISender sender,
            DocumentOptions options,
            CancellationToken ct) =>
        {
            // The friendly refusal, not the enforcing one. The limits attached
            // to this route (see below) are what stop an oversized body from
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
        })
        // Required for [FromForm] binding in a minimal API. ASP.NET Core enables
        // antiforgery validation for form posts by default, and without this the
        // endpoint answers 400 for every request that has no token — which is
        // every request from a script, from Swagger and from the test suite.
        //
        // Turning it off is correct HERE and would not be correct on a
        // cookie-authenticated form: CSRF protection exists to stop a browser
        // attaching ambient credentials to a cross-site request, and this app has
        // no authentication and no cookies for a browser to attach. When auth
        // lands, this line is one of the things that has to be revisited, which
        // is why it is a paragraph and not a fluent call nobody reads.
        .DisableAntiforgery()
        // -------------------------------------------------------------------
        // Where the size cap is ACTUALLY enforced
        // -------------------------------------------------------------------
        // The `file.Length > MaxBytes` check inside the handler is not the first
        // line of defence, and the comment above it used to imply it was. By the
        // time a [FromForm] parameter is bound, ASP.NET Core has already read the
        // whole multipart body — files over 64 KB spool to a temp file on disk —
        // so without these two limits a 30 MB upload is written to disk in full
        // and only then answered "the limit is 5120KB". The framework defaults
        // are 128 MB for a multipart body and 30 MB for the request, both an
        // order of magnitude above anything this endpoint wants.
        //
        // These two do the refusing, before the bytes are stored:
        //   - multipartBodyLengthLimit stops the form reader mid-stream and is
        //     what a client sending an oversized part actually hits.
        //   - RequestSizeLimit is the belt to that braces: it bounds the whole
        //     request, so a body that is oversized in some way the multipart
        //     reader would not count still cannot get through.
        //
        // The handler's own check stays. It is cheap, it produces the friendly
        // message with the real numbers in it, and it is the one that fires when
        // a client declares a length under the cap — these limits are about what
        // an attacker can make the server DO, not about what a user is told.
        .WithFormOptions(multipartBodyLengthLimit: uploadOptions.MaxBytes)
        // Attached as metadata rather than through a fluent helper because
        // minimal APIs do not have one - RequestSizeLimitAttribute is an MVC
        // type that also implements IRequestSizeLimitMetadata, which is what
        // routing reads to set the request body limit for this endpoint.
        .WithMetadata(new RequestSizeLimitAttribute(
            uploadOptions.MaxBytes + MultipartEnvelopeSlack))
        .WithSummary("Upload a resume or job ad; returns a draft to review.");

        // GET /imports?status=AwaitingReview — the review queue. Defaults to what
        // is still waiting on you.
        group.MapGet("/", async (
            ImportStatus? status,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new ListImports(status), ct)).ToHttpResult());

        // GET /imports/{id} — the review screen: draft plus the extracted text.
        group.MapGet("/{id:guid}", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new GetImport(id), ct)).ToHttpResult());

        // PUT /imports/{id} — the user's corrections. A full replace of the
        // draft; see ReviewImport.cs for why this is not a PATCH.
        group.MapPut("/{id:guid}", async (
            Guid id,
            ImportDraft draft,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new ReviewImport(id, draft), ct)).ToHttpResult());

        // POST /imports/{id}/reparse — run the model over the stored text again.
        // POST because it is neither safe nor idempotent in the HTTP sense: it
        // replaces the draft, and a second call can produce a different one.
        group.MapPost("/{id:guid}/reparse", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new RestructureImport(id), ct)).ToHttpResult());

        // POST /imports/{id}/confirm — the gate. Everything before this writes
        // one row in one table nothing else reads; this is where a resume, an
        // application, skills and requirements come into existence.
        group.MapPost("/{id:guid}/confirm", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new CommitImport(id), ct)).ToHttpResult());

        // DELETE /imports/{id} — discard. Marks the row rather than removing it;
        // DiscardImport.cs explains what that buys.
        group.MapDelete("/{id:guid}", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new DiscardImport(id), ct)).ToHttpResult(_ => Results.NoContent()));

        // A second group, under a second path prefix, in the same module — because
        // a module owns its routes, and Documents owns `resume_skills`. The URL
        // follows the resource (/resumes/...) while the code follows the owner, the
        // same split AiModule.cs makes when it maps routes under /applications.
        var resumes = app.MapGroup("/resumes").WithTags("Documents");

        // POST /resumes/{id}/skills — add a skill to a resume by name.
        //
        // The mirror of POST /applications/{id}/skills, and the first write to
        // `resume_skills` outside the import cycle. See AddSkillToResume.cs for why
        // that asymmetry was worth closing.
        resumes.MapPost("/{id:guid}/skills", async (
            Guid id,
            AddSkillToResumeRequest request,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new AddSkillToResume(id, request), ct)).ToHttpResult())
            .WithSummary("Add a skill to a resume, reusing the shared skill row.");

        // DELETE /resumes/{id}/skills/{skillName} — the inverse of the above, and
        // what makes the ATS check's drag undoable. 204: the resource is gone, and
        // there is nothing useful to say about it.
        //
        // The name is a path segment, so a skill containing a slash cannot be
        // addressed. Deliberate, and the same limitation
        // DELETE /applications/{id}/skills/{skillName} already carries: a skill
        // name is a short vocabulary token, `C#` and `.NET` survive URL encoding
        // fine, and a query parameter would make this route disagree in shape with
        // its posting-side mirror for a case that does not occur.
        resumes.MapDelete("/{id:guid}/skills/{skillName}", async (
            Guid id,
            string skillName,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new RemoveSkillFromResume(id, skillName), ct)).ToHttpResult(_ => Results.NoContent()))
            .WithSummary("Remove a skill from a resume; the shared skill row survives.");

        // GET /resumes — the shelf. Summaries only; ListResumes.cs explains why
        // the resume text is not in them.
        resumes.MapGet("/", async (
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new ListResumes(), ct)).ToHttpResult())
            .WithSummary("List resume versions, newest-updated first.");

        // GET /resumes/{id} — one resume in full, text included.
        resumes.MapGet("/{id:guid}", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new GetResume(id), ct)).ToHttpResult())
            .WithSummary("One resume: structured records plus the text they came from.");

        // DELETE /resumes/{id} — the endpoint DiscardImport's error message has
        // been naming since Phase 4.5. 400, not 409, when an application or a
        // stored ATS check still points at it: every other refusal on both
        // surfaces goes through SliceResult.Invalid, and one route inventing a
        // third status code is the surface-specific behaviour the parity suite
        // exists to stop. DeleteResume.cs carries the argument for the checks
        // themselves.
        resumes.MapDelete("/{id:guid}", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new DeleteResume(id), ct)).ToHttpResult(_ => Results.NoContent()))
            .WithSummary("Delete a resume version, if nothing still points at it.");

        return app;
    }
}
