using Jobkeep.Contracts.Applications;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Ai;

// PHASE 13.3c — what replaces `ai_analyses.PostingId ON DELETE CASCADE`.
//
// The mirror of Match' OnApplicationDeleted, and the same argument: an analysis is
// a description of one job ad, so it means nothing once the ad is gone, and since
// 13.3b `ai_analyses` and `job_postings` are in two schemas that a foreign key
// may no longer span.
//
// Worth naming, because it is the interesting half of this pair: until 13.3c
// NOTHING IN THE APPLICATION DELETED A POSTING. The cascade had been unreachable
// for the whole life of the table — postings are created implicitly by
// CreateApplication and were never removable — so the orphan this replaces was a
// defect only a test could produce. DeletePosting.cs is the route that makes it
// real, and it landed in this step precisely so the replacement has a publisher
// rather than being a handler nobody can trigger.
//
// ExecuteDelete for the same two reasons as the Match handler: `ai_analyses` is a
// leaf with nothing to cascade, and deleting zero rows must be a success so a
// redelivered event is harmless.
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
public class OnPostingDeleted : INotificationHandler<PostingDeleted>
{
    private readonly AiDbContext _db;

    public OnPostingDeleted(AiDbContext db) => _db = db;

    public async ValueTask Handle(PostingDeleted notification, CancellationToken ct)
        => await _db.AiAnalyses
            .Where(a => a.PostingId == notification.PostingId)
            .ExecuteDeleteAsync(ct);
}
