using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Modules.Skills;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// PHASE 13.1: the interface and its DTOs live in Jobkeep.Contracts; the
// implementation stays here, with the module that owns the tables it guards.
// The namespace is deliberately unchanged -- 13.6 renames namespaces to match
// projects, in one pass, once nothing else is moving.


public class PostingContract : IPostingContract
{
    private readonly IApplicationsDbContext _db;
    private readonly ISkillCatalog _skills;

    public PostingContract(IApplicationsDbContext db, ISkillCatalog skills)
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
            .Select(g => new ExtractedSkill(g.Key, g.First().IsRequired))
            .ToList();

        // One call for the whole batch. The catalog collapses spellings, so two
        // entries here can come back pointing at the same row — which is exactly
        // the duplicate-key case the `linked` set below already handles.
        var resolved = await _skills.FindOrCreateAsync(
            deduped.Select(s => new SkillRequest(s.Name)).ToList(), ct);

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
