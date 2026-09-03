using Jobkeep.Contracts.Skills;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Analytics;

// Slice: which skills show up most across every posting you have tracked.
//
// This is the query the whole storage decision was made for. Phase 2's argument
// for Postgres over DynamoDB (architecture.md decision 1) was that a normalized,
// SHARED `skills` table turns "what should I learn next?" into one GROUP BY,
// where a denormalized document store has to read every document and tally in
// application code. Until now that argument existed only as a psql one-liner in
// the phase-2 doc. This is it, executable, over the API:
//
//   SELECT s."Name", COUNT(*) FROM skills s
//   JOIN posting_skills ps ON ps."SkillId" = s."Id"
//   GROUP BY s."Name" ORDER BY 2 DESC;

// Flat, and deliberately not carrying the postings themselves — this answers
// "how often", not "where". Drilling into which applications want a skill is
// already GET /applications?skill=<name> (Phase 2.3), so duplicating it here
// would be a second implementation of the same question.
public record SkillDemandItem(string Name, string? Category, int PostingCount);

public record SkillDemand(int? Top) : IRequest<SliceResult<List<SkillDemandItem>>>;

public class SkillDemandHandler : IRequestHandler<SkillDemand, SliceResult<List<SkillDemandItem>>>
{
    // A cap, not a preference — same reasoning as ListApplicationsHandler's
    // MaxPageSize. `top` reaches Take() directly off an unauthenticated query
    // string, so it needs a ceiling.
    private const int MaxTop = 100;
    private const int DefaultTop = 20;

    private readonly AnalyticsDbContext _db;
    private readonly ISkillCatalog _skills;

    public SkillDemandHandler(AnalyticsDbContext db, ISkillCatalog skills)
    {
        _db = db;
        _skills = skills;
    }

    public async ValueTask<SliceResult<List<SkillDemandItem>>> Handle(
        SkillDemand message, CancellationToken ct)
    {
        var top = message.Top;
        var take = top ?? DefaultTop;

        // Rejects rather than clamps, matching the list slice: a caller asking
        // for 1000 rows and silently getting 100 has no way to tell.
        if (take < 1 || take > MaxTop)
            return SliceResult<List<SkillDemandItem>>.Invalid($"top must be between 1 and {MaxTop}.");

        // PHASE 13.2 — the aggregate still runs in Postgres, but it now runs in
        // a view Applications publishes, and the view stops at SkillId.
        //
        // That is the interesting decision in this file, so it is worth being
        // exact about. `posting_skills` belongs to Applications and `skills` is
        // its own module (13.3 gives them separate schemas). A view that joined
        // them would not have removed the cross-module read — it would have moved
        // it from C#, where the compiler can see it, into SQL, where nothing can.
        // So the view counts rows it owns, and the names are resolved afterwards
        // through the catalog.
        //
        // The aggregate, the ordering and the LIMIT are all still one statement:
        // Postgres returns at most `take` rows, and the second query resolves at
        // most `take` ids. Loading posting_skills and counting in C# would be the
        // same answer at the cost of the whole table, and is the thing "what good
        // looks like here" specifically rules out. Two bounded queries is not that.
        var counts = await _db.PostingSkillDemands
            .AsNoTracking()
            .OrderByDescending(d => d.PostingCount)
            // Tiebreak on SkillId rather than on the name, because the name is
            // not in this view. See the note below on what that costs.
            .ThenBy(d => d.SkillId)
            .Take(take)
            .ToListAsync(ct);

        var names = await _skills.GetAsync(counts.Select(c => c.SkillId).ToList(), ct);

        // ACCEPTED BEHAVIOUR CHANGE, and the only one in 13.2 — the alphabetical
        // tiebreak is now WITHIN THE PAGE, not across the whole table.
        //
        // Before: ORDER BY count DESC, name ASC, then LIMIT, so among skills tied
        // on count the alphabetically-first ones were the ones kept. Now the
        // database breaks that tie on SkillId, which is arbitrary, and the
        // alphabetical sort happens after the names arrive. Same rows in the
        // common case; a different subset of a tied group at the LIMIT boundary.
        //
        // Why that is the right trade rather than a regression tolerated: the
        // alternative is the view joining `skills`, which is the coupling this
        // step exists to remove. The ordering that actually carries meaning — by
        // demand — is unchanged and still computed in SQL over the whole table.
        // The tiebreak was always a determinism device (see the note below), and
        // it still is one: ThenBy(SkillId) makes the page stable across calls,
        // which is the property that stopped rows going missing between two
        // otherwise identical requests.
        //
        // A skill id with no row in the catalog is skipped. That cannot happen
        // today — the FK guarantees it — but the FK is one of the five 13.3
        // drops, so the code is written for the world it is heading into.
        var demand = counts
            .Where(c => names.ContainsKey(c.SkillId))
            .Select(c => new SkillDemandItem(
                names[c.SkillId].Name, names[c.SkillId].Category, c.PostingCount))
            .OrderByDescending(d => d.PostingCount)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();

        // The known gap this comment used to record — `skills` dedupping
        // case-sensitively, so "C#" and "c#" appeared as two entries splitting
        // one skill's count — was FIXED in Phase 7 by a stored lower() generated
        // column with the unique index on it. This is where that defect cost the
        // most, which is why the note stays rather than being deleted: a demand
        // table is exactly what a duplicate row corrupts.
        //
        // It also retired a subtlety. This used to GROUP BY (Name, Category)
        // because the count that meant something was per distinct NAME; with the
        // name now unique in `skills`, grouping by SkillId in the view is the
        // same grouping, which is what let the join leave this file at all.
        return SliceResult<List<SkillDemandItem>>.Ok(demand);
    }
}
