using Jobkeep.Data;
using Jobkeep.Shared;
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

public class SkillDemandHandler
{
    // A cap, not a preference — same reasoning as ListApplicationsHandler's
    // MaxPageSize. `top` reaches Take() directly off an unauthenticated query
    // string, so it needs a ceiling.
    private const int MaxTop = 100;
    private const int DefaultTop = 20;

    private readonly AppDbContext _db;

    public SkillDemandHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<List<SkillDemandItem>>> HandleAsync(
        int? top, CancellationToken ct = default)
    {
        var take = top ?? DefaultTop;

        // Rejects rather than clamps, matching the list slice: a caller asking
        // for 1000 rows and silently getting 100 has no way to tell.
        if (take < 1 || take > MaxTop)
            return SliceResult<List<SkillDemandItem>>.Invalid($"top must be between 1 and {MaxTop}.");

        // The aggregate runs in Postgres, not in C#. Ordering and Take are part
        // of the same statement, so the database returns at most `take` rows —
        // loading posting_skills and counting with LINQ-to-Objects would be the
        // same answer at the cost of the whole table, and is the thing "what
        // good looks like here" specifically rules out.
        //
        // Grouped by (Name, Category) rather than by SkillId because the count
        // that means something is per distinct skill *name*, and the name is
        // already unique in `skills`. Category rides along as a group key rather
        // than an aggregate for the same reason: it is functionally dependent on
        // the name, so grouping by it adds nothing and costs nothing.
        //
        // COUNT(*) over posting_skills is a count of POSTINGS, not applications.
        // The composite PK means a skill is linked to a posting at most once, so
        // one posting contributes one row here even if you applied to it twice.
        // "Which skills does the market ask for" is a question about ads, not
        // about how many times you hit send.
        var demand = await _db.PostingSkills
            .AsNoTracking()
            .GroupBy(ps => new { ps.Skill.Name, ps.Skill.Category })
            .OrderByDescending(g => g.Count())
            // Alphabetical tiebreak, for the same reason ListApplications sorts
            // by Id after DateApplied: ties plus a LIMIT is how a row goes
            // missing between two otherwise identical calls.
            .ThenBy(g => g.Key.Name)
            .Select(g => new SkillDemandItem(g.Key.Name, g.Key.Category, g.Count()))
            .Take(take)
            .ToListAsync(ct);

        // Known gap, recorded not fixed: `skills` dedups case-sensitively, so
        // "C#" and "c#" are two rows and appear here as two entries splitting
        // one skill's count. This is where that defect actually costs something
        // — a demand table is exactly what a duplicate row corrupts. The fix is
        // a case-insensitive natural key, which is a migration, so it has its
        // own phase. See CLAUDE.md "Known gaps".
        return SliceResult<List<SkillDemandItem>>.Ok(demand);
    }
}
