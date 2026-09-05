using Jobkeep.Contracts.Applications;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Match;

// PHASE 13.3c — what replaces `match_results.ApplicationId ON DELETE CASCADE`.
//
// The rule is unchanged from the day Phase 5 wrote it down: a match result is a
// judgement about one application against one résumé, and it means nothing once
// the application is gone. What changed at 13.3b is only WHO enforces it.
// Postgres cannot: `match_results` is in the `ats` schema and `job_applications` is
// in `applications`, and a foreign key between two schemas is exactly the join
// that stops existing when the boundary becomes a network.
//
// So Applications announces the delete and this deletes the row. It is not a
// slice — no user asks for it, there is no route, and it returns no SliceResult.
// It sits in Application/ anyway because it is a use case of this module, just
// one whose trigger is another module's event rather than an HTTP request. The
// naming convention this establishes is On<Event>.cs.
//
// ---------------------------------------------------------------------------
// ExecuteDelete, and why this one may
// ---------------------------------------------------------------------------
// DeleteApplication.cs deliberately loads the row before removing it, so EF
// applies configured cascades rather than leaving them to the database. Nothing
// cascades from `match_results` — it is a leaf, and since 13.3b it is the only
// table in its schema — so there is nothing for EF to apply and one statement
// with no round trip is the honest shape.
//
// It is also idempotent, which the publisher's comment relies on: deleting zero
// rows is a success. That matters because a retry of a half-delivered event, or
// a second delete of an application already gone, must not turn into an error
// the caller cannot act on.
//
// PHASE 11.2b — ExecuteDelete WALKS PAST THE OWNER FILTER, and that is left
// alone deliberately. Both events are unpublished since Phase 8 (archiving must
// not destroy a derived record about a row that survived), so nothing reaches
// this handler through the app today. Their real caller is the F18 purge, which
// runs as the system with no principal — an owner predicate would make it delete
// nothing at all. The id it is given already came from the owner's own delete.
//
// ponytail: unscoped by design while the only caller is a purge. If an event
// ever carries an id from anywhere but the owner's own request, this needs the
// owner in the predicate.
public class OnApplicationDeleted : INotificationHandler<ApplicationDeleted>
{
    private readonly MatchDbContext _db;

    public OnApplicationDeleted(MatchDbContext db) => _db = db;

    public async ValueTask Handle(ApplicationDeleted notification, CancellationToken ct)
        => await _db.MatchResults
            .Where(r => r.ApplicationId == notification.ApplicationId)
            .ExecuteDeleteAsync(ct);
}
