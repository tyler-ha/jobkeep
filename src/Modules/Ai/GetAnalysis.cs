using Jobkeep.Data;
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
    private readonly AppDbContext _db;

    public GetAnalysisHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<AnalysisSummaryResponse>> HandleAsync(
        Guid applicationId, CancellationToken ct = default)
    {
        // One query from application to analysis across the posting FK. This
        // traverses `job_applications` and `job_postings`, which Ai does not own —
        // but it reads nothing from them beyond the join, projecting only
        // ai_analyses columns. A contract method to resolve an id to a posting id
        // already exists (IPostingContract.GetContentAsync), and using it here
        // would pull the whole description over just to discard it. The cap on
        // that interface is deliberate, so this slice takes the join instead and
        // says so out loud.
        var found = await _db.AiAnalyses
            .Where(a => _db.JobApplications
                .Any(app => app.Id == applicationId && app.PostingId == a.PostingId))
            .Select(a => new AnalysisSummaryResponse(
                a.PostingId, a.Seniority, a.Summary, a.ModelUsed, a.AnalyzedAtUtc))
            .FirstOrDefaultAsync(ct);

        // "No analysis yet" and "no such application" are both NotFound here, and
        // the message says which. Distinguishing them would cost a second query
        // to prove the application exists, for a caller that is about to POST
        // /analyze either way.
        return found is null
            ? SliceResult<AnalysisSummaryResponse>.NotFound(
                $"No analysis stored for application {applicationId}. Run the analyzer first.")
            : SliceResult<AnalysisSummaryResponse>.Ok(found);
    }
}
