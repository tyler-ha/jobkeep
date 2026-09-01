// PHASE 13.1: the interface and its DTOs live in Jobkeep.Contracts; the
// implementation stays here, with the module that owns the tables it guards.
// The namespace is deliberately unchanged -- 13.6 renames namespaces to match
// projects, in one pass, once nothing else is moving.

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
