using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Shared;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Applications.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// PHASE 13.1: the interface and its DTOs live in Jobkeep.Contracts; the
// implementation stays here, with the module that owns the tables it guards.
// The namespace is deliberately unchanged -- 13.6 renames namespaces to match
// projects, in one pass, once nothing else is moving.


public class PostingContract : IPostingContract
{
    private readonly ApplicationsDbContext _db;
    private readonly ISkillCatalog _skills;

    public PostingContract(ApplicationsDbContext db, ISkillCatalog skills)
    {
        _db = db;
        _skills = skills;
    }

    public async Task<PostingContent?> GetContentAsync(Guid applicationId, CancellationToken ct = default)
        // Flat projection, not Include: the caller wants two columns, and pulling
        // the aggregate to read one string is the over-fetch A1 is about.
        => await _db.JobApplications
            .Where(a => a.Id == applicationId)
            .Select(a => new PostingContent(a.PostingId, a.Posting.Description))
            .FirstOrDefaultAsync(ct);

    // ponytail: reads a child table by a caller-supplied parent id, and
    // `posting_skills` has no owner column — so this is scoped only because every
    // caller resolves the posting through GetContentAsync (filtered) first. Same
    // for GetRequirementsAsync below and IResumeContract.GetSkillIdsAsync. A
    // caller that obtains a parent id any other way reads a stranger's rows. The
    // write above checks; these do not, because a check on every read of a
    // child list is a join on the hot path. Add one here the day a caller stops
    // starting from an owner-checked parent.
    public async Task<IReadOnlyList<PostingSkillRef>> GetSkillsAsync(
        Guid postingId, CancellationToken ct = default)
        // No OrderBy. The caller needs these sorted by NAME, and the name is not
        // in this module's hands any more — it is resolved through ISkillCatalog
        // after the ids arrive, so ordering here would sort by the wrong thing
        // and read as if it had done the caller's job.
        => await _db.PostingSkills
            .AsNoTracking()
            .Where(ps => ps.PostingId == postingId)
            .Select(ps => new PostingSkillRef(ps.SkillId, ps.IsRequired))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PostingRequirementText>> GetRequirementsAsync(
        Guid postingId, CancellationToken ct = default)
        // Must-haves first, then alphabetical within each group — the order the
        // interface promises, and a stable one, which matters because the caller
        // numbers this list for a model and stores answers against those numbers.
        // Ordering by the column rather than by a property of the projected record
        // is not a style choice: EF cannot translate `ORDER BY new Requirement(...)`
        // and fails at runtime rather than at compile time.
        => await _db.JobRequirements
            .AsNoTracking()
            .Where(r => r.PostingId == postingId)
            .OrderBy(r => r.IsMustHave ? 0 : 1)
            .ThenBy(r => r.Text)
            .Select(r => new PostingRequirementText(r.Text, r.IsMustHave))
            .ToListAsync(ct);

    public async Task<int> AddExtractedSkillsAsync(
        Guid postingId, IReadOnlyList<ExtractedSkill> skills, CancellationToken ct = default)
    {
        if (skills.Count == 0) return 0;

        // PHASE 11.2b — the one WRITE on this contract, so the one that checks.
        //
        // `posting_skills` carries no owner column, by the argument in IOwned:
        // every slice reaches it through an owner-checked parent. That argument
        // holds for slices and does NOT hold for a contract method, which takes
        // a posting id from another module and cannot see how it was obtained.
        // Today's only caller derives it from GetContentAsync, which is filtered
        // — but "safe because of what the caller happens to do first" is exactly
        // the guarantee a cross-module boundary is supposed to stop relying on.
        //
        // `JobPostings` carries the owner filter, so this EXISTS is the whole
        // check. One extra round trip on a path that has just finished waiting
        // for a language model.
        if (!await _db.JobPostings.AnyAsync(p => p.Id == postingId, ct)) return 0;

        // Distinct by name first: an LLM asked for a skill list will sometimes
        // return "C#" twice, and the composite PK on posting_skills would turn
        // that into a duplicate-key exception on SaveChanges rather than a no-op.
        // First occurrence wins, so an early "required" is not downgraded by a
        // later "nice to have".
        //
        // Phase 13.2d — this dedup is on the RAW name and it is now only half the
        // job. The other half, collapsing "C#" and "c#" onto one row, moved into
        // ISkillCatalog with the natural key it needs. What is left here is the
        // part that is genuinely this method's business: which IsRequired wins
        // when a model says a skill twice. The catalog cannot answer that, because
        // IsRequired is a fact about a posting and not about a skill.
        var deduped = skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Name.Trim())
            .Select(g => new ExtractedSkill(g.Key, g.First().IsRequired, g.First().Kind))
            .ToList();

        // One call for the whole batch. The catalog collapses spellings, so two
        // entries here can come back pointing at the same row — which is exactly
        // the duplicate-key case the `linked` set below already handles.
        // PHASE 14 — Kind rides along. Advisory on create, so the first document
        // to call something Soft settles it and a later one disagreeing does not
        // start a tug of war. No Category: an ad names a skill, it does not name
        // the family that skill belongs to, and inventing one here would be this
        // method deciding something it did not read.
        var resolved = await _skills.FindOrCreateAsync(
            deduped.Select(s => new SkillRequest(s.Name, Kind: s.Kind)).ToList(), ct);

        // Every link that could collide, in one query. The in-memory part is a set
        // difference over a handful of rows, not an aggregate — "aggregate in SQL"
        // is about GROUP BY, and this isn't one.
        var existingLinks = await _db.PostingSkills
            .Where(ps => ps.PostingId == postingId)
            .Select(ps => ps.SkillId)
            .ToListAsync(ct);
        var linked = existingLinks.ToHashSet();

        var created = 0;
        foreach (var extracted in deduped)
        {
            // Absent only if the name was blank, which the filter above removed.
            if (!resolved.TryGetValue(extracted.Name, out var skill)) continue;

            // Already linked — skip, and note what that means: if a human added
            // "C#" with Source = Parsed, re-analyzing leaves their row alone
            // rather than restamping it AiExtracted. Human entry outranks
            // extraction, and a re-run never downgrades the provenance of a row
            // the user typed themselves.
            if (linked.Contains(skill.Id)) continue;

            _db.PostingSkills.Add(new PostingSkill
            {
                PostingId = postingId,
                SkillId = skill.Id,
                IsRequired = extracted.IsRequired,
                Source = SkillSource.AiExtracted   // the whole point of the enum
            });
            linked.Add(skill.Id);
            created++;
        }

        // One SaveChanges for the batch. A re-analysis that adds nothing new
        // still calls it, which is a no-op EF turns into zero SQL.
        await _db.SaveChangesAsync(ct);
        return created;
    }
}
