using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Skills;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: delete an application — which since PHASE 8 means ARCHIVE it.
//
// ---------------------------------------------------------------------------
// Why the name, the route and the mutation all still say "delete"
// ---------------------------------------------------------------------------
// Because from the API's side nothing changed. The row stops being returned by
// every read, a second DELETE answers 404, and a caller cannot tell the
// difference. Soft delete is a STORAGE decision — it buys a restore and it
// protects the analytics history — and renaming three types, two routes and two
// mutations to advertise it would leave the HTTP verb and the handler disagreeing
// about what the operation is called. `POST /applications/{id}/restore` is the
// new verb, and it is the only place the user meets the word.
//
// What actually changed here is one line: the notification below is no longer
// published. See the block above it, which is the interesting half of the phase.
//
// What survives the archive, which is now EVERYTHING, and the list is kept
// because the reasons have not all collapsed into the same one:
//   - the match_results row now SURVIVES. Until Phase 8 it went, by notification
//     rather than by cascade; see the block in the handler for why archiving
//     revokes that.
//   - the job_posting is left in place, as it always was. The relationship is
//     Restrict in the other direction and several applications can share one
//     posting, so the ad outlives your record of applying to it. DeletePosting.cs
//     archives the ad itself, once nothing live is applying with it.
//   - the company and any shared skills survive for the reason they always did:
//     they are shared rows, and another application still names them.
public record DeleteApplication(Guid Id) : IRequest<SliceResult<bool>>;

public class DeleteApplicationHandler : IRequestHandler<DeleteApplication, SliceResult<bool>>
{
    private readonly ApplicationsDbContext _db;

    // IPublisher is gone from this constructor as of Phase 8. It was here for one
    // call, and that call is the one the phase removed.
    public DeleteApplicationHandler(ApplicationsDbContext db) => _db = db;

    public async ValueTask<SliceResult<bool>> Handle(
        DeleteApplication message, CancellationToken ct)
    {
        var id = message.Id;
        // Loads the row rather than issuing ExecuteDelete, and since Phase 8 that
        // is no longer a preference — it is required. ExecuteDelete bypasses the
        // change tracker, so AuditSaveChangesInterceptor would never see the
        // entry and the row would be genuinely destroyed. Tracked-load-then-Remove
        // is what makes Remove() mean archive.
        //
        // The query filter also does real work here: an already-archived row is
        // invisible to this read, so a second DELETE answers 404 rather than
        // re-stamping DeletedAtUtc and quietly moving the archive date.
        var application = await _db.JobApplications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (application is null)
            return SliceResult<bool>.NotFound($"Application {id} not found.");

        _db.JobApplications.Remove(application);
        await _db.SaveChangesAsync(ct);

        // ---------------------------------------------------------------------
        // PHASE 8 — THE NOTIFICATION IS NO LONGER PUBLISHED, and that is a
        // decision this file's own earlier argument forces.
        // ---------------------------------------------------------------------
        // 13.3c published ApplicationDeleted here so that Match could delete the
        // match_results row whose CASCADE 13.3b had dropped. It weighed two
        // failure modes — "an invisible orphan" against "destroyed work on a row
        // that survived" — and chose the orphan, because re-earning a match
        // result costs a model call the user waits minutes for.
        //
        // Soft delete moves the row from one side of that weighing to the other.
        // The application is not gone; it is hidden, and a restore is one click.
        // Publishing would therefore destroy a stored judgement about a row that
        // still exists and is about to come back — which is exactly the outcome
        // the 13.3c comment refused. So archiving announces nothing.
        //
        // The consequence, stated so nobody re-derives it: `match_results` and
        // `ai_analyses` rows now outlive every archive, and nothing in the
        // application deletes them any more. They are 1:1 with a row that still
        // exists, so they are not orphans; they are simply retained. The reads
        // reach them through an application the query filter hides, so an
        // archived application's stale check is invisible until it is restored,
        // at which point it is the check the user last ran — which is what they
        // would expect.
        //
        // ponytail: ApplicationDeleted, PostingDeleted and their two subscribers
        // are now unreachable through the app. They are kept rather than deleted
        // because a PURGE — hard-deleting archived rows, audit finding F18, and
        // explicitly out of this phase's scope — is the caller they were written
        // for, and it is a named backlog item rather than a hypothetical. If
        // purge is ever refused outright, delete SharedKernel/DomainEvents.cs's
        // two application events and both handlers with it.

        return SliceResult<bool>.Ok(true);
    }
}
