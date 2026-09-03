using Microsoft.EntityFrameworkCore;
using Jobkeep.Contracts.Ats;

namespace Jobkeep.Modules.Ats;

// PHASE 13.3c: the interface lives in Jobkeep.Contracts; the implementation
// stays here, with the module that owns the table it guards. Same placement as
// ApplicationContract and ResumeContract — Infrastructure/, not Application/,
// because a slice answers a user's request and this answers another module's.
public class AtsContract : IAtsContract
{
    private readonly AtsDbContext _db;

    public AtsContract(AtsDbContext db) => _db = db;

    public async Task<int> CountResultsForResumeAsync(Guid resumeId, CancellationToken ct = default)
        // Counted in SQL. The rows carry five text[] columns of keywords and
        // notes, so materialising them to call .Count would pull the whole of
        // every stored check across a boundary to produce one integer.
        => await _db.AtsResults.CountAsync(r => r.ResumeId == resumeId, ct);
}
