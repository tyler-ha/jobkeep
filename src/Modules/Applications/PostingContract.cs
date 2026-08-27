using Jobkeep.Data;
using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// The public contract Applications exposes to other modules. Phase 4's Ai module
// is its first and only caller.
//
// ---------------------------------------------------------------------------
// Why this exists when Analytics didn't get one
// ---------------------------------------------------------------------------
// architecture.md rule 2 says a module only queries the tables it owns.
// Analytics is allowed to break that (decision 13), and the argument for the
// exception is entirely load-bearing on one word: Analytics is **read-only**.
// It "can never leave another module's data in a state that module did not
// choose, so the coupling is to a shape, not to a lifecycle."
//
// The Ai module writes. It creates `posting_skills` rows with
// Source = AiExtracted, which is precisely leaving Applications' data in a state
// Applications did not choose. So decision 13 does not stretch to cover it, and
// stretching it would retire the read-only constraint that made it defensible —
// the exception would quietly become the rule.
//
// Hence a contract. The thing to be honest about is that this is the same move
// AnalyticsModule.cs rejected, for a reason that has not gone away:
// IJobApplicationRepository died in Phase 2.3 because a contract with one method
// per use case grows without limit, and every method added looks locally
// reasonable.
//
// What is different here, and why it is worth the risk this time:
//
//   * Analytics needed one method **per question**, and there is no bound on how
//     many questions a reporting module has. Ai needs one method per *side effect
//     it has on someone else's tables*, and there are exactly two: read the text,
//     write the extracted skills.
//   * A write boundary is where the coupling actually costs something. Reading a
//     shape you don't own is recoverable; writing rows another module's
//     invariants depend on is not.
//
// **This interface is capped at these two methods.** A third one is the signal
// that the boundary is in the wrong place — at that point either the use case
// belongs in Applications, or Ai should own the tables outright. Do not grow it
// one locally-reasonable method at a time. That is exactly how the repository
// got to the size that killed it.
public interface IPostingContract
{
    // Resolves the application to its posting and hands back the text to analyze.
    // Returns null when the application does not exist — the caller turns that
    // into its own NotFound, because "which id was wrong" is the caller's message
    // to write, not this contract's.
    Task<PostingContent?> GetContentAsync(Guid applicationId, CancellationToken ct = default);

    // Links extracted skills to the posting, reusing shared `skills` rows by name.
    // Takes the whole batch rather than one skill at a time: a per-skill call
    // would mean one SaveChanges per skill, and an analysis that half-applied
    // when the fourth skill failed. Returns how many links were newly created —
    // re-analyzing a posting is expected to report fewer than it extracted.
    Task<int> AddExtractedSkillsAsync(
        Guid postingId, IReadOnlyList<ExtractedSkill> skills, CancellationToken ct = default);
}

// Deliberately not the JobPosting entity. Handing the entity across a module
// boundary would hand over its navigation properties too, and the boundary would
// exist on paper only (architecture.md A2, applied between modules instead of at
// the API edge).
public record PostingContent(Guid PostingId, string? Description);

public record ExtractedSkill(string Name, bool IsRequired);

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
        // case-insensitive natural key (Phase 2.7). Fixing it only on the AI path
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
        var existingSkills = await _db.Skills
            .Where(s => names.Contains(s.Name))
            .ToDictionaryAsync(s => s.Name, ct);

        var existingLinks = await _db.PostingSkills
            .Where(ps => ps.PostingId == postingId)
            .Select(ps => ps.SkillId)
            .ToListAsync(ct);
        var linked = existingLinks.ToHashSet();

        var created = 0;
        foreach (var extracted in deduped)
        {
            if (!existingSkills.TryGetValue(extracted.Name, out var skill))
            {
                // Added explicitly for the same reason AddSkillToPosting does it:
                // Skill.Id is client-generated, so EF reads the set key as
                // "already exists" and skips the INSERT unless told otherwise.
                skill = new Skill { Name = extracted.Name };
                _db.Skills.Add(skill);
                existingSkills[extracted.Name] = skill;
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
