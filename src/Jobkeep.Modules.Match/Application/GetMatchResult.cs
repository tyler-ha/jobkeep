using Jobkeep.Contracts.Documents;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Match;

// Slice: read back the stored match result for an application, without recomputing
// anything.
//
// This is the reason `match_results` is a table rather than a computed response.
// The check is not expensive in the way the Phase 4 analyzer is — three of its
// four stages need no model — but it is not free either, and more importantly it
// is not *stable*: the answer depends on the model's mood and on posting_skills
// rows that a later analyzer run can add. A stored result is the answer you
// actually read and acted on, and re-reading it should not quietly become a
// different one.
//
// Same split GetAnalysis.cs makes, for the same reason, and it returns the same
// DTO RunMatchCheck returns so the two routes cannot drift apart.
//
// ---------------------------------------------------------------------------
// 13.2e: one column of this response is not in a table this module owns
// ---------------------------------------------------------------------------
// The résumé label was `r.Resume.Label` — a navigation property, so the query
// joined `resumes` while naming no foreign DbSet at all. That is the crossing
// class the phase's scope correction was written about: invisible to the boundary
// test, invisible in review, and the first thing to stop compiling at 13.3.
//
// It is now the row-plus-hydration shape ApplicationDetail arrived at in 13.2d:
// project what this module owns, then finish the response from a contract. The
// public DTO is unchanged, so no caller and no test on either surface moves.
public record GetMatchResult(Guid ApplicationId) : IRequest<SliceResult<MatchCheckResponse>>;

public class GetMatchResultHandler : IRequestHandler<GetMatchResult, SliceResult<MatchCheckResponse>>
{
    private readonly MatchDbContext _db;
    private readonly IResumeContract _resumes;

    public GetMatchResultHandler(MatchDbContext db, IResumeContract resumes)
    {
        _db = db;
        _resumes = resumes;
    }

    public async ValueTask<SliceResult<MatchCheckResponse>> Handle(
        GetMatchResult message, CancellationToken ct)
    {
        var applicationId = message.ApplicationId;
        // Flat projection, no Include, and the label left null for now.
        var found = await _db.MatchResults
            .AsNoTracking()
            .Where(r => r.ApplicationId == applicationId)
            .Select(r => new MatchCheckResponse(
                r.ApplicationId,
                r.ResumeId,
                null,
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
        if (found is null)
            return SliceResult<MatchCheckResponse>.NotFound(
                $"No match check stored for application {applicationId}. Run the check first.");

        // The one extra round trip, skipped when there is nothing to look up.
        // GetAsync, not GetContentAsync: this needs a chip's worth of text and
        // asking for the document would pull a CV across a boundary to read one
        // short column off it.
        //
        // A résumé id with no row leaves the label null rather than failing the
        // read. Impossible today — match_results.ResumeId is a foreign key — and
        // the honest answer at 13.3 when it is not, because a stored judgement is
        // still worth showing after the document it judged was deleted.
        if (found.ResumeId is null)
            return SliceResult<MatchCheckResponse>.Ok(found);

        var resume = await _resumes.GetAsync(found.ResumeId.Value, ct);

        return SliceResult<MatchCheckResponse>.Ok(found with { ResumeLabel = resume?.Label });
    }
}
