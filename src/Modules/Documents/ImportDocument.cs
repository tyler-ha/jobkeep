using System.Security.Cryptography;
using System.Text.Json;
using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
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

public class ImportDocumentHandler
{
    private readonly AppDbContext _db;
    private readonly IDocumentTextExtractor _extractor;
    private readonly IDocumentStructurer _structurer;
    private readonly ModelOptions _model;
    private readonly DocumentOptions _options;

    public ImportDocumentHandler(
        AppDbContext db,
        IDocumentTextExtractor extractor,
        IDocumentStructurer structurer,
        ModelOptions model,
        DocumentOptions options)
    {
        _db = db;
        _extractor = extractor;
        _structurer = structurer;
        _model = model;
        _options = options;
    }

    public async Task<SliceResult<ImportResponse>> HandleAsync(
        byte[] bytes,
        string fileName,
        DocumentKind kind,
        string? label,
        string? sourceUrl,
        CancellationToken ct = default)
    {
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
        var resolvedLabel = string.IsNullOrWhiteSpace(label)
            ? Path.GetFileNameWithoutExtension(safeName)
            : label.Trim();
        if (string.IsNullOrWhiteSpace(resolvedLabel)) resolvedLabel = "Imported resume";

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
        var import = new DocumentImport
        {
            Kind = kind,
            Status = ImportStatus.AwaitingReview,
            FileName = safeName,
            Format = extracted.Format,
            ByteCount = bytes.Length,
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ExtractedText = extracted.Text,
            DraftJson = "{}",
            Warning = extracted.Warning
        };

        _db.DocumentImports.Add(import);
        await _db.SaveChangesAsync(ct);

        // A scan with no text layer stops here rather than being sent to the
        // model. Asking a model to structure an empty string does not fail — it
        // invents a plausible resume, which is the single worst outcome this
        // feature could produce. The import is kept so the user can see what was
        // extracted (nothing) and why.
        if (extracted.Text.Length < _options.MinTextChars)
            return SliceResult<ImportResponse>.Ok(ToResponse(import, EmptyDraft(kind, resolvedLabel)));

        var structured = await _structurer.StructureAsync(
            kind, extracted.Text, resolvedLabel, sourceUrl, ct);

        if (structured.Status != ResultStatus.Ok)
        {
            // The model answered with something unusable. The row survives with
            // the extraction intact and an explanation attached, so /reparse can
            // try again and the user can still hand-fill the draft in the
            // meantime. Returning Ok here rather than Invalid is deliberate: the
            // upload succeeded, and the caller now owns a real import id.
            import.Warning = Join(import.Warning, structured.Error);
            import.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return SliceResult<ImportResponse>.Ok(ToResponse(import, EmptyDraft(kind, resolvedLabel)));
        }

        import.DraftJson = JsonSerializer.Serialize(structured.Value!.Draft, DraftMapper.Json);
        import.ModelUsed = _model.Model;
        import.Warning = Join(import.Warning, structured.Value.Warning);
        import.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return SliceResult<ImportResponse>.Ok(ToResponse(import, structured.Value.Draft));
    }

    private static string? Join(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (null, var s) => s,
            (var f, null) => f,
            var (f, s) => $"{f} {s}"
        };

    // An empty draft of the right shape, so a client rendering the review screen
    // always has the fields to fill in rather than a null it has to special-case.
    internal static ImportDraft EmptyDraft(DocumentKind kind, string label) =>
        kind == DocumentKind.Resume
            ? new ImportDraft(new ResumeDraft(label, null, null, null, null, null, [], [], []), null)
            : new ImportDraft(null, new PostingDraft("", "", null, null, null, [], []));

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
            return JsonSerializer.Deserialize<ImportDraft>(import.DraftJson, DraftMapper.Json)
                   ?? EmptyDraft(import.Kind, import.FileName);
        }
        catch (JsonException)
        {
            return EmptyDraft(import.Kind, import.FileName);
        }
    }
}
