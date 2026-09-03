using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: the review queue — what have I uploaded, and what is still waiting on me.
//
// Deliberately NOT paged, unlike ListApplications. The review queue is things you
// have not finished; if it is long enough to need paging, the feature has failed
// at something paging will not fix. A cap is applied instead, so an unbounded
// query can never be issued.

// A summary WITHOUT the extracted text or the draft. A resume is personal
// information and a list endpoint is the wrong place to spray it — the security
// audit's finding against the old ResumeText column was exactly this shape, and
// the fix there (Phase 2.3, drop Description and ResumeText from the list
// projection) is the precedent being followed rather than re-learned.
public record ImportSummary(
    Guid Id,
    DocumentKind Kind,
    ImportStatus Status,
    string FileName,
    SourceFormat Format,
    long ByteCount,
    int TextLength,
    string? Warning,
    DateTime CreatedAtUtc,
    Guid? CommittedEntityId);

public record ListImports(ImportStatus? Status) : IRequest<SliceResult<List<ImportSummary>>>;

public class ListImportsHandler : IRequestHandler<ListImports, SliceResult<List<ImportSummary>>>
{
    private readonly DocumentsDbContext _db;
    private readonly DocumentOptions _options;

    public ListImportsHandler(DocumentsDbContext db, DocumentOptions options)
    {
        _db = db;
        _options = options;
    }

    public async ValueTask<SliceResult<List<ImportSummary>>> Handle(
        ListImports message, CancellationToken ct)
    {
        var status = message.Status;
        var query = _db.DocumentImports.AsNoTracking();

        // The default view is the queue: what still needs confirming. Passing an
        // explicit status widens it; there is no "everything" option because the
        // committed rows are receipts and the interesting view of them is the row
        // they created, not the import.
        query = query.Where(d => d.Status == (status ?? ImportStatus.AwaitingReview));

        var items = await query
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(_options.MaxListSize)
            // Flat projection, not the entity: ExtractedText and DraftJson are
            // the two biggest columns in the table and neither is in this shape,
            // so they are never read off disk for this query. TextLength is
            // computed in SQL (length()) rather than by materialising the text.
            .Select(d => new ImportSummary(
                d.Id,
                d.Kind,
                d.Status,
                d.FileName,
                d.Format,
                d.ByteCount,
                d.ExtractedText.Length,
                d.Warning,
                d.CreatedAtUtc,
                d.CommittedEntityId))
            .ToListAsync(ct);

        return SliceResult<List<ImportSummary>>.Ok(items);
    }
}
