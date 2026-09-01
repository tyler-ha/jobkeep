using Jobkeep.Data;
using Jobkeep.Shared;
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

public class CompanyRollupHandler
{
    private const int MaxTop = 100;
    private const int DefaultTop = 20;

    private readonly AppDbContext _db;

    public CompanyRollupHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<List<CompanyRollupItem>>> HandleAsync(
        int? top, CancellationToken ct = default)
    {
        var take = top ?? DefaultTop;

        if (take < 1 || take > MaxTop)
            return SliceResult<List<CompanyRollupItem>>.Invalid($"top must be between 1 and {MaxTop}.");

        // Grouped from the application side rather than by walking Companies and
        // counting their postings' applications. Both answer the question; this
        // one is a single GROUP BY with two joins, where the other is a
        // correlated subquery per company row.
        //
        // The consequence, stated rather than hidden: a company with postings but
        // no applications does not appear at all, because it has no row to group.
        // Today that state is unreachable — companies are only ever created by
        // CreateApplication's find-or-create — so the alternative would be paying
        // for a case that cannot happen. If a posting-only import path ever lands
        // (scraping, Phase 4), this becomes a real omission and the query has to
        // start from `companies` with a LEFT JOIN.
        var rollup = await _db.JobApplications
            .AsNoTracking()
            .GroupBy(a => a.Posting.Company.Name)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => new CompanyRollupItem(g.Key, g.Count()))
            .Take(take)
            .ToListAsync(ct);

        // Company dedup is case-sensitive too, so "Canva" and "canva" split into
        // two rows here exactly as "C#"/"c#" do in skill demand. Same known gap,
        // same migration-shaped fix. See CLAUDE.md "Known gaps".
        return SliceResult<List<CompanyRollupItem>>.Ok(rollup);
    }
}
