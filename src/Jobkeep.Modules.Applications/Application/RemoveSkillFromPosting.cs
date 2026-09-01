using Jobkeep.Data;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: unlink a skill from an application's posting.
//
// The normalization consequence worth stating out loud: this deletes the
// `posting_skills` JOIN ROW and deliberately leaves the `skills` row alone. That
// skill may be attached to other postings, and the FK from posting_skills to
// skills is DeleteBehavior.Restrict precisely so a shared row can't vanish
// underneath them. "Remove C# from this job" is not "C# is no longer a skill".
public class RemoveSkillFromPostingHandler
{
    private readonly AppDbContext _db;

    public RemoveSkillFromPostingHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<bool>> HandleAsync(
        Guid applicationId, string skillName, CancellationToken ct = default)
    {
        var name = skillName?.Trim();
        if (string.IsNullOrEmpty(name))
            return SliceResult<bool>.Invalid("skillName is required.");

        var postingId = await _db.JobApplications
            .Where(a => a.Id == applicationId)
            .Select(a => a.PostingId)
            .FirstOrDefaultAsync(ct);
        if (postingId == Guid.Empty)
            return SliceResult<bool>.NotFound($"Application {applicationId} not found.");

        // Match on the pair, not on the skill alone: the join row is what's being
        // deleted, and it's identified by (posting, skill).
        var link = await _db.PostingSkills
            .FirstOrDefaultAsync(ps => ps.PostingId == postingId && ps.Skill.Name == name, ct);
        if (link is null)
            return SliceResult<bool>.NotFound($"Skill '{name}' is not linked to application {applicationId}.");

        _db.PostingSkills.Remove(link);
        await _db.SaveChangesAsync(ct);
        return SliceResult<bool>.Ok(true);
    }
}
