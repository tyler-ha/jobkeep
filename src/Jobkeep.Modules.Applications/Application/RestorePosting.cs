using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: bring an archived job ad back. PHASE 8.
//
// The mirror of RestoreApplication and deliberately not merged with it. Two
// entities, two DbSets, two messages — a shared `Restore<T>` would need the
// entity type as a parameter, which is where a slice stops owning its use case
// and starts being a small ORM. Thirty lines of near-duplicate is the cheaper
// half of that trade, and it is what lets this file carry the note below that
// RestoreApplication's cannot.
//
// UNLIKE RESTORING AN APPLICATION, THIS ONE CANNOT PARTIALLY FAIL. The ad has no
// unique index it could have lost (Title is not unique — two companies advertise
// "Software Engineer"), and nothing above it can be archived: `companies` is not
// soft-deletable at all. So there is no third case here, where the résumé has
// two and the application has one.
//
// The ad's skills and requirements come back with it because they never left —
// the DELETE that used to cascade into them is what soft delete stopped running.
public record RestorePosting(Guid Id) : IRequest<SliceResult<bool>>;

public class RestorePostingHandler : IRequestHandler<RestorePosting, SliceResult<bool>>
{
    private readonly ApplicationsDbContext _db;

    public RestorePostingHandler(ApplicationsDbContext db) => _db = db;

    public async ValueTask<SliceResult<bool>> Handle(
        RestorePosting message, CancellationToken ct)
    {
        var id = message.Id;

        var posting = await _db.JobPostings
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id && p.IsDeleted, ct);

        if (posting is null)
            return SliceResult<bool>.NotFound($"No archived job ad {id} found.");

        posting.IsDeleted = false;
        posting.DeletedAtUtc = null;
        await _db.SaveChangesAsync(ct);

        return SliceResult<bool>.Ok(true);
    }
}
