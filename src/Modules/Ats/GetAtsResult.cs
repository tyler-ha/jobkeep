using Jobkeep.Data;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Ats;

// Slice: read back the stored ATS result for an application, without recomputing
// anything.
//
// This is the reason `ats_results` is a table rather than a computed response.
// The check is not expensive in the way the Phase 4 analyzer is — three of its
// four stages are pure SQL — but it is not free either, and more importantly it is
// not *stable*: the answer depends on the model's mood and on posting_skills rows
// that a later analyzer run can add. A stored result is the answer you actually
// read and acted on, and re-reading it should not quietly become a different one.
//
// Same split GetAnalysis.cs makes, for the same reason, and it returns the same
// DTO CheckAts returns so the two routes cannot drift apart.
public class GetAtsResultHandler
{
    private readonly AppDbContext _db;

    public GetAtsResultHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<AtsCheckResponse>> HandleAsync(
        Guid applicationId, CancellationToken ct = default)
    {
        // Flat projection into the response DTO, no Include. The one join is to
        // `resumes` for the label, which is a table this module does not own —
        // read-only, and legal under decision 17. See AtsModule.cs.
        var found = await _db.AtsResults
            .Where(r => r.ApplicationId == applicationId)
            .Select(r => new AtsCheckResponse(
                r.ApplicationId,
                r.ResumeId,
                r.Resume != null ? r.Resume.Label : null,
                r.MatchedKeywords,
                r.MissingMustHaveKeywords,
                r.MissingNiceToHaveKeywords,
                r.UnmetRequirements,
                r.FormattingRiskNotes,
                r.Warning,
                r.CheckedAtUtc))
            .FirstOrDefaultAsync(ct);

        // "Never checked" and "no such application" are both NotFound, and the
        // message says which is meant. Distinguishing them costs a second query
        // for a caller who is about to POST the check either way — the same call
        // GetAnalysis.cs makes.
        return found is null
            ? SliceResult<AtsCheckResponse>.NotFound(
                $"No ATS check stored for application {applicationId}. Run the check first.")
            : SliceResult<AtsCheckResponse>.Ok(found);
    }
}
