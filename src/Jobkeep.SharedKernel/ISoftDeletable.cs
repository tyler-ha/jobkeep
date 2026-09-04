namespace Jobkeep.SharedKernel;

// Phase 8 — the marker for a row that is archived rather than destroyed.
// Closes audit finding F10.
//
// WHY AN INTERFACE, THE SAME ARGUMENT AS IAuditable
// -------------------------------------------------
// Two things have to be true of every soft-deletable entity and neither can be
// left to a slice to remember:
//
//   1. `Remove()` must mean *archive*, not *destroy*. That is done once, in
//      AuditSaveChangesInterceptor, by matching on this interface — so a slice
//      written next year that calls Remove() cannot accidentally hard-delete.
//   2. Every read must exclude archived rows. That is `HasQueryFilter`, applied
//      per entity in its own configuration, which is the only place EF lets it
//      be applied.
//
// The interface is what makes (1) possible. (2) is still stated three times,
// because a query filter is an expression over a *specific* entity type and EF
// has no convention hook that can write one generically without reflection —
// three explicit lines beat a clever one.
//
// WHICH ENTITIES GET IT
// ---------------------
// The three with a delete slice: JobApplication, JobPosting, Resume.
//
// Deliberately excluded, and the exclusions carry the reasoning:
//
//   * `Company` and `Skill` have NO delete path and never have. The phase plan
//     called for filtered unique indexes on `companies.Name` and `skills.Name`
//     on the assumption they would become archivable; they did not, because
//     nothing archives them. A filter predicate on an index no row can ever
//     fail is a promise about a code path that does not exist.
//   * Link and child rows (`PostingSkill`, `JobRequirement`, `ResumeSkill`,
//     `ResumeExperience`, `ResumeEducation`) are owned by a parent that is now
//     archivable, and they SURVIVE its archive untouched — that is precisely
//     what makes a restore possible. Their old CASCADE never fires because the
//     parent's DELETE never runs; see the interceptor.
//   * `DocumentImport` is already a receipt with its own lifecycle column
//     (`Status`, including `Discarded`), and DiscardImport.cs argues at length
//     for keeping discarded rows readable. A second, vaguer "archived" beside
//     it would be two answers to one question — the same test IAuditable
//     applies to AiAnalysis and MatchResult.
//   * `AiAnalysis` and `MatchResult` are 1:1 derived records that re-running
//     overwrites. They are deleted in response to their subject being deleted
//     (the 13.3c notifications), and under soft delete their subject is no
//     longer deleted, so nothing deletes them either.
//
// The rule that falls out: a row is soft-deletable when a *user* can end its
// life, and only then.
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }

    // Nullable, and it is the honest shape: a live row has no archive instant.
    // A non-nullable `DateTime` would need a sentinel, and `default` reads as
    // 0001-01-01, which sorts before every real date and is exactly the kind of
    // value a later query trusts.
    DateTime? DeletedAtUtc { get; set; }
}
