using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: read one import back, including its extracted text and current draft.
//
// This is the review screen's GET. It returns the full extracted text, which
// every other read path in this codebase would call an over-fetch — and it is
// the exception that proves the rule: the user's job on this screen is to decide
// whether the draft matches the document, and they cannot do that without the
// document in front of them.
public record GetImport(Guid Id) : IRequest<SliceResult<ImportResponse>>;

public class GetImportHandler : IRequestHandler<GetImport, SliceResult<ImportResponse>>
{
    private readonly DocumentsDbContext _db;

    public GetImportHandler(DocumentsDbContext db) => _db = db;

    public async ValueTask<SliceResult<ImportResponse>> Handle(
        GetImport message, CancellationToken ct)
    {
        var id = message.Id;
        // Tracking is off: this is a pure read, and the draft is deserialized
        // from a column rather than mutated.
        var import = await _db.DocumentImports
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (import is null)
            return SliceResult<ImportResponse>.NotFound($"Import {id} not found.");

        return SliceResult<ImportResponse>.Ok(
            ImportDocumentHandler.ToResponse(import, ImportDocumentHandler.ReadDraft(import)));
    }
}
