using Jobkeep.Data;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: fetch one application in full.
//
// The read this replaces (IJobApplicationRepository.GetByIdAsync) built a
// five-part include graph behind AsSplitQuery and returned the EF entity. This
// one projects straight into ApplicationDetail, so it is a single statement
// selecting named columns, and what leaves the handler is a DTO the schema can
// change underneath (architecture.md A2).

public class GetApplicationHandler
{
    private readonly AppDbContext _db;

    public GetApplicationHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<ApplicationDetail>> HandleAsync(
        Guid id, CancellationToken ct = default)
    {
        var application = await _db.JobApplications
            .AsNoTracking()
            .Where(a => a.Id == id)
            .Select(ApplicationDetailProjection.Expression)
            .FirstOrDefaultAsync(ct);

        // Same message shape as every other slice, so the two surfaces render
        // one sentence in two forms rather than inventing their own.
        return application is null
            ? SliceResult<ApplicationDetail>.NotFound($"Application {id} not found.")
            : SliceResult<ApplicationDetail>.Ok(application);
    }
}
