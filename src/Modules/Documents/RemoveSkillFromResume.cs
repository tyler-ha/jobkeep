using Jobkeep.Data;
using Jobkeep.Shared;
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
public class RemoveSkillFromResumeHandler
{
    private readonly AppDbContext _db;

    public RemoveSkillFromResumeHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<bool>> HandleAsync(
        Guid resumeId, string skillName, CancellationToken ct = default)
    {
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

        // Matched on the pair, not on the skill alone: the join row is what is
        // being deleted, and it is identified by (résumé, skill).
        //
        // The name comparison is case-SENSITIVE, matching every other writer of
        // the shared `skills` table. That is the known dedup gap in CLAUDE.md, not
        // an oversight here — a case-insensitive match on the way *out* while the
        // way *in* stays case-sensitive would let this delete a row the caller did
        // not name.
        var link = await _db.ResumeSkills
            .FirstOrDefaultAsync(rs => rs.ResumeId == resumeId && rs.Skill.Name == name, ct);
        if (link is null)
            return SliceResult<bool>.NotFound($"Skill '{name}' is not on resume {resumeId}.");

        _db.ResumeSkills.Remove(link);
        await _db.SaveChangesAsync(ct);
        return SliceResult<bool>.Ok(true);
    }
}
