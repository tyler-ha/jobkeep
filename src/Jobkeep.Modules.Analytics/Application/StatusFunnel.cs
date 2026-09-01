using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Analytics;

// Slice: how many applications sit at each stage.
//
// The other half of "why relational" — a COUNT ... GROUP BY over one column.
// Less interesting than skill demand as a storage argument (a document store
// can maintain a counter), but it is the number the tool exists to show you.

public record StatusCount(ApplicationStatus Status, int Count);

// A funnel, not a bare list, because the total is part of the answer: the point
// of the view is the ratio between stages, and a caller that has to sum the
// stages itself to get a denominator will eventually sum them differently.
public record ApplicationFunnel(List<StatusCount> Stages, int Total);

public class StatusFunnelHandler
{
    private readonly AppDbContext _db;

    public StatusFunnelHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<ApplicationFunnel>> HandleAsync(CancellationToken ct = default)
    {
        // GROUP BY "Status" — in the database. Status is stored as text
        // (HasConversion<string>), so this groups on the string column and EF
        // converts each key back to the enum on the way out.
        var counts = await _db.JobApplications
            .AsNoTracking()
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        // A stage with no applications has no row to group, so SQL cannot return
        // it — and a funnel missing its empty stages is a broken funnel, because
        // "no offers yet" is exactly the fact you are looking for. The zero-fill
        // therefore happens here, in memory.
        //
        // That is not a contradiction of "aggregate in SQL, not in memory": the
        // counting is in SQL, and this loop is over the five values of an enum,
        // not over table rows. It stays O(stages) however many applications
        // exist.
        //
        // Ordered by the enum's declaration order, which is the lifecycle order
        // — Applied, Interviewing, Offer, Rejected, Withdrawn. Phase 2.5 defines
        // that lifecycle properly; until it does, this ordering is a convention
        // held up by the order of a few words in Enums.cs, and inserting a stage
        // in the wrong place there silently reorders this response.
        var stages = Enum.GetValues<ApplicationStatus>()
            .Select(status => new StatusCount(status, counts.GetValueOrDefault(status)))
            .ToList();

        return SliceResult<ApplicationFunnel>.Ok(
            new ApplicationFunnel(stages, stages.Sum(s => s.Count)));
    }
}
