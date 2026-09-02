using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: delete an application.
//
// What survives the delete is a schema decision, not an accident:
//   - ats_results cascades (ApplicationsDbContext), because your resume-vs-this-posting
//     result has no meaning once the application is gone.
//   - the job_posting is deliberately left in place. The FK is Restrict in the
//     other direction and several applications can share one posting, so the ad
//     outlives your record of applying to it.
//   - the company and any shared skills survive for the same reason they are
//     shared rows at all.

public class DeleteApplicationHandler
{
    private readonly ApplicationsDbContext _db;

    public DeleteApplicationHandler(ApplicationsDbContext db) => _db = db;

    public async Task<SliceResult<bool>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        // Loads the row rather than issuing ExecuteDelete, so EF applies the
        // configured cascade to ats_results instead of leaving the database to
        // decide. Nothing else is loaded.
        var application = await _db.JobApplications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (application is null)
            return SliceResult<bool>.NotFound($"Application {id} not found.");

        _db.JobApplications.Remove(application);
        await _db.SaveChangesAsync(ct);

        return SliceResult<bool>.Ok(true);
    }
}
