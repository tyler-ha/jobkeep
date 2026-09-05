using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: bring an archived résumé version back. PHASE 8.
//
// ---------------------------------------------------------------------------
// The one restore that can be REFUSED, and why that is the right trade
// ---------------------------------------------------------------------------
// `resumes.LabelNormalized` carries a unique index, and Phase 8 made it FILTERED
// to live rows (see ResumeConfiguration). That is not an optimisation — without
// it, archiving "backend" would hold the name hostage forever and the next
// import under that label would be refused by a constraint naming a document the
// user cannot see. Freeing the label is the correct behaviour.
//
// The price lands here. Between the archive and the restore, another résumé can
// legitimately take the label, and then the restore would put two live rows on
// one name. The index would refuse it — from inside SaveChanges, as a
// DbUpdateException, which the API turns into a 500 for a request the user made
// correctly. So it is asked first and answered as a 400 naming the conflict.
//
// The check is NOT what protects the invariant; the unique index still is, and it
// would still refuse a row that slipped in between the check and the commit. Same
// division of labour as DeletePostingHandler's count — the check buys a sentence,
// the constraint buys the guarantee. That is the shape to reach for whenever both
// are available; DeleteResume's two counts are the weaker case, where the
// constraint is gone and the check is alone.
//
// The alternative considered and refused: restore under a suffixed label
// ("backend (2)"), the way CommitImport de-duplicates on import. Refused because
// the user did not ask for a new document, they asked for THIS one back, and
// silently renaming it makes the restore something other than a restore. Phase 4.5
// suffixes because two imports genuinely are two documents; there is only one
// document here, and the honest answer is to say the name is taken.
public record RestoreResume(Guid Id) : IRequest<SliceResult<bool>>;

public class RestoreResumeHandler : IRequestHandler<RestoreResume, SliceResult<bool>>
{
    private readonly DocumentsDbContext _db;

    public RestoreResumeHandler(DocumentsDbContext db) => _db = db;

    public async ValueTask<SliceResult<bool>> Handle(
        RestoreResume message, CancellationToken ct)
    {
        var id = message.Id;

        var resume = await _db.Resumes
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
            .FirstOrDefaultAsync(r => r.Id == id && r.IsDeleted, ct);

        if (resume is null)
            return SliceResult<bool>.NotFound($"No archived resume {id} found.");

        // Compared on LabelNormalized rather than on Label, and NOT through
        // NaturalKey.Of — that helper is for skill names and is called in exactly
        // one file (13.2c). This column is computed by Postgres, so comparing two
        // of them is comparing the database's own answer to its own question,
        // which is the only comparison guaranteed to agree with the index that is
        // about to be consulted.
        //
        // No IgnoreQueryFilters on this one, deliberately: the index is filtered
        // to live rows, so live rows are exactly what conflicts, and the default
        // filter already says that.
        var taken = await _db.Resumes
            .AnyAsync(r => r.LabelNormalized == resume.LabelNormalized, ct);

        if (taken)
            return SliceResult<bool>.Invalid(
                $"Another résumé is already called '{resume.Label}'. "
                + "Rename that one first, or this restore would leave two documents "
                + "sharing a label.");

        resume.IsDeleted = false;
        resume.DeletedAtUtc = null;
        await _db.SaveChangesAsync(ct);

        return SliceResult<bool>.Ok(true);
    }
}
