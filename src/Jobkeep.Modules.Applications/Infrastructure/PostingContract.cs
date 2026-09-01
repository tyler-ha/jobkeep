using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// PHASE 13.1: the interface and its DTOs live in Jobkeep.Contracts; the
// implementation stays here, with the module that owns the tables it guards.
// The namespace is deliberately unchanged -- 13.6 renames namespaces to match
// projects, in one pass, once nothing else is moving.


public class PostingContract : IPostingContract
{
    private readonly AppDbContext _db;

    public PostingContract(AppDbContext db) => _db = db;

    public async Task<PostingContent?> GetContentAsync(Guid applicationId, CancellationToken ct = default)
        // Flat projection, not Include: the caller wants two columns, and pulling
        // the aggregate to read one string is the over-fetch A1 is about.
        => await _db.JobApplications
            .Where(a => a.Id == applicationId)
            .Select(a => new PostingContent(a.PostingId, a.Posting.Description))
            .FirstOrDefaultAsync(ct);

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
        // KNOWN GAP, matching the rest of the codebase rather than fixing it
        // here: this dedup is case-sensitive, so "C#" and "c#" survive as two
        // rows. That is the same defect the human-entry path has, it is recorded
        // in CLAUDE.md and pinned by a test, and its fix is a migration to a
        // case-insensitive natural key (Phase 7). Fixing it only on the AI path
        // would make the two entry points disagree, which is worse than one
        // consistent known bug.
        var deduped = skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Name.Trim())
            .Select(g => new ExtractedSkill(g.Key, g.First().IsRequired))
            .ToList();

        var names = deduped.Select(s => s.Name).ToList();

        // Two round trips for the whole batch, not two per skill: load every
        // shared skill row and every existing link that could collide, then
        // decide in memory. The in-memory part is a set difference over a handful
        // of rows, not an aggregate — "aggregate in SQL" is about GROUP BY, and
        // this isn't one.
        // Phase 7 — batch-resolve on the natural key, not the raw name. Before
        // this the dictionary was keyed on Name, so an extracted "c#" missed the
        // stored "C#" and inserted a second row. That used to be a silent
        // duplicate; with the unique index on NameNormalized it would be a
        // failed INSERT, so this is a correctness fix and not a tidy-up.
        var keys = names.Select(NaturalKey.Of).ToList();
        var existingSkills = await _db.Skills
            .Where(s => keys.Contains(s.NameNormalized))
            .ToDictionaryAsync(s => s.NameNormalized, ct);

        var existingLinks = await _db.PostingSkills
            .Where(ps => ps.PostingId == postingId)
            .Select(ps => ps.SkillId)
            .ToListAsync(ct);
        var linked = existingLinks.ToHashSet();

        var created = 0;
        foreach (var extracted in deduped)
        {
            if (!existingSkills.TryGetValue(NaturalKey.Of(extracted.Name), out var skill))
            {
                // Added explicitly for the same reason AddSkillToPosting does it:
                // Skill.Id is client-generated, so EF reads the set key as
                // "already exists" and skips the INSERT unless told otherwise.
                skill = new Skill { Name = extracted.Name };
                _db.Skills.Add(skill);
                existingSkills[NaturalKey.Of(extracted.Name)] = skill;
            }

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
