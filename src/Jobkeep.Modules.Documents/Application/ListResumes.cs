using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: the résumé shelf — which versions do I keep, and how fleshed out is each.
//
// ---------------------------------------------------------------------------
// Why this only arrives in Phase 6
// ---------------------------------------------------------------------------
// Until now `resumes` had no read surface at all. Phase 4.5 created the rows and
// Phase 5 read them from inside the ATS check, but the only route under /resumes
// was POST /resumes/{id}/skills — so a résumé was reachable only by remembering
// the id of the import that made it. That was survivable while the client was
// Swagger and the ids were in front of you; it stops being survivable the moment
// a screen has to offer a *picker*, which is what both the Résumés screen and the
// ATS check need.
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
    DateTime UpdatedAtUtc);

public class ListResumesHandler
{
    private readonly IDocumentsDbContext _db;
    private readonly DocumentOptions _options;

    public ListResumesHandler(IDocumentsDbContext db, DocumentOptions options)
    {
        _db = db;
        _options = options;
    }

    public async Task<SliceResult<List<ResumeSummary>>> HandleAsync(CancellationToken ct = default)
    {
        var items = await _db.Resumes
            .AsNoTracking()
            // Most recently touched first. UpdatedAtUtc rather than CreatedAtUtc
            // because adding a skill to a résumé is what the ATS check's drag
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
                r.UpdatedAtUtc))
            .ToListAsync(ct);

        return SliceResult<List<ResumeSummary>>.Ok(items);
    }
}
