using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: one résumé, in full — the structured records plus the text they were
// parsed out of.
//
// The counterpart to ListResumes, and the split between the two is deliberate in
// exactly the way ListImports/GetImport is. The list omits SourceText because a
// list of ten cards must not ship ten résumés; this one includes it, because the
// détail screen's job is to show you what the parser made of your document, and
// you cannot judge that without the document. Same exception, same reason, and
// GetImport.cs states it first.
//
// One thing this is NOT: it is not the ATS check's input. That reads
// resume_skills as rows and joins them against posting skills (Modules/Ats/
// CheckAts.cs) — it never reads this DTO. Nothing here is on a hot path.

public record ResumeSkillItem(string SkillName, string? Category, SkillSource Source);

public record ResumeExperienceItem(
    Guid Id,
    string Employer,
    string? Title,
    string? StartText,
    string? EndText,
    List<string> Highlights,
    int Ordinal);

public record ResumeEducationItem(
    Guid Id,
    string Institution,
    string? Qualification,
    string? YearText,
    int Ordinal);

public record ResumeDetail(
    Guid Id,
    string Label,
    string? FullName,
    string? Email,
    string? Phone,
    string? Location,
    string? Headline,
    string SourceText,
    string? SourceFileName,
    SourceFormat? SourceFormat,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    List<ResumeSkillItem> Skills,
    List<ResumeExperienceItem> Experiences,
    List<ResumeEducationItem> Educations);

public class GetResumeHandler
{
    private readonly AppDbContext _db;

    public GetResumeHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<ResumeDetail>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var detail = await _db.Resumes
            .AsNoTracking()
            .Where(r => r.Id == id)
            // Projected, not Include()d, even though this read wants nearly the
            // whole aggregate. Include builds a graph of entities and then leans
            // on the serializer to hide the navigation properties that cycle;
            // this returns records that have no cycles to hide (architecture.md
            // A1/A2, and the reason ReferenceHandler.IgnoreCycles could be
            // deleted in Phase 2.3).
            .Select(r => new ResumeDetail(
                r.Id,
                r.Label,
                r.FullName,
                r.Email,
                r.Phone,
                r.Location,
                r.Headline,
                r.SourceText,
                r.SourceFileName,
                r.SourceFormat,
                r.CreatedAtUtc,
                r.UpdatedAtUtc,
                // Alphabetical, because skills have no meaningful document order —
                // they arrive from a list the parser found or from a human
                // clicking one on, and neither carries a rank worth preserving.
                r.ResumeSkills
                    .OrderBy(rs => rs.Skill.Name)
                    .Select(rs => new ResumeSkillItem(rs.Skill.Name, rs.Skill.Category, rs.Source))
                    .ToList(),
                // Experiences and educations DO have one, and Ordinal exists to
                // hold it: a résumé is read top-down and the top entry is the
                // current job (Models/Resume.cs). Without this the rows come back
                // in whatever order Postgres finds convenient, which would quietly
                // reorder someone's career on the screen.
                r.Experiences
                    .OrderBy(e => e.Ordinal)
                    .Select(e => new ResumeExperienceItem(
                        e.Id, e.Employer, e.Title, e.StartText, e.EndText, e.Highlights, e.Ordinal))
                    .ToList(),
                r.Educations
                    .OrderBy(e => e.Ordinal)
                    .Select(e => new ResumeEducationItem(
                        e.Id, e.Institution, e.Qualification, e.YearText, e.Ordinal))
                    .ToList()))
            .FirstOrDefaultAsync(ct);

        if (detail is null)
            return SliceResult<ResumeDetail>.NotFound($"Resume {id} not found.");

        return SliceResult<ResumeDetail>.Ok(detail);
    }
}
