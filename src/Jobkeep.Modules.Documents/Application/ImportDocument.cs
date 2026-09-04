using System.Security.Cryptography;
using System.Text.Json;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

// HotChocolate publishes a global `Path` type (its GraphQL field-path), which
// collides with System.IO.Path. Aliased rather than fully qualified at each use:
// the filename handling below is the only thing here that touches it.
using Path = System.IO.Path;

namespace Jobkeep.Modules.Documents;

// Slice: upload a document, extract its text, and propose a draft.
//
// This is step one of three. Nothing here writes a resume, an application or a
// skill — it writes ONE document_imports row that a human then confirms
// (CommitImport.cs) or throws away (DiscardImport.cs). That gate is the feature.

// The full view of an import, including the extracted text. The text is here on
// purpose: this is the review screen's payload, and "does the draft match the
// document" is a question you cannot answer without the document. ListImports
// returns a summary without it, the same split as ApplicationDetail vs
// ApplicationListItem.
public record ImportResponse(
    Guid Id,
    DocumentKind Kind,
    ImportStatus Status,
    string FileName,
    SourceFormat Format,
    long ByteCount,
    string ContentHash,
    string ExtractedText,
    ImportDraft Draft,
    string? ModelUsed,
    string? Warning,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? CommittedEntityId);

public record ImportDocument(
    byte[] Bytes,
    string FileName,
    DocumentKind Kind,
    string? Label,
    string? SourceUrl) : IRequest<SliceResult<ImportResponse>>;

public class ImportDocumentHandler : IRequestHandler<ImportDocument, SliceResult<ImportResponse>>
{
    private readonly DocumentsDbContext _db;
    private readonly IDocumentTextExtractor _extractor;
    private readonly DocumentOptions _options;
    private readonly ImportParseQueue _queue;

    // IDocumentStructurer and ModelOptions were dependencies here until group 6.
    // The upload no longer calls the model at all, so it no longer needs to know
    // one exists — RestructureImport is where both moved to, and this handler
    // now names only the queue that asks for the work to be done.
    public ImportDocumentHandler(
        DocumentsDbContext db,
        IDocumentTextExtractor extractor,
        DocumentOptions options,
        ImportParseQueue queue)
    {
        _db = db;
        _extractor = extractor;
        _options = options;
        _queue = queue;
    }

    public async ValueTask<SliceResult<ImportResponse>> Handle(
        ImportDocument message, CancellationToken ct)
    {
        var (bytes, fileName, kind, label, sourceUrl) = message;
        // The filename is a display label and nothing else. It is truncated here
        // and never used to open, create or name anything on disk — the bytes are
        // discarded after extraction, so there is no file operation for a hostile
        // name to reach. That is a real security property of decision 4 (don't
        // keep the file), not just a cost saving.
        var safeName = string.IsNullOrWhiteSpace(fileName)
            ? "upload"
            : Path.GetFileName(fileName.Trim());
        if (safeName.Length > 260) safeName = safeName[..260];

        var extraction = _extractor.Extract(bytes, safeName);
        if (extraction.Status != ResultStatus.Ok)
            return SliceResult<ImportResponse>.Invalid(extraction.Error!);

        var extracted = extraction.Value!;

        // The label defaults to the filename without its extension. The user is
        // expected to change it at review — it is how THEY organise resumes, and
        // the document contains no evidence about it (see ResumeDraft.Label).
        //
        // A label the USER typed is validated, not clipped: silently storing
        // something other than what they wrote is the worse failure, and it is
        // also what CommitImport does with the same value, so the two agree.
        // A label DERIVED from the filename is clipped, because the user never
        // chose it - a 200-character filename is ordinary, resumes.Label is
        // varchar(100), and a default nobody typed should not be the thing that
        // turns confirm into a database error.
        if (label is not null && label.Trim().Length > DraftLimits.MaxLabelLength)
            return SliceResult<ImportResponse>.Invalid(
                $"That label is {label.Trim().Length} characters. Keep it under {DraftLimits.MaxLabelLength}.");

        var resolvedLabel = string.IsNullOrWhiteSpace(label)
            ? Path.GetFileNameWithoutExtension(safeName)
            : label.Trim();
        if (string.IsNullOrWhiteSpace(resolvedLabel)) resolvedLabel = "Imported resume";
        if (resolvedLabel.Length > DraftLimits.MaxLabelLength)
            resolvedLabel = resolvedLabel[..DraftLimits.MaxLabelLength].TrimEnd();

        // -------------------------------------------------------------------
        // Save the extraction BEFORE calling the model
        // -------------------------------------------------------------------
        // This ordering is the point of the two-stage design, and it is worth
        // being explicit about the failure it buys off. Ollama being down, or
        // timing out on a cold model load, is the most likely failure in this
        // whole path. If the model call came first, that failure would throw away
        // a successful PDF parse and make the user upload the file again.
        //
        // Saved first, the text is durable: the import exists, the review screen
        // can show it, and POST /imports/{id}/reparse retries the cheap-to-fail
        // half without touching the file. Phase 4 made the same call in miniature
        // — it saves the analysis row before writing skills, so a skill-write
        // failure does not lose the inference.

        // Whether there is anything worth sending to the model at all. A scan
        // with no text layer is finished the moment it is saved, so it goes
        // straight to AwaitingReview rather than claiming a parse that will
        // never be driven — a Parsing row nobody is going to structure is
        // exactly the invisible-orphan state this status exists to remove.
        var willParse = extracted.Text.Length >= _options.MinTextChars;

        var import = new DocumentImport
        {
            Kind = kind,
            Status = willParse ? ImportStatus.Parsing : ImportStatus.AwaitingReview,
            FileName = safeName,
            Format = extracted.Format,
            ByteCount = bytes.Length,
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ExtractedText = extracted.Text,
            // The empty draft, not "{}" — and it carries the resolved label. Both
            // degraded paths below (no text layer, model unusable) return without
            // ever writing a draft, and storing a placeholder there meant the
            // upload response showed the label the caller asked for while the
            // stored row did not: a later GET or confirm re-derived it from the
            // filename. Seeding the real shape here makes the response and the row
            // agree whatever happens next.
            DraftJson = JsonSerializer.Serialize(
                EmptyDraft(kind, resolvedLabel, sourceUrl), DraftMapper.Json),
            Warning = extracted.Warning
        };

        _db.DocumentImports.Add(import);
        await _db.SaveChangesAsync(ct);

        // -------------------------------------------------------------------
        // And that is the whole upload. Phase 6.5 group 6.
        // -------------------------------------------------------------------
        // The model call used to be the next line, and it was the only slow
        // thing here: everything above is byte work that finishes in
        // milliseconds, while StructureAsync blocks for up to 180 seconds
        // against llama3.2:3b on CPU. So this change is not "make the upload
        // async" — it is "return at the save that already existed", and the
        // reasoning for that early save transfers unchanged.
        //
        // ENQUEUED AFTER SaveChangesAsync, NEVER BEFORE. Same rule as publishing
        // a domain event (SharedKernel/DomainEvents.cs, 13.3c) and same reason:
        // on failure this leaves an invisible orphan rather than acting on a row
        // that does not exist. Here the orphan is self-healing — a row stuck in
        // Parsing is exactly what ImportParseWorker's startup sweep looks for.
        //
        // Not a mediator notification, deliberately. AddMediator is registered
        // ServiceLifetime.Scoped and IPublisher.Publish awaits its handlers
        // inline, so a notification would run the model INSIDE this request and
        // reinstate the block this whole group exists to remove. The channel is
        // the point: it is the boundary the request thread does not cross.
        if (willParse) _queue.Enqueue(import.Id);

        return SliceResult<ImportResponse>.Ok(ToResponse(import, EmptyDraft(kind, resolvedLabel, sourceUrl)));
    }

    // An empty draft of the right shape, so a client rendering the review screen
    // always has the fields to fill in rather than a null it has to special-case.
    // sourceUrl is seeded for the same reason the label is. Since group 6 the
    // upload no longer calls the model, so the URL the user typed has nowhere
    // else to live until they confirm — and /reparse reads it back OUT of the
    // stored draft (RestructureImport.cs). Dropping it here would mean the
    // parse that finishes the upload ran without the URL the upload was given.
    // The three ReadDraft fallbacks below pass nothing, correctly: they are
    // reconstructing a draft that failed to deserialize, and inventing a field
    // for it is not their job.
    internal static ImportDraft EmptyDraft(DocumentKind kind, string label, string? sourceUrl = null) =>
        kind == DocumentKind.Resume
            ? new ImportDraft(new ResumeDraft(label, null, null, null, null, null, [], [], [], []), null)
            : new ImportDraft(null, new PostingDraft("", "", null, null, sourceUrl, [], []));

    internal static ImportResponse ToResponse(DocumentImport import, ImportDraft draft) => new(
        import.Id,
        import.Kind,
        import.Status,
        import.FileName,
        import.Format,
        import.ByteCount,
        import.ContentHash,
        import.ExtractedText,
        draft,
        import.ModelUsed,
        import.Warning,
        import.CreatedAtUtc,
        import.UpdatedAtUtc,
        import.CommittedEntityId);

    // Reads the stored jsonb back into the draft shape. A row whose draft failed
    // to parse degrades to an empty draft rather than failing the read: the point
    // of storing the extracted text separately is that a broken draft is always
    // recoverable, and a review screen that 500s is not recovery.
    internal static ImportDraft ReadDraft(DocumentImport import)
    {
        if (string.IsNullOrWhiteSpace(import.DraftJson) || import.DraftJson == "{}")
            return EmptyDraft(import.Kind, Path.GetFileNameWithoutExtension(import.FileName));

        try
        {
            var draft = JsonSerializer.Deserialize<ImportDraft>(import.DraftJson, DraftMapper.Json);
            return draft is null
                ? EmptyDraft(import.Kind, Path.GetFileNameWithoutExtension(import.FileName))
                // Rows stored before DraftSanitiser existed can hold null lists,
                // so the guard is on the way out as well as on the way in.
                : draft.Sanitise();
        }
        catch (JsonException)
        {
            return EmptyDraft(import.Kind, Path.GetFileNameWithoutExtension(import.FileName));
        }
    }
}
