namespace Jobkeep.SharedKernel;

// Phase 11.2b — the marker for a row that belongs to one user.
// Closes audit finding F1's second half: 11.2a asked *is anyone there*, this
// asks *whose row is this*.
//
// WHICH ENTITIES GET IT, AND WHY THE CHILDREN DO NOT
// --------------------------------------------------
// The seven ROOTS — the rows a query starts from: JobApplication, JobPosting,
// Company, Resume, DocumentImport, AiAnalysis, MatchResult.
//
// The five CHILDREN — PostingSkill, JobRequirement, ResumeSkill,
// ResumeExperience, ResumeEducation — deliberately do NOT carry it. Every slice
// that touches one of them resolves its parent first, and that read carries the
// owner filter: AddSkillToPosting, RemoveSkillFromPosting, AddRequirementToPosting
// and RemoveRequirement all begin with a lookup in `job_applications`;
// AddSkillToResume and RemoveSkillFromResume both begin with
// `Resumes.AnyAsync`. A child row is unreachable except through an
// owner-checked parent in the SAME schema, where the foreign key still exists
// and still cascades.
//
// The CONTRACT methods are the exception to that enumeration, because they take
// a parent id from another module and cannot see how it was obtained.
// `IPostingContract.AddExtractedSkillsAsync` is the only one that WRITES, and it
// checks the posting through the filtered set before it does. The three that read
// (`GetSkillsAsync`, `GetRequirementsAsync`, `IResumeContract.GetSkillIdsAsync`)
// do not, and each carries a `ponytail:` note saying what that rests on.
//
// This is the same argument Phase 8 made for leaving the soft-delete filter off
// those five tables, and it inherits the same ponytail ceiling from Program.cs:
// the safety rests on "every child slice routes through its parent". A future
// route addressed by child id would break it silently. That is the day to add
// the column, not today.
//
// `Skill` and `SkillAlias` are global by decision 9 (Accepted 2026-09-04) — one
// shared vocabulary, so the single GROUP BY that Postgres was chosen for keeps
// working. The accepted cost is that one user's taxonomy is visible in
// aggregate to another.
//
// WHY IT IS NOT A FOREIGN KEY
// ---------------------------
// Every value here points at `identity."AspNetUsers"`, across a schema
// boundary, and 13.3b/13.3c made a cross-schema relationship a contract check
// rather than a constraint. There is no contract check either, and none is
// needed: the only writer of this column is the interceptor, and the only value
// it can write is the id of a principal the authorization layer already
// accepted.
public interface IOwned
{
    Guid OwnerUserId { get; set; }
}

// The two global filter keys, named because EF 10 lets a query drop ONE of them.
//
// Before this phase there was one filter per entity and `IgnoreQueryFilters()`
// meant "show me archived rows". With a second filter that sentence would also
// mean "show me other people's rows", which is the accident this phase exists to
// make impossible — so the five restore/include-archived call sites now name the
// filter they are escaping.
public static class QueryFilters
{
    public const string SoftDelete = nameof(SoftDelete);
    public const string Owner = nameof(Owner);
}
