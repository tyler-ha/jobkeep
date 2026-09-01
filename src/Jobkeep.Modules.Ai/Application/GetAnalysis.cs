using Jobkeep.Data;
using Jobkeep.Modules.Applications;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Ai;

// Slice: read back the stored analysis for an application, without re-running
// the model.
//
// Not in the Phase 4 plan, and added anyway, because without it the analysis is
// write-only — the summary and seniority would be reachable only in the response
// of the run that produced them. The *skills* do show up in the application
// detail already, since they are ordinary posting_skills rows carrying
// Source = AiExtracted.
//
// Why this is not simply a field on ApplicationDetail: that projection belongs to
// Applications, and `ai_analyses` belongs to Ai. Adding it there would have
// Applications reading another module's table — the same rule-2 crossing the
// write path took a contract to avoid, in the opposite direction. A contract
// method for it would be Applications asking Ai for data so it could hand it
// back out again, which is a round trip for nothing. A separate route on the
// owning module is the cheaper honest answer, and it is why the note in
// ApplicationDetail.cs was rewritten rather than deleted.
public record AnalysisSummaryResponse(
    Guid PostingId,
    Models.SeniorityLevel Seniority,
    string? Summary,
    string? ModelUsed,
    DateTime AnalyzedAtUtc);

public class GetAnalysisHandler
{
    private readonly IAiDbContext _db;
    private readonly IApplicationContract _applications;

    public GetAnalysisHandler(IAiDbContext db, IApplicationContract applications)
    {
        _db = db;
        _applications = applications;
    }

    public async Task<SliceResult<AnalysisSummaryResponse>> HandleAsync(
        Guid applicationId, CancellationToken ct = default)
    {
        // PHASE 13.2 — This used to be ONE query: an EXISTS over job_applications
        // inside a query on ai_analyses, joining across the posting FK. The
        // comment here argued for that join on the grounds that the only contract
        // method available (IPostingContract.GetContentAsync) would have pulled a
        // whole job description over just to discard it, and that IPostingContract
        // was capped at two methods so a narrower one could not be added.
        //
        // Both halves of that argument are now gone. The cap belonged to the world
        // decision 17 described, where a cross-module READ was ordinary and only a
        // write needed guarding; Phase 13 reverses that, because a read across a
        // boundary is exactly what stops being possible when the module is a
        // separate deployable. And IApplicationContract.GetPostingIdAsync is the
        // narrow method the old comment wanted and could not have.
        //
        // The cost is one extra round trip, and it is worth naming rather than
        // hiding: two indexed primary-key lookups instead of one join. At 13.3
        // this stops being a choice at all — `job_applications` will be in another
        // schema and the join will not translate.
        var postingId = await _applications.GetPostingIdAsync(applicationId, ct);

        if (postingId is null)
            // Distinguished from "no analysis yet" now, which the old single-query
            // shape could not do — it saw one null and had to guess. The comment
            // it replaces said splitting these "would cost a second query"; the
            // second query is no longer optional, so the distinction is free.
            // Both are still 404, so no caller sees a different status.
            return SliceResult<AnalysisSummaryResponse>.NotFound(
                $"Application {applicationId} not found.");

        var found = await _db.AiAnalyses
            .AsNoTracking()
            .Where(a => a.PostingId == postingId.Value)
            .Select(a => new AnalysisSummaryResponse(
                a.PostingId, a.Seniority, a.Summary, a.ModelUsed, a.AnalyzedAtUtc))
            .FirstOrDefaultAsync(ct);

        return found is null
            ? SliceResult<AnalysisSummaryResponse>.NotFound(
                $"No analysis stored for application {applicationId}. Run the analyzer first.")
            : SliceResult<AnalysisSummaryResponse>.Ok(found);
    }
}
