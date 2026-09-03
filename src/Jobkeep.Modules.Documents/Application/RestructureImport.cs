using System.Text.Json;
using Jobkeep.Models;
using Jobkeep.Shared;
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

        if (import.Status != ImportStatus.AwaitingReview)
            return SliceResult<ImportResponse>.Invalid(
                $"This import is {import.Status.ToString().ToLowerInvariant()} and can no longer be re-parsed.");

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
            // Unlike the upload path, this one reports the failure as Invalid
            // rather than swallowing it into a warning. There, the caller had
            // just uploaded a file and needed the import id back whatever the
            // model did. Here the import already exists and the *only* thing
            // asked for was a re-parse, so a failure is the answer to the
            // question, and the previous draft is deliberately left untouched.
            return SliceResult<ImportResponse>.Invalid(structured.Error!);

        import.DraftJson = JsonSerializer.Serialize(structured.Value!.Draft, DraftMapper.Json);
        import.ModelUsed = _model.Model;
        import.Warning = structured.Value.Warning;   // replaced, not appended: a fresh run's warnings
        import.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return SliceResult<ImportResponse>.Ok(
            ImportDocumentHandler.ToResponse(import, structured.Value.Draft));
    }
}
