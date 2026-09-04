using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Analytics;

// Slice: how many applications you have in with each company.
//
// Marked optional in the phase doc; built because the `companies` table has been
// carrying an unexecuted promise since Phase 2. Company.cs justifies storing an
// employer as its own row with the words "that's what enables company-level
// rollups like '3 roles at Canva'" — and nothing in the API had ever asked that
// question. A normalization decision defended by a query nobody runs is the kind
// of thing that falls over in an interview.

public record CompanyRollupItem(string Name, int ApplicationCount);

public record CompanyRollup(int? Top) : IRequest<SliceResult<List<CompanyRollupItem>>>;

public class CompanyRollupHandler : IRequestHandler<CompanyRollup, SliceResult<List<CompanyRollupItem>>>
{
    private const int MaxTop = 100;
    private const int DefaultTop = 20;

    private readonly AnalyticsDbContext _db;

    public CompanyRollupHandler(AnalyticsDbContext db) => _db = db;

    public async ValueTask<SliceResult<List<CompanyRollupItem>>> Handle(
        CompanyRollup message, CancellationToken ct)
    {
        var top = message.Top;
        var take = top ?? DefaultTop;

        if (take < 1 || take > MaxTop)
            return SliceResult<List<CompanyRollupItem>>.Invalid($"top must be between 1 and {MaxTop}.");

        // PHASE 13.2 — the join and the GROUP BY moved into a view Applications
        // publishes; the ordering and the LIMIT stay here, because "top N" is
        // this module's question and not a property of the rollup.
        //
        // The consequence the old comment recorded is unchanged and now lives in
        // the view's SQL: it groups from the application side, so a company with
        // postings but no applications has no row and does not appear. That state
        // is still unreachable — companies are only ever created by
        // CreateApplication's find-or-create — so the view pays for the case that
        // can happen rather than the one that cannot. A posting-only import path
        // would make it a real omission, and the fix would be a LEFT JOIN in the
        // view rather than a change here.
        var rollup = await _db.CompanyApplicationCounts
            .AsNoTracking()
            .OrderByDescending(c => c.ApplicationCount)
            .ThenBy(c => c.CompanyName)
            .Take(take)
            .Select(c => new CompanyRollupItem(c.CompanyName, c.ApplicationCount))
            .ToListAsync(ct);

        // The case-sensitive dedup gap this comment used to record — "Canva" and
        // "canva" splitting one employer into two rows — was FIXED in Phase 7:
        // companies.Name carries a stored lower() generated column and the unique
        // index sits on it. Kept as a note rather than deleted because the rollup
        // is where that defect actually cost something visible.
        return SliceResult<List<CompanyRollupItem>>.Ok(rollup);
    }
}
