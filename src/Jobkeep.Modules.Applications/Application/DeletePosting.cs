using Jobkeep.Contracts.Applications;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: delete a job posting — the ad itself, not your record of applying to it.
// Since PHASE 8 this ARCHIVES rather than destroys; DeleteApplication.cs argues
// why the name, the route and the mutation all still say "delete".
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
// PHASE 8 CHANGED WHAT THIS CHECK IS FOR, and it is now the only thing doing the
// job. The database no longer refuses anything here: the DELETE never runs, so
// `job_applications.PostingId`'s RESTRICT is never consulted. The count below is
// the whole protection, which puts it in the same position as DeleteResume's two
// — with the difference that the residue it prevents is benign, because an
// application whose ad is archived still finds that ad through a restore.
//
// It also counts LIVE applications only, for free, because the query filter is
// on job_applications too. Archiving your last application for an ad is
// therefore what makes the ad archivable — which is the behaviour a user would
// guess, and it is a property of the filter rather than of a rule written here.
//
// The original reasoning, kept because it is what the check was built for:
// `job_applications.PostingId` is RESTRICT and both tables are in this module's
// schema, so the database WOULD refuse this. It would do it by throwing
// DbUpdateException out of SaveChanges, which the API turns into a 500 — an
// unhandled server error for a request the user made correctly. Checking first
// turns that into a 400 with a sentence naming the number of applications in the
// way.
//
// That paragraph is now history. Under soft delete the foreign key is not
// consulted, so the concurrent-insert window it used to close is open here too.
public record DeletePosting(Guid Id) : IRequest<SliceResult<bool>>;

public class DeletePostingHandler : IRequestHandler<DeletePosting, SliceResult<bool>>
{
    private readonly ApplicationsDbContext _db;

    public DeletePostingHandler(ApplicationsDbContext db) => _db = db;

    public async ValueTask<SliceResult<bool>> Handle(
        DeletePosting message, CancellationToken ct)
    {
        var id = message.Id;
        var posting = await _db.JobPostings.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (posting is null)
            return SliceResult<bool>.NotFound($"Posting {id} not found.");

        var applications = await _db.JobApplications.CountAsync(a => a.PostingId == id, ct);
        if (applications > 0)
            return SliceResult<bool>.Invalid(
                $"This job ad still has {applications} application(s) logged against it. "
                + "Archive those first — the ad is deliberately kept while any of them exist.");

        // Loaded and removed rather than ExecuteDelete, and Phase 8 inverted why.
        // It used to be so that EF would apply the two cascades this table has —
        // posting_skills and job_requirements. Now it is so that the interceptor
        // can convert the delete to an archive, and the happy consequence is that
        // those two cascades DO NOT FIRE: the ad keeps its skills and its
        // requirements, which is the difference between a restore and a re-import.
        //
        // The company is NOT touched. companies.Id is RESTRICT from here, and a
        // company with no postings left is a fact about a company, not a dangling
        // row — the same reasoning that keeps a shared skill alive after its last
        // link goes.
        _db.JobPostings.Remove(posting);
        await _db.SaveChangesAsync(ct);

        // PHASE 8 — PostingDeleted is no longer published, for the reason
        // DeleteApplication.cs sets out in full: the ai_analyses row it existed to
        // delete is a stored model result about an ad that still exists and can
        // come back, and destroying it costs a model call to re-earn.

        return SliceResult<bool>.Ok(true);
    }
}
