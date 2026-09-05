using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: bring an archived application back. PHASE 8 — the new use case, and the
// only place in the API where the user meets the word.
//
// ---------------------------------------------------------------------------
// IgnoreQueryFilters is the whole slice
// ---------------------------------------------------------------------------
// Every other read in this module is written as if archived rows do not exist,
// because the global filter makes that true. This one has to see past it, and
// saying so explicitly — once, here — is the escape hatch working as designed.
// A filter you can never turn off is a filter that eventually gets removed.
//
// ---------------------------------------------------------------------------
// Why restoring a live row is a 404 and not a no-op
// ---------------------------------------------------------------------------
// It is the same answer DeleteApplication gives to deleting an already-archived
// one, from the other side. "Restore" addresses a row in the archive; a live row
// is not in the archive, so the thing being addressed is not there. Answering 200
// would mean a client that restored the wrong id gets the same response as one
// that restored the right one — and there is no second signal to tell them apart,
// which is precisely the argument 13.3c made when it stopped returning `false`
// from the GraphQL delete.
//
// ---------------------------------------------------------------------------
// What comes back with it
// ---------------------------------------------------------------------------
// Everything, and none of it needs code here. The posting, its skills, its
// requirements, the résumé link, the AI analysis and the stored match check were
// all untouched by the archive — no cascade fired, and no notification was
// published. Clearing two columns is genuinely the whole operation.
//
// The ONE thing that can still be missing is the posting: archiving an ad is
// refused while a live application names it, but an application archived FIRST
// and its ad archived SECOND is a legal sequence, and restoring the application
// then leaves it pointing at an ad the query filter still hides. That is not
// repaired automatically, deliberately — silently un-archiving a second entity
// the user did not name is the kind of helpfulness that makes a restore
// unpredictable. Restore the ad too; `POST /postings/{id}/restore` exists.
public record RestoreApplication(Guid Id) : IRequest<SliceResult<bool>>;

public class RestoreApplicationHandler : IRequestHandler<RestoreApplication, SliceResult<bool>>
{
    private readonly ApplicationsDbContext _db;

    public RestoreApplicationHandler(ApplicationsDbContext db) => _db = db;

    public async ValueTask<SliceResult<bool>> Handle(
        RestoreApplication message, CancellationToken ct)
    {
        var id = message.Id;

        var application = await _db.JobApplications
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
            .FirstOrDefaultAsync(a => a.Id == id && a.IsDeleted, ct);

        if (application is null)
            return SliceResult<bool>.NotFound($"No archived application {id} found.");

        application.IsDeleted = false;
        application.DeletedAtUtc = null;

        // Ordinary Modified entry, so AuditSaveChangesInterceptor stamps
        // UpdatedAtUtc exactly as it does for an edit — which is correct: a
        // restore IS a change to this row, and the archive date it clears is the
        // only record that says otherwise.
        await _db.SaveChangesAsync(ct);

        return SliceResult<bool>.Ok(true);
    }
}
