using Jobkeep.Contracts.Applications;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Ats;

// PHASE 13.3c — what replaces `ats_results.ApplicationId ON DELETE CASCADE`.
//
// The rule is unchanged from the day Phase 5 wrote it down: an ATS result is a
// judgement about one application against one résumé, and it means nothing once
// the application is gone. What changed at 13.3b is only WHO enforces it.
// Postgres cannot: `ats_results` is in the `ats` schema and `job_applications` is
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
// cascades from `ats_results` — it is a leaf, and since 13.3b it is the only
// table in its schema — so there is nothing for EF to apply and one statement
// with no round trip is the honest shape.
//
// It is also idempotent, which the publisher's comment relies on: deleting zero
// rows is a success. That matters because a retry of a half-delivered event, or
// a second delete of an application already gone, must not turn into an error
// the caller cannot act on.
public class OnApplicationDeleted : INotificationHandler<ApplicationDeleted>
{
    private readonly AtsDbContext _db;

    public OnApplicationDeleted(AtsDbContext db) => _db = db;

    public async ValueTask Handle(ApplicationDeleted notification, CancellationToken ct)
        => await _db.AtsResults
            .Where(r => r.ApplicationId == notification.ApplicationId)
            .ExecuteDeleteAsync(ct);
}
