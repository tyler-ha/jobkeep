using Jobkeep.Data;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// PHASE 13.2d: the interface and its DTO live in Jobkeep.Contracts; the
// implementation stays here, with the module that owns the table it guards.
//
// It sits beside DocumentTextExtractor and DocumentStructurer in Infrastructure/
// rather than in Application/, matching where Applications puts its own two
// contracts: a slice answers a user's request, and this answers another module's.
public class ResumeContract : IResumeContract
{
    private readonly IDocumentsDbContext _db;

    public ResumeContract(IDocumentsDbContext db) => _db = db;

    public async Task<ResumeRef?> GetAsync(Guid resumeId, CancellationToken ct = default)
        // Two columns, projected. The caller is checking an id or rendering a
        // chip; loading the aggregate to do either would pull a whole CV — its
        // text, its contact details — across a module boundary to discard it.
        // That is finding A1 and the audit's PII exposure at the same time.
        => await _db.Resumes
            .AsNoTracking()
            .Where(r => r.Id == resumeId)
            .Select(r => new ResumeRef(r.Id, r.Label))
            .FirstOrDefaultAsync(ct);
}
