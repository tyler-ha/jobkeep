using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore;
using Jobkeep.Contracts.Match;

namespace Jobkeep.Modules.Match;

// PHASE 13.3c: the interface lives in Jobkeep.Contracts; the implementation
// stays here, with the module that owns the table it guards. Same placement as
// ApplicationContract and ResumeContract — Infrastructure/, not Application/,
// because a slice answers a user's request and this answers another module's.
public class MatchContract : IMatchContract
{
    private readonly MatchDbContext _db;

    public MatchContract(MatchDbContext db) => _db = db;

    public async Task<int> CountResultsForResumeAsync(Guid resumeId, CancellationToken ct = default)
        // Counted in SQL. The rows carry five text[] columns of keywords and
        // notes, so materialising them to call .Count would pull the whole of
        // every stored check across a boundary to produce one integer.
        => await _db.MatchResults.CountAsync(r => r.ResumeId == resumeId, ct);

    public async Task<IReadOnlyDictionary<Guid, MatchSummary>> GetSummariesAsync(
        IReadOnlyCollection<Guid> applicationIds, CancellationToken ct = default)
    {
        // An empty page asks nothing. Not an optimisation -- `WHERE id = ANY('{}')`
        // is a round trip whose answer is known before it is sent.
        if (applicationIds.Count == 0)
            return ReadOnlyDictionary<Guid, MatchSummary>.Empty;

        // Distinct because the caller is a list and a caller is allowed to be
        // careless; the same courtesy ISkillCatalog.GetAsync extends.
        var ids = applicationIds.Distinct().ToArray();

        // COUNTED IN SQL. `.Count` on a List<string> mapped to a Postgres text[]
        // translates to cardinality(), so the keyword arrays stay in the database
        // -- which is the whole reason this returns two integers. Materialising the
        // rows to call .Count in C# would pull every stored check's five arrays
        // across the boundary to produce a fraction.
        //
        // Total is matched + both missing buckets: every skill the ad named. It is
        // summed here rather than sent as three numbers because the caller renders
        // one fraction, and three numbers on the wire is three things a screen can
        // add up differently from this one.
        return await _db.MatchResults
            .AsNoTracking()
            .Where(r => ids.Contains(r.ApplicationId))
            .Select(r => new
            {
                r.ApplicationId,
                Matched = r.MatchedKeywords.Count,
                Total = r.MatchedKeywords.Count
                    + r.MissingMustHaveKeywords.Count
                    + r.MissingNiceToHaveKeywords.Count,
            })
            .ToDictionaryAsync(
                x => x.ApplicationId,
                x => new MatchSummary(x.Matched, x.Total),
                ct);
    }
}
