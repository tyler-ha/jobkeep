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

// Slice: run the model over the stored text again, replacing the draft.
//
// This slice is the two-stage design's dividend, and the reason the extracted
// text is a column rather than a local variable. Three things it makes cheap
// that would otherwise mean re-uploading the file:
//
//   * The model was down when the document was uploaded. The extraction is
//     already saved; this retries only the half that failed.
//   * The prompt or the schema improved. Every past import can be re-run against
//     the better version without the user finding the original PDF again.
//   * A bigger model was pulled. Same, and ModelUsed then records which rows
//     came from which.
//
// It is destructive of hand edits, which is why it is a separate explicit action
// rather than something the review screen does on load — see below.
public record RestructureImport(Guid Id) : IRequest<SliceResult<ImportResponse>>;

public class RestructureImportHandler : IRequestHandler<RestructureImport, SliceResult<ImportResponse>>
{
    private readonly DocumentsDbContext _db;
    private readonly IDocumentStructurer _structurer;
    private readonly ModelOptions _model;
    private readonly DocumentOptions _options;

    public RestructureImportHandler(
        DocumentsDbContext db, IDocumentStructurer structurer, ModelOptions model, DocumentOptions options)
    {
        _db = db;
        _structurer = structurer;
        _model = model;
        _options = options;
    }

    public async ValueTask<SliceResult<ImportResponse>> Handle(
        RestructureImport message, CancellationToken ct)
    {
        var id = message.Id;
        var import = await _db.DocumentImports.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (import is null)
            return SliceResult<ImportResponse>.NotFound($"Import {id} not found.");

        // Two callers now, and the difference between them decides what a model
        // failure means below. Phase 6.5 group 6 made this endpoint the second
        // half of the upload, so it runs either as:
        //
        //   Parsing        — finishing an upload that deliberately returned
        //                    before the model. The user has not seen a draft yet.
        //   AwaitingReview — a human pressed "Read it again" on a draft that
        //                    already exists.
        if (import.Status is not (ImportStatus.AwaitingReview or ImportStatus.Parsing))
            return SliceResult<ImportResponse>.Invalid(
                $"This import is {import.Status.ToString().ToLowerInvariant()} and can no longer be re-parsed.");

        var finishingUpload = import.Status == ImportStatus.Parsing;

        if (import.ExtractedText.Length < _options.MinTextChars)
            return SliceResult<ImportResponse>.Invalid(
                "There is no extracted text to re-parse. Upload a text-based document instead.");

        // The label survives a re-parse. It is the user's decision about how they
        // organise resumes, the model never proposed it, and silently resetting
        // it to the filename would be the re-parse quietly undoing an edit that
        // had nothing to do with the model.
        var existing = ImportDocumentHandler.ReadDraft(import);
        var label = existing.Resume?.Label
                    ?? Path.GetFileNameWithoutExtension(import.FileName);

        var sourceUrl = existing.Posting?.SourceUrl;

        var structured = await _structurer.StructureAsync(
            import.Kind, import.ExtractedText, label, sourceUrl, ct);

        if (structured.Status != ResultStatus.Ok)
        {
            // A re-parse a human asked for reports the failure as Invalid rather
            // than swallowing it into a warning: the import already exists and
            // the *only* thing asked for was a re-parse, so a failure is the
            // answer to the question, and the previous draft is left untouched.
            if (!finishingUpload)
                return SliceResult<ImportResponse>.Invalid(structured.Error!);

            // Finishing an upload is the opposite case, and it is the behaviour
            // the upload slice used to have inline: the caller has a real import
            // and no draft, so the row must not be left claiming a parse that has
            // stopped. It lands in AwaitingReview with the extraction intact and
            // an explanation attached, which is exactly what POST /imports
            // returned before group 6 moved this call out of it. The user can
            // press "Read it again", or hand-fill the draft.
            import.Status = ImportStatus.AwaitingReview;
            import.Warning = Join(import.Warning, structured.Error);
            import.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return SliceResult<ImportResponse>.Ok(
                ImportDocumentHandler.ToResponse(import, ImportDocumentHandler.ReadDraft(import)));
        }

        // The one line that closes the Parsing state. On the re-parse path the
        // row is already AwaitingReview and this changes nothing.
        import.Status = ImportStatus.AwaitingReview;
        import.DraftJson = JsonSerializer.Serialize(structured.Value!.Draft, DraftMapper.Json);
        import.ModelUsed = _model.Model;
        // Replaced on a re-parse — a fresh run's warnings are the current truth.
        // Joined when finishing an upload, because the extraction's own warning
        // (a partial text layer, say) is about the document, not about this run,
        // and the upload slice used to join it for that reason.
        import.Warning = finishingUpload
            ? Join(import.Warning, structured.Value.Warning)
            : structured.Value.Warning;
        import.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return SliceResult<ImportResponse>.Ok(
            ImportDocumentHandler.ToResponse(import, structured.Value.Draft));
    }

    // Moved here from ImportDocument with the model call it belonged to.
    private static string? Join(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (null, var s) => s,
            (var f, null) => f,
            var (f, s) => $"{f} {s}"
        };
}
