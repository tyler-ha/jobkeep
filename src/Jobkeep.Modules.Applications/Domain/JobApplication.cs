namespace Jobkeep.Models;

// Aggregate root: YOUR record of applying to a posting.
//
// PHASE 13.3b — AtsResult used to be declared in this file, directly below, and
// it now lives in Jobkeep.Modules.Ats. Two classes in one file was fine while
// one project held every entity; it is not fine when the file has to be in two
// assemblies at once.
public class JobApplication : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PostingId { get; set; }
    public JobPosting Posting { get; set; } = null!;

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Applied;
    public DateOnly DateApplied { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public string? Notes { get; set; }

    // Which resume version you sent. Phase 4.5 replaced the old `ResumeText`
    // column with this FK: the text used to be duplicated onto every application
    // that shared a resume, and there was nowhere for the parsed records to live.
    // See Documents' Resume.cs for the full argument.
    //
    // Nullable, because an application logged by hand does not have to name a
    // resume, and every application that existed before this phase had its text
    // dropped rather than migrated (single-user local database; the column was
    // scaffolding no endpoint had ever meaningfully filled).
    //
    // PHASE 13.3b CUT THE NAVIGATION AND THE FOREIGN KEY. `resumes` is Documents'
    // table, in Documents' schema, and this id is now an ordinary Guid column
    // that Postgres does not check. That is the real price of a service boundary
    // and it is worth naming: the RESTRICT that used to stop you deleting a
    // resume you had applied with is gone, so nothing at the database level now
    // prevents this column pointing at a row that no longer exists.
    //
    // What replaces it: a contract check at write, through
    // IResumeContract.GetAsync, which CreateApplication and UpdateApplication
    // already do — they were validating the id before this FK was dropped, and
    // that is why dropping it changes no behaviour on the write path. The DELETE
    // side is 13.3c's work.
    public Guid? ResumeId { get; set; }

    // Deliberately no AtsResult navigation since 13.3b. The result is Ats' table
    // in Ats' schema; the 1:1 is expressed by ats_results.ApplicationId and read
    // through the Ats module, not by walking a property from here.

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
