using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: delete a job posting — the ad itself, not your record of applying to it.
//
// ---------------------------------------------------------------------------
// Why this route did not exist until 13.3c
// ---------------------------------------------------------------------------
// Postings are created implicitly: you log an application, and CompanyLookup and
// CreateApplication bring the company and the ad into existence behind it. So
// until now there was no way to remove one, and the consequence was visible —
// deleting your last application for an ad left the ad behind forever, with its
// skills, its requirements and its AI analysis, reachable by nothing.
//
// 13.3c is where that stopped being tolerable, and the reason is worth recording
// because it is the phase in miniature: `ai_analyses.PostingId` was a CASCADE
// that 13.3b dropped, and its replacement is a delete notification. A
// notification needs a publisher. Writing OnPostingDeleted without this route
// would have meant shipping a handler nothing could trigger and a test that
// proved it by reaching into the database — which is how a replacement gets
// believed rather than verified.
//
// ---------------------------------------------------------------------------
// The refusal, and why it is here rather than left to Postgres
// ---------------------------------------------------------------------------
// `job_applications.PostingId` is RESTRICT and both tables are in this module's
// schema, so the database WOULD refuse this. It would do it by throwing
// DbUpdateException out of SaveChanges, which the API turns into a 500 — an
// unhandled server error for a request the user made correctly. Checking first
// turns that into a 400 with a sentence naming the number of applications in the
// way.
//
// The check is not what protects the invariant. The foreign key still is: a
// concurrent insert between the count and the delete would be refused by
// Postgres, in the same transaction, exactly as before. That is the difference
// between this refusal and the ones in DeleteResume.cs, where the key is gone and
// the check is all there is.
public class DeletePostingHandler
{
    private readonly ApplicationsDbContext _db;
    private readonly IDomainEventPublisher _events;

    public DeletePostingHandler(ApplicationsDbContext db, IDomainEventPublisher events)
    {
        _db = db;
        _events = events;
    }

    public async Task<SliceResult<bool>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var posting = await _db.JobPostings.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (posting is null)
            return SliceResult<bool>.NotFound($"Posting {id} not found.");

        var applications = await _db.JobApplications.CountAsync(a => a.PostingId == id, ct);
        if (applications > 0)
            return SliceResult<bool>.Invalid(
                $"This job ad still has {applications} application(s) logged against it. "
                + "Delete those first — the ad is deliberately kept while any of them exist.");

        // Loaded and removed rather than ExecuteDelete, so EF applies the two
        // cascades this table does have: posting_skills and job_requirements.
        // Both are Applications' own tables in Applications' own schema, which is
        // exactly why they still cascade and ai_analyses does not.
        //
        // The company is NOT touched. companies.Id is RESTRICT from here, and a
        // company with no postings left is a fact about a company, not a dangling
        // row — the same reasoning that keeps a shared skill alive after its last
        // link goes.
        _db.JobPostings.Remove(posting);
        await _db.SaveChangesAsync(ct);

        // PHASE 13.3c — the replacement for ai_analyses' dropped CASCADE, after
        // the save for the reason DeleteApplication.cs argues at length: the
        // orphan nobody can see beats the destroyed row somebody can.
        await _events.PublishAsync(new PostingDeleted(id), ct);

        return SliceResult<bool>.Ok(true);
    }
}
