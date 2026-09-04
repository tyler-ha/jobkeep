using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Skills;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: delete an application.
//
// What survives the delete is a schema decision, not an accident:
//   - the match_results row goes, but since 13.3b Postgres is no longer what makes
//     it go — see the publish below. Your resume-vs-this-posting result has no
//     meaning once the application is gone, and that rule outlived the foreign
//     key that used to enforce it.
//   - the job_posting is deliberately left in place. The FK is Restrict in the
//     other direction and several applications can share one posting, so the ad
//     outlives your record of applying to it. DeletePosting.cs is how you remove
//     the ad itself, once nothing is applying with it.
//   - the company and any shared skills survive for the same reason they are
//     shared rows at all.
public record DeleteApplication(Guid Id) : IRequest<SliceResult<bool>>;

public class DeleteApplicationHandler : IRequestHandler<DeleteApplication, SliceResult<bool>>
{
    private readonly ApplicationsDbContext _db;
    private readonly IPublisher _events;

    public DeleteApplicationHandler(ApplicationsDbContext db, IPublisher events)
    {
        _db = db;
        _events = events;
    }

    public async ValueTask<SliceResult<bool>> Handle(
        DeleteApplication message, CancellationToken ct)
    {
        var id = message.Id;
        // Loads the row rather than issuing ExecuteDelete, so EF applies the
        // configured cascades instead of leaving the database to decide. Since
        // 13.3b the only cascades left from here are within Applications' own
        // schema; nothing else is loaded.
        var application = await _db.JobApplications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (application is null)
            return SliceResult<bool>.NotFound($"Application {id} not found.");

        _db.JobApplications.Remove(application);
        await _db.SaveChangesAsync(ct);

        // PHASE 13.3c — the replacement for match_results' dropped CASCADE.
        //
        // AFTER the save, and the order is the decision. Publishing first would
        // mean deleting the match result of an application that then failed to
        // delete: a surviving row loses a stored judgement, and re-earning it
        // costs a model call the user waits three minutes for. Publishing after
        // means a failure between the two leaves an orphan `match_results` row that
        // nothing can read — nothing queries that table except by application id,
        // and that application is gone.
        //
        // So the two failure modes are "invisible orphan" and "destroyed work on
        // a live row", and this picks the first. It is the same call
        // ISkillCatalog.FindOrCreateAsync makes about its own save ordering, and
        // the same conclusion: prefer the residue nobody can see.
        //
        // The honest gap: this is publish-after-commit with no outbox, so a crash
        // in between loses the event entirely. Jobkeep.Contracts'
        // ApplicationEvents.cs records why an outbox waits for Phase 14 rather
        // than arriving here, and why 13.4's mediator did not change it.
        await _events.Publish(new ApplicationDeleted(id), ct);

        return SliceResult<bool>.Ok(true);
    }
}
