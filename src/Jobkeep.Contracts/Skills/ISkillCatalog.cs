namespace Jobkeep.Modules.Skills;

// PHASE 13.2: the interface and its DTO live in Jobkeep.Contracts; the
// implementation lives with the module that owns the table. The namespace is
// deliberately Jobkeep.Modules.Skills -- 13.6 renames namespaces to match
// projects, in one pass, once nothing else is moving.

// What the rest of the app is allowed to know about a skill. Deliberately not
// the Skill entity: handing that over hands over PostingSkills and ResumeSkills
// with it, and the boundary would exist on paper only.
public record SkillInfo(Guid Id, string Name, string? Category);

// A skill a caller wants to exist. Category is advisory: it is used only when
// the row is created, never to update one that is already there. Two modules
// disagreeing about whether "SQL" is a Language or a Database must not take
// turns overwriting each other, and the first writer to name it is as good a
// tiebreak as any -- the alternative is a merge rule nobody asked for.
public record SkillRequest(string Name, string? Category = null);

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
// 13.2c added the second and third verbs and stopped there. All three were
// named above before either had a caller, which is the difference between a
// closed list and a list that has not grown yet.
//
// ---------------------------------------------------------------------------
// The natural key is this module's business, and callers must not do it
// ---------------------------------------------------------------------------
// Phase 7 put a unique index on a STORED generated column, lower("Name"), and
// the standing rule is that the C# side and the generated column have to agree
// or a lookup misses a row the index then refuses to insert. Four modules each
// remembering to call NaturalKey.Of is four places to forget. It is applied in
// here, once.
//
// The visible consequence at 13.2c: no caller of this interface passes a
// normalized key, and none of them should start. `NaturalKey` is in SharedKernel
// and will stay reachable; the rule is that reaching for it near a skill name is
// the bug.
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

    // Resolves ONE name to the row it names, or null when no row does.
    //
    // Not batched, unlike GetAsync, and the asymmetry is the callers' shape
    // rather than an oversight: reads arrive as a page of ids, while a name
    // arrives from a human typing one into a box. There is no caller holding a
    // hundred names it did not itself create.
    //
    // The match is on the natural key, so "C#" finds the row stored as "c#".
    // That is a deliberate CHANGE from what the callers did before 13.2c, where
    // each compared `Skill.Name` directly and a wrong-cased name simply missed.
    // Phase 7's unique index means at most one row per natural key exists, so a
    // case-insensitive lookup can no longer match a row the caller did not name
    // — which was the objection the old comments recorded, and it stopped being
    // true when the index landed.
    Task<SkillInfo?> FindByNameAsync(string name, CancellationToken ct = default);

    // Find-or-create, batched, for a caller turning a list of names into links.
    //
    // Returns a dictionary keyed by the name AS PASSED IN, so the caller can map
    // its own list back to rows without knowing what normalization happened in
    // here. Two spellings of one skill therefore give two keys pointing at ONE
    // SkillInfo — call `.Values.DistinctBy(s => s.Id)` if what you want is the
    // set. That is the shape callers actually need, because they are building
    // link rows and the link table's key is the skill id.
    //
    // Blank names are skipped rather than refused: the callers are cleaning up
    // model output, and a model that emits an empty string in a list of skills
    // has not made an error the user can act on.
    //
    // ---------------------------------------------------------------------
    // 13.2c semantic change, stated because it is invisible at the call site
    // ---------------------------------------------------------------------
    // This SAVES. Before 13.2c every caller added the new `skills` rows to its
    // own change tracker and committed them in the same SaveChanges as the link
    // rows, so a new skill and the thing that referenced it were atomic. Through
    // a contract they cannot be, because at 13.3 this is a different service and
    // there is no shared transaction to join.
    //
    // The accepted cost is an ORPHAN TAXONOMY ROW: create the skill, fail to
    // create the link, and `skills` keeps a row nothing points at. It is
    // harmless in this schema — every count in Analytics is over link rows, and
    // find-or-create will reuse the orphan next time the name comes up — but it
    // is a real state the old code could not reach, so it is written down here
    // rather than discovered later.
    //
    // What callers must do about it: call this BEFORE adding their own rows to
    // the change tracker.
    //
    // PHASE 13.3b CHANGED WHY, and left the rule itself untouched. Until then
    // all six interfaces resolved one scoped AppDbContext, so a SaveChanges here
    // flushed whatever the caller had pending — the hazard was that a caller who
    // half-built an aggregate first would have it committed early, by someone
    // else's save, in a different transaction from the rest of its work.
    //
    // Since 13.3b the Skills context is genuinely its own unit of work, so this
    // save no longer touches the caller's change tracker at all. The rule
    // survives because the underlying problem does: this is a separate
    // transaction either way, so a failure after it leaves the skill rows
    // committed and the caller's rows not. Calling first is what keeps the
    // leftover an orphan taxonomy row — harmless, reusable — rather than a
    // half-written aggregate. The ordering was belt-and-braces under one shared
    // context and is the entire safeguard now.
    Task<IReadOnlyDictionary<string, SkillInfo>> FindOrCreateAsync(
        IReadOnlyCollection<SkillRequest> skills, CancellationToken ct = default);
}
