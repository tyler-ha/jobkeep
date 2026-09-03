// PHASE 13.1: the interface and its DTOs live in Jobkeep.Contracts; the
// implementation stays here, with the module that owns the tables it guards.
// The namespace is deliberately unchanged -- 13.6 renames namespaces to match
// projects, in one pass, once nothing else is moving.

using Jobkeep.Models;

namespace Jobkeep.Modules.Applications;

// The public contract Applications exposes about a POSTING — the ad behind an
// application. Its callers are the Ai module (Phase 4) and the Ats module (13.2e).
//
// ---------------------------------------------------------------------------
// The two-method cap is LIFTED at 13.2e, and that is not the cap failing
// ---------------------------------------------------------------------------
// Until this step this interface carried a hard cap: "capped at these two
// methods; a third one is the signal that the boundary is in the wrong place."
// The cap is gone and the paragraph it lived in is rewritten rather than deleted,
// because its reasoning was correct for the world it was written in and the world
// changed underneath it.
//
// What it said: architecture.md decision 17 made a cross-module READ ordinary and
// required a contract only for a WRITE. Under that rule the only methods this
// interface could ever need were its writes, plus the read that feeds one — so
// two really was the whole list, and a third method genuinely would have meant
// something was in the wrong module. AtsModule.cs cited this cap by name as the
// reason Ats could read `posting_skills` directly instead of asking for a
// GetPostingSkills method.
//
// What changed: Phase 13 reverses decision 17. The question is no longer "is this
// read safe?" — it is, a reader cannot corrupt anyone — but "can this module be
// lifted out?", and a SELECT across a boundary is precisely what stops working
// when the boundary becomes a network. Every crossing needs a contract now,
// reads included, so counting to two stopped being a bound on anything.
//
// The bound that replaces it is ISkillCatalog's test, which was always the real
// rule underneath the number: does a proposed method name a fact about a POSTING,
// or a question the caller has about its own feature? The four below are the
// first kind. "Which of this posting's skills is my résumé missing" is the second
// kind, and it stays in Ats — which is why the skill gap moved to Ats's own code
// rather than becoming a fifth method here. That test bounds the list by what a
// posting IS; a count bounds it by nothing once the count is wrong.
//
// The failure the old cap was guarding against has not gone away, and it is worth
// naming so it is not walked into: IJobApplicationRepository died in Phase 2.3
// because it had one method per use case, and every method added looked locally
// reasonable. A method that answers a caller's question rather than describing a
// posting is that failure wearing this interface's name.
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

    // The skills this posting asks for — ids and whether the ad said each was
    // required. Ids, not names, for the reason v_posting_skill_demand stops at
    // SkillId (13.2b): joining `skills` in here would not remove a cross-module
    // read, it would only move it from C# where the compiler sees it into SQL
    // where nothing does. The caller resolves names through ISkillCatalog.
    //
    // Empty for a posting that does not exist, which is the same answer as a
    // posting with no skills recorded. The one caller has already resolved the
    // application by the time it asks, so a distinction between them would have
    // no reader.
    Task<IReadOnlyList<PostingSkillRef>> GetSkillsAsync(
        Guid postingId, CancellationToken ct = default);

    // The posting's free-text requirements, in the order a reader should meet
    // them: must-haves first, then alphabetically within each group.
    //
    // The ordering is in here rather than at the call site because it is a fact
    // about requirements — a must-have outranks a nice-to-have on any screen that
    // shows both — and because it has to be STABLE for the caller, which numbers
    // the list for a model and stores the answers against those numbers.
    //
    // Unlimited, deliberately. The caller truncates to its own model-context
    // budget, which is its business and not this contract's, and the whole list
    // is bounded in practice: a real ad carries five to fifteen requirements.
    Task<IReadOnlyList<PostingRequirementText>> GetRequirementsAsync(
        Guid postingId, CancellationToken ct = default);
}

// Deliberately not the JobPosting entity. Handing the entity across a module
// boundary would hand over its navigation properties too, and the boundary would
// exist on paper only (architecture.md A2, applied between modules instead of at
// the API edge).
public record PostingContent(Guid PostingId, string? Description);

// PHASE 14 added Kind, defaulted so the two existing call sites that do not know
// one need no change. It is passed straight through to ISkillCatalog, where it is
// advisory on create — this contract does not decide what a skill IS, it only
// carries what its caller found out while reading a document.
public record ExtractedSkill(string Name, bool IsRequired, SkillKind Kind = SkillKind.Unknown);

// One row of `posting_skills`, minus the row's own bookkeeping. Source is not
// here: whether a human typed a skill or a model extracted it governs whether
// AddExtractedSkillsAsync may overwrite it, which is a rule inside Applications
// and not a fact a reader needs.
public record PostingSkillRef(Guid SkillId, bool IsRequired);

// One requirement, as text plus its weight. Deliberately not carrying the
// requirement's Kind: the one caller asks a model whether the résumé evidences a
// line, and whether that line is a Qualification or a Responsibility does not
// change the question. Adding it later is additive; shipping an unread field is
// wire schema nobody can safely remove.
public record PostingRequirementText(string Text, bool IsMustHave);
