namespace Jobkeep.Modules.Skills;

// PHASE 13.2: the interface and its DTO live in Jobkeep.Contracts; the
// implementation lives with the module that owns the table. The namespace is
// deliberately Jobkeep.Modules.Skills -- 13.6 renames namespaces to match
// projects, in one pass, once nothing else is moving.

// What the rest of the app is allowed to know about a skill. Deliberately not
// the Skill entity: handing that over hands over PostingSkills and ResumeSkills
// with it, and the boundary would exist on paper only.
public record SkillInfo(Guid Id, string Name, string? Category);

// The shared skill taxonomy, as a service rather than a table.
//
// ---------------------------------------------------------------------------
// Why this one is allowed to have several methods
// ---------------------------------------------------------------------------
// IPostingContract carries a cap comment saying a third method means the
// boundary is in the wrong place, and that warning was earned — this codebase
// has twice watched a contract grow one locally-reasonable method at a time
// (IJobApplicationRepository, decision 5; the contract AnalyticsModule refused
// to build, decision 13). Both grew because they had one method PER QUESTION,
// and there is no bound on how many questions a caller has about someone else's
// data.
//
// A catalog is a different shape and the difference is not a matter of degree.
// Its methods are the operations of a *vocabulary* — resolve an id, resolve a
// name, find or create one — and that list is closed by what a vocabulary is,
// not by what its callers happen to want this week. Five modules will share it
// and none of them will need a sixth verb.
//
// The test to apply to a proposed addition: does it name a SKILL operation, or
// does it name a question the caller has about its own feature? "Get these ids"
// is the first. "Which skills does posting X ask for that resume Y lacks" is the
// second, and belongs to whoever owns that question.
//
// ---------------------------------------------------------------------------
// The natural key is this module's business, and callers must not do it
// ---------------------------------------------------------------------------
// Phase 7 put a unique index on a STORED generated column, lower("Name"), and
// the standing rule is that the C# side and the generated column have to agree
// or a lookup misses a row the index then refuses to insert. Four modules each
// remembering to call NaturalKey.Of is four places to forget. It is applied in
// here, once.
public interface ISkillCatalog
{
    // Resolves ids to names, for a caller that holds skill ids and needs to
    // render them. Ids with no row are simply absent from the result rather than
    // throwing: the caller reading a link table has already been told by its own
    // foreign key that the row exists, and a missing one at 13.3 (when that FK is
    // dropped) is a gap to report, not an exception to raise here.
    //
    // Batched, never one id at a time. The callers are all rendering a page of
    // rows, so the per-id version would be a query per row.
    Task<IReadOnlyDictionary<Guid, SkillInfo>> GetAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}
