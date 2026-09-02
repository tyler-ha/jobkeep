using Jobkeep.Modules.Applications;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Ai;

// PHASE 13.3c — what replaces `ai_analyses.PostingId ON DELETE CASCADE`.
//
// The mirror of Ats' OnApplicationDeleted, and the same argument: an analysis is
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
// ExecuteDelete for the same two reasons as the Ats handler: `ai_analyses` is a
// leaf with nothing to cascade, and deleting zero rows must be a success so a
// redelivered event is harmless.
public class OnPostingDeleted : IDomainEventHandler<PostingDeleted>
{
    private readonly AiDbContext _db;

    public OnPostingDeleted(AiDbContext db) => _db = db;

    public async Task HandleAsync(PostingDeleted domainEvent, CancellationToken ct = default)
        => await _db.AiAnalyses
            .Where(a => a.PostingId == domainEvent.PostingId)
            .ExecuteDeleteAsync(ct);
}
