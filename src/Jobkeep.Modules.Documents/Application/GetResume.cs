using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Modules.Skills;
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
    // The shape SQL can actually produce, which since 13.2c is no longer the
    // shape the API returns. Its skills are ids: the names live in another
    // module's table, so the projection stops one join short and the handler
    // finishes the job through ISkillCatalog.
    //
    // A near-duplicate of ResumeDetail, and the duplication is the visible price
    // of the boundary rather than an oversight. It is one record in one file, it
    // is private, and it disappears the moment a résumé's skills come back from
    // the taxonomy service already named.
    private record ResumeRow(
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
        List<ResumeSkillRow> Skills,
        List<ResumeExperienceItem> Experiences,
        List<ResumeEducationItem> Educations);

    private record ResumeSkillRow(Guid SkillId, SkillSource Source);

    private readonly IDocumentsDbContext _db;
    private readonly ISkillCatalog _skills;

    public GetResumeHandler(IDocumentsDbContext db, ISkillCatalog skills)
    {
        _db = db;
        _skills = skills;
    }

    public async Task<SliceResult<ResumeDetail>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.Resumes
            .AsNoTracking()
            .Where(r => r.Id == id)
            // Projected, not Include()d, even though this read wants nearly the
            // whole aggregate. Include builds a graph of entities and then leans
            // on the serializer to hide the navigation properties that cycle;
            // this returns records that have no cycles to hide (architecture.md
            // A1/A2, and the reason ReferenceHandler.IgnoreCycles could be
            // deleted in Phase 2.3).
            .Select(r => new ResumeRow(
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
                // Phase 13.2c — ids and provenance only. This used to read
                // `rs.Skill.Name`, which compiles into a join onto `skills` and
                // is a cross-module read the compiler makes invisible: no DbSet
                // is named, so the boundary test would have passed while the
                // query crossed. The names are resolved below, through
                // ISkillCatalog, which is the seam that survives 13.3.
                r.ResumeSkills
                    .Select(rs => new ResumeSkillRow(rs.SkillId, rs.Source))
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

        if (row is null)
            return SliceResult<ResumeDetail>.NotFound($"Resume {id} not found.");

        // One batched call for the whole résumé's skills, not one per row — see
        // ISkillCatalog.GetAsync. A résumé carries tens of skills, so this is one
        // extra round trip in place of a join, which is the price of the boundary
        // and is stated rather than hidden.
        var names = await _skills.GetAsync(row.Skills.Select(x => x.SkillId).ToList(), ct);

        // Alphabetical, because skills have no meaningful document order — they
        // arrive from a list the parser found or from a human clicking one on,
        // and neither carries a rank worth preserving. It happens in memory now
        // rather than in SQL: the names are not in the database's hands any more.
        // Tens of rows, already materialised, so the sort costs nothing.
        //
        // A skill id with no row is dropped rather than rendered blank. The
        // foreign key makes that impossible today; at 13.3, when the FK is gone,
        // a missing row means the taxonomy service lost a row this résumé points
        // at, and showing an empty chip would be the worst of the three options.
        var skills = row.Skills
            .Where(x => names.ContainsKey(x.SkillId))
            .Select(x => new ResumeSkillItem(names[x.SkillId].Name, names[x.SkillId].Category, x.Source))
            .OrderBy(x => x.SkillName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return SliceResult<ResumeDetail>.Ok(new ResumeDetail(
            row.Id,
            row.Label,
            row.FullName,
            row.Email,
            row.Phone,
            row.Location,
            row.Headline,
            row.SourceText,
            row.SourceFileName,
            row.SourceFormat,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            skills,
            row.Experiences,
            row.Educations));
    }
}
