using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: the résumé shelf — which versions do I keep, and how fleshed out is each.
//
// ---------------------------------------------------------------------------
// Why this only arrives in Phase 6
// ---------------------------------------------------------------------------
// Until now `resumes` had no read surface at all. Phase 4.5 created the rows and
// Phase 5 read them from inside the match check, but the only route under /resumes
// was POST /resumes/{id}/skills — so a résumé was reachable only by remembering
// the id of the import that made it. That was survivable while the client was
// Swagger and the ids were in front of you; it stops being survivable the moment
// a screen has to offer a *picker*, which is what both the Résumés screen and the
// match check need.
//
// Not paged, for the same reason ListImports is not: you keep two or three
// résumé versions, not two hundred. The MaxListSize cap is shared with the review
// queue rather than given a setting of its own — a second knob nobody will ever
// turn independently is a worse answer than a shared one.

// A summary WITHOUT SourceText, and that omission is the point.
//
// The résumé's full text is the most personal thing this database holds — a name,
// a phone number, an address and an employment history, in one column. The
// security audit's finding against the old job_applications.ResumeText column was
// exactly this shape, and Phase 2.3's fix (drop the big personal columns from list
// projections) is the precedent being followed rather than re-argued. GetResume
// returns it; a list that renders ten cards must not ship ten résumés to do it.
//
// SkillCount is here instead, because the one thing a picker genuinely needs to
// show is whether a version is fleshed out or a stub — and a count is not
// personal information.
public record ResumeSummary(
    Guid Id,
    string Label,
    string? FullName,
    string? Location,
    SourceFormat? SourceFormat,
    int SkillCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    // PHASE 8, and the same reasoning as ApplicationListItem.IsArchived: a mixed
    // list has to be renderable without the client inferring state from the
    // request it sent.
    bool IsArchived);

// PHASE 8 — the flag arrives as a parameter rather than as a query object,
// because one optional bool is not a filter surface. If a second ever lands here,
// that is the point to give this slice an ApplicationQuery of its own; doing it
// now would be building the shape rather than the feature.
//
// Defaulted, so every existing call site — the controller, the GraphQL resolver
// and eleven tests — compiles and keeps its old meaning unchanged.
public record ListResumes(bool IncludeArchived = false)
    : IRequest<SliceResult<List<ResumeSummary>>>;

public class ListResumesHandler : IRequestHandler<ListResumes, SliceResult<List<ResumeSummary>>>
{
    private readonly DocumentsDbContext _db;
    private readonly DocumentOptions _options;

    public ListResumesHandler(DocumentsDbContext db, DocumentOptions options)
    {
        _db = db;
        _options = options;
    }

    public async ValueTask<SliceResult<List<ResumeSummary>>> Handle(
        ListResumes message, CancellationToken ct)
    {
        var resumes = _db.Resumes.AsNoTracking();

        // PHASE 8. Include, not only — see ApplicationQuery.IncludeArchived.
        // Nothing else on a résumé is filtered, so unlike the applications list
        // this drops exactly one predicate.
        if (message.IncludeArchived)
            resumes = resumes.IgnoreQueryFilters();

        var items = await resumes
            // Most recently touched first. UpdatedAtUtc rather than CreatedAtUtc
            // because adding a skill to a résumé is what the match check's drag
            // does, and the version you just corrected is the one you are working
            // on. (A8 in the audit notes these timestamps are not yet maintained
            // by an interceptor — this ordering inherits that weakness rather
            // than introducing it.)
            .OrderByDescending(r => r.UpdatedAtUtc)
            .Take(_options.MaxListSize)
            // Flat projection, never Include: SourceText is the biggest column in
            // the table and it is not in this shape, so it is never read off disk
            // for this query. The count is a correlated COUNT in the SQL EF
            // generates — the row set is never materialised to be counted in C#,
            // which is the "aggregate in SQL" rule in CLAUDE.md.
            .Select(r => new ResumeSummary(
                r.Id,
                r.Label,
                r.FullName,
                r.Location,
                r.SourceFormat,
                r.ResumeSkills.Count,
                r.CreatedAtUtc,
                r.UpdatedAtUtc,
                r.IsDeleted))
            .ToListAsync(ct);

        return SliceResult<List<ResumeSummary>>.Ok(items);
    }
}
