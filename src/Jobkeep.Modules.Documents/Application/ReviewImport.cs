using System.Text.Json;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: the user corrects the draft.
//
// This is the "fix on the user's terms" half of the review cycle, and it is the
// reason the whole pipeline stops at a draft instead of writing rows. A small
// model reading a two-column PDF will get an employer wrong, merge two jobs, or
// miss a skill; the design answer is not a better model, it is a screen where the
// person who knows the answer types it.
//
// A full replace (PUT), not a patch. The client on this screen is holding the
// entire draft already — it just rendered it — so sending back the whole thing is
// simpler on both sides and has no merge semantics to get wrong. The draft is a
// document, and PATCH-ing a document with nested arrays means inventing a
// path syntax for "the third experience entry's second bullet".
public record ReviewImport(Guid Id, ImportDraft Draft) : IRequest<SliceResult<ImportResponse>>;

public class ReviewImportHandler : IRequestHandler<ReviewImport, SliceResult<ImportResponse>>
{
    private readonly DocumentsDbContext _db;

    public ReviewImportHandler(DocumentsDbContext db) => _db = db;

    public async ValueTask<SliceResult<ImportResponse>> Handle(
        ReviewImport message, CancellationToken ct)
    {
        var (id, draft) = message;
        var import = await _db.DocumentImports.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (import is null)
            return SliceResult<ImportResponse>.NotFound($"Import {id} not found.");

        // Committed is terminal. Editing the draft of an import that has already
        // become a resume would be editing a receipt: the rows exist and this
        // column no longer describes them. The resume itself is the thing to
        // edit at that point.
        if (import.Status != ImportStatus.AwaitingReview)
            return SliceResult<ImportResponse>.Invalid(
                $"This import is {import.Status.ToString().ToLowerInvariant()} and can no longer be edited.");

        // The half that matches the import's kind is the only one kept. A client
        // sending a posting draft to a resume import is confused about which
        // import it is holding, and silently storing it would produce a row that
        // commits into nothing.
        var normalised = import.Kind switch
        {
            DocumentKind.Resume when draft.Resume is not null => new ImportDraft(draft.Resume, null),
            DocumentKind.JobPosting when draft.Posting is not null => new ImportDraft(null, draft.Posting),
            _ => null
        };

        if (normalised is null)
            return SliceResult<ImportResponse>.Invalid(
                $"This is a {import.Kind} import, so the draft must carry its "
                + $"{(import.Kind == DocumentKind.Resume ? "resume" : "posting")} half.");

        // Null lists in the body become empty ones before anything stores them —
        // see DraftSanitiser for why a deserializer can produce them at all.
        normalised = normalised.Sanitise();

        import.DraftJson = JsonSerializer.Serialize(normalised, DraftMapper.Json);

        // ModelUsed is cleared once a human has edited the draft. It records what
        // produced the content, and after an edit the answer is "partly a person",
        // which the column cannot express — so it stops claiming the model's
        // output is what is stored. The alternative, leaving a stale model tag on
        // hand-written content, is the kind of quiet inaccuracy that makes a
        // provenance column worse than not having one.
        import.ModelUsed = null;
        import.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return SliceResult<ImportResponse>.Ok(ImportDocumentHandler.ToResponse(import, normalised));
    }
}
