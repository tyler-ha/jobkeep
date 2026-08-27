using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Jobkeep.Modules.Documents;

// Configuration for document import. A plain class registered as a singleton
// rather than IOptions<T>, matching AiOptions: nothing here reloads, and
// IOptions buys change-notification this project has no use for while costing an
// unwrap in every handler.
public class DocumentOptions
{
    // The hard size cap, checked before anything parses. 5 MB is generous for a
    // resume — a text-based PDF resume is usually under 200 KB — and it is small
    // enough that a malicious upload cannot make the process do meaningful work.
    //
    // This is the app's first real attack surface, and the cap is the cheapest of
    // the mitigations: everything downstream (a PDF parser, a zip reader, a model
    // with a context window) has cost proportional to input size, so bounding the
    // input bounds all of them at once.
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;

    // Below this many characters, a document is treated as having no text. It
    // catches the scanned-PDF case, where the file parses perfectly and yields
    // nothing — see DocumentTextExtractor.
    public int MinTextChars { get; set; } = 40;

    // How much text the model is shown. Set high enough that it effectively never
    // fires on a real resume: a dense three-page resume is around 8,000
    // characters. DocumentStructurer explains why head-truncation is a worse
    // answer for a resume than it was for a job ad in Phase 4, and why the fix is
    // a bigger limit plus an honest warning rather than chunking.
    public int MaxStructureChars { get; set; } = 24000;

    // The review queue is not paged — it is a list of things you have not
    // finished. This caps it so an unbounded query can never be issued.
    public int MaxListSize { get; set; } = 200;
}

// Module wiring for Documents: DI plus the /imports routes.
//
// ---------------------------------------------------------------------------
// What this module owns
// ---------------------------------------------------------------------------
// `document_imports`, `resumes`, `resume_skills`, `resume_experiences` and
// `resume_educations`. It writes `skills` — the shared vocabulary table that
// belongs to no module — and reaches Applications-owned tables only through
// IPostingContract and through Applications' own use-case handlers. See
// CommitImport.cs for why those two are different things.
//
// It calls a language model and does not live in the Ai module, which is the
// point Shared/ModelClient.cs makes at length: Ai owns a table, not a technology.
public static class DocumentsModule
{
    public static IServiceCollection AddDocumentsModule(
        this IServiceCollection services, IConfiguration config)
    {
        var options = new DocumentOptions();
        config.GetSection("Documents").Bind(options);
        services.AddSingleton(options);

        // Both are stateless and hold no scoped dependency of their own, so
        // singleton is safe — but they are registered scoped anyway to match the
        // handlers that consume them, because DocumentStructurer's constructor
        // takes IChatClient and a future decorator around it (retry, logging,
        // caching) would very plausibly be scoped. Registering them scoped now
        // costs nothing and removes a captive-dependency trap later.
        services.AddScoped<IDocumentTextExtractor, DocumentTextExtractor>();
        services.AddScoped<IDocumentStructurer, DocumentStructurer>();

        services.AddScoped<ImportDocumentHandler>();
        services.AddScoped<GetImportHandler>();
        services.AddScoped<ListImportsHandler>();
        services.AddScoped<ReviewImportHandler>();
        services.AddScoped<RestructureImportHandler>();
        services.AddScoped<CommitImportHandler>();
        services.AddScoped<DiscardImportHandler>();

        return services;
    }

    public static IEndpointRouteBuilder MapDocumentsModule(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/imports").WithTags("Documents");

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
        group.MapPost("/", async (
            [FromForm] IFormFile file,
            [FromForm] DocumentKind kind,
            [FromForm] string? label,
            [FromForm] string? sourceUrl,
            ImportDocumentHandler handler,
            DocumentOptions options,
            CancellationToken ct) =>
        {
            // Checked against the declared length before a byte is read, so an
            // oversized upload is refused rather than buffered and then refused.
            // The extractor checks the real length again — this one is a claim by
            // the client, and the authoritative check is over the bytes actually
            // received.
            if (file.Length > options.MaxBytes)
                return Results.BadRequest(
                    $"That file is {file.Length / 1024}KB. The limit is {options.MaxBytes / 1024}KB.");

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);

            return (await handler.HandleAsync(
                buffer.ToArray(), file.FileName, kind, label, sourceUrl, ct))
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
        .WithSummary("Upload a resume or job ad; returns a draft to review.");

        // GET /imports?status=AwaitingReview — the review queue. Defaults to what
        // is still waiting on you.
        group.MapGet("/", async (
            ImportStatus? status,
            ListImportsHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(status, ct)).ToHttpResult());

        // GET /imports/{id} — the review screen: draft plus the extracted text.
        group.MapGet("/{id:guid}", async (
            Guid id,
            GetImportHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, ct)).ToHttpResult());

        // PUT /imports/{id} — the user's corrections. A full replace of the
        // draft; see ReviewImport.cs for why this is not a PATCH.
        group.MapPut("/{id:guid}", async (
            Guid id,
            ImportDraft draft,
            ReviewImportHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, draft, ct)).ToHttpResult());

        // POST /imports/{id}/reparse — run the model over the stored text again.
        // POST because it is neither safe nor idempotent in the HTTP sense: it
        // replaces the draft, and a second call can produce a different one.
        group.MapPost("/{id:guid}/reparse", async (
            Guid id,
            RestructureImportHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, ct)).ToHttpResult());

        // POST /imports/{id}/confirm — the gate. Everything before this writes
        // one row in one table nothing else reads; this is where a resume, an
        // application, skills and requirements come into existence.
        group.MapPost("/{id:guid}/confirm", async (
            Guid id,
            CommitImportHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, ct)).ToHttpResult());

        // DELETE /imports/{id} — discard. Marks the row rather than removing it;
        // DiscardImport.cs explains what that buys.
        group.MapDelete("/{id:guid}", async (
            Guid id,
            DiscardImportHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, ct)).ToHttpResult(_ => Results.NoContent()));

        return app;
    }
}
