namespace Jobkeep.SharedKernel;

// Phase 7. The two timestamps every independently-lifecycled row carries, and
// the marker the interceptor looks for.
//
// WHY AN INTERFACE AND NOT JUST TWO PROPERTIES
// --------------------------------------------
// Before this, `UpdatedAtUtc` was maintained by hand in exactly one place. The
// audit (F8) caught the failure mode demonstrating itself: Phase 2.1 replaced
// one stale write path with four, and none of the four touched the column. So
// the column was not merely un-audited, it was *wrong* — a value that says
// "last changed at X" when the row changed at Y is worse than no column, because
// a query trusts it.
//
// A hand-maintained audit column is only correct until someone adds a second
// write path. There is no way to make that safe by convention, so it is made
// safe by construction: `AuditSaveChangesInterceptor` stamps every entity that
// implements this on the way to the database, and a new slice cannot forget
// because it never had to remember. The interface is what the interceptor
// matches on — that is its whole job.
//
// WHICH ENTITIES GET IT, AND WHY NOT ALL OF THEM
// -----------------------------------------------
// The seven with an independent lifecycle: Company, JobPosting, Skill,
// JobRequirement, JobApplication, Resume, DocumentImport. Something creates and
// later changes each of them on its own schedule.
//
// Deliberately excluded, and the exclusions are the interesting half:
//
//   * Link rows (`PostingSkill`, `ResumeSkill`) have a composite key, are
//     insert-or-delete only, and are never updated. An `UpdatedAtUtc` on a row
//     that cannot be updated is a column that is always equal to another column.
//   * Child rows (`ResumeExperience`, `ResumeEducation`) are owned by a resume
//     and replaced wholesale when it is re-imported. Their lifecycle *is* the
//     parent's, so the parent's timestamps already answer the question.
//   * `AiAnalysis` and `MatchResult` already carry a domain timestamp that means
//     something more specific than "changed" — `AnalyzedAtUtc`, `CheckedAtUtc`.
//     Both are 1:1 records that re-running overwrites, so "when was this last
//     written" is exactly what those columns already say. A second, vaguer pair
//     beside them would be two answers to one question.
//
// The rule that falls out, worth applying to the next entity: a row gets audit
// timestamps when it can change without its parent changing.
public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }
    DateTime UpdatedAtUtc { get; set; }
}
