using Jobkeep.Modules.Skills;
using Jobkeep.Shared;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: unlink a skill from a résumé — the inverse of AddSkillToResume.
//
// ---------------------------------------------------------------------------
// Why the inverse only arrives now
// ---------------------------------------------------------------------------
// AddSkillToResume shipped in Phase 5 without one, and that was defensible right
// up until there was a UI: nothing could put the wrong skill on a résumé except a
// deliberate API call. The Phase 6 design ships the CV-centre drag, which makes
// adding a skill a gesture — and a gesture you cannot undo is a worse feature
// than no gesture. So the asymmetry that Phase 5 closed on the add side
// (posting_skills was editable, resume_skills was not) had a second half.
//
// The normalization consequence, stated as RemoveSkillFromPosting states it: this
// deletes the `resume_skills` JOIN ROW and deliberately leaves the `skills` row
// alone. That skill may be on other résumés and on any number of postings, and
// the FK is DeleteBehavior.Restrict precisely so a shared row cannot vanish
// underneath them. "Take C# off my CV" is not "C# is no longer a skill".
//
// A near-copy of RemoveSkillFromPosting rather than a shared helper the two call,
// for the reason AddSkillToResume.cs gives at length: sharing the write path would
// put one module's write inside the other's file, which is the thing rule 2 exists
// to prevent. Documents owns `resume_skills`; Applications owns `posting_skills`.
public record RemoveSkillFromResume(
    Guid ResumeId,
    string SkillName) : IRequest<SliceResult<bool>>;

public class RemoveSkillFromResumeHandler : IRequestHandler<RemoveSkillFromResume, SliceResult<bool>>
{
    private readonly DocumentsDbContext _db;
    private readonly ISkillCatalog _skills;

    public RemoveSkillFromResumeHandler(DocumentsDbContext db, ISkillCatalog skills)
    {
        _db = db;
        _skills = skills;
    }

    public async ValueTask<SliceResult<bool>> Handle(
        RemoveSkillFromResume message, CancellationToken ct)
    {
        var (resumeId, skillName) = message;
        var name = skillName?.Trim();
        if (string.IsNullOrEmpty(name))
            return SliceResult<bool>.Invalid("skillName is required.");

        // Distinguish "no such résumé" from "that skill isn't on it", because the
        // two mean different things to a client: the first is a stale id, the
        // second is a no-op the UI can ignore. Projected to the key — this slice
        // deletes one join row and has no use for the résumé's text.
        var exists = await _db.Resumes.AnyAsync(r => r.Id == resumeId, ct);
        if (!exists)
            return SliceResult<bool>.NotFound($"Resume {resumeId} not found.");

        // Phase 13.2c — the name is resolved to an id first, then the join row is
        // matched on the pair. It used to be one query with `rs.Skill.Name == name`
        // in it, which is a join onto another module's table that no DbSet
        // reference made visible; this is the same lookup with the boundary in it.
        //
        // A name nobody has ever used is a 404 without touching resume_skills at
        // all, which is strictly less work than the join was.
        //
        // The comparison also became case-INSENSITIVE, and that is a fix rather
        // than drift. The old comment here argued for case-sensitivity on the
        // grounds that a loose match on the way out could delete a row the caller
        // did not name — true while "C#" and "c#" could be two rows, and untrue
        // since Phase 7 put a unique index on lower("Name"). At most one row per
        // natural key exists, so there is nothing else the loose match could hit.
        var skill = await _skills.FindByNameAsync(name, ct);
        if (skill is null)
            return SliceResult<bool>.NotFound($"Skill '{name}' is not on resume {resumeId}.");

        var link = await _db.ResumeSkills
            .FirstOrDefaultAsync(rs => rs.ResumeId == resumeId && rs.SkillId == skill.Id, ct);
        if (link is null)
            return SliceResult<bool>.NotFound($"Skill '{name}' is not on resume {resumeId}.");

        _db.ResumeSkills.Remove(link);
        await _db.SaveChangesAsync(ct);
        return SliceResult<bool>.Ok(true);
    }
}
