using Jobkeep.Data;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// PHASE 13.2d: the interface and its DTO live in Jobkeep.Contracts; the
// implementation stays here, with the module that owns the table it guards.
//
// It sits beside DocumentTextExtractor and DocumentStructurer in Infrastructure/
// rather than in Application/, matching where Applications puts its own two
// contracts: a slice answers a user's request, and this answers another module's.
public class ResumeContract : IResumeContract
{
    private readonly IDocumentsDbContext _db;

    public ResumeContract(IDocumentsDbContext db) => _db = db;

    public async Task<ResumeRef?> GetAsync(Guid resumeId, CancellationToken ct = default)
        // Two columns, projected. The caller is checking an id or rendering a
        // chip; loading the aggregate to do either would pull a whole CV — its
        // text, its contact details — across a module boundary to discard it.
        // That is finding A1 and the audit's PII exposure at the same time.
        => await _db.Resumes
            .AsNoTracking()
            .Where(r => r.Id == resumeId)
            .Select(r => new ResumeRef(r.Id, r.Label))
            .FirstOrDefaultAsync(ct);

    public async Task<ResumeContent?> GetContentAsync(Guid resumeId, CancellationToken ct = default)
    {
        // Still a flat projection, and Experiences is counted in SQL rather than
        // loaded — the caller wants the number of roles, not the roles, and
        // materialising them would be a second CV's worth of personal history
        // pulled across a boundary to call .Count on.
        var row = await _db.Resumes
            .AsNoTracking()
            .Where(r => r.Id == resumeId)
            .Select(r => new
            {
                r.Id,
                r.Label,
                r.FullName,
                r.Email,
                r.Location,
                r.SourceFormat,
                r.SourceText,
                ExperienceCount = r.Experiences.Count
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        return new ResumeContent(
            row.Id, row.Label, row.FullName, row.Email, row.Location,
            MapFormat(row.SourceFormat), row.SourceText, row.ExperienceCount);
    }

    public async Task<IReadOnlyList<Guid>> GetSkillIdsAsync(
        Guid resumeId, CancellationToken ct = default)
        // Ids only, unordered. Both are deliberate: the name lives in the skills
        // vocabulary and is the catalog's to resolve, and the caller is building a
        // set to test membership against, so any order it were given would be
        // thrown away.
        => await _db.ResumeSkills
            .AsNoTracking()
            .Where(rs => rs.ResumeId == resumeId)
            .Select(rs => rs.SkillId)
            .ToListAsync(ct);

    // The explicit switch the contract's enum comment promises. A cast would
    // compile and stay compiling after someone reorders either enum; this stops
    // building the moment the two lists disagree, which is the whole reason the
    // duplication is written down as correct rather than tolerated.
    private static ResumeSourceFormat? MapFormat(Models.SourceFormat? format) => format switch
    {
        Models.SourceFormat.PlainText => ResumeSourceFormat.PlainText,
        Models.SourceFormat.Markdown => ResumeSourceFormat.Markdown,
        Models.SourceFormat.Pdf => ResumeSourceFormat.Pdf,
        Models.SourceFormat.Docx => ResumeSourceFormat.Docx,
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };
}
