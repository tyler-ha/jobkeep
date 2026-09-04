using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Documents;
using Jobkeep.Contracts.Shared;
using Jobkeep.SharedKernel;
namespace Jobkeep.Modules.Applications.Domain;

// Aggregate root: YOUR record of applying to a posting.
//
// PHASE 13.3b — MatchResult used to be declared in this file, directly below, and
// it now lives in Jobkeep.Modules.Match. Two classes in one file was fine while
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
    // What replaces it, in two halves. On the WRITE path, a contract check
    // through IResumeContract.GetAsync, which CreateApplication and
    // UpdateApplication already did before this FK was dropped — which is why
    // dropping it changed no behaviour going in. On the DELETE path, 13.3c:
    // DeleteResume asks IApplicationContract.CountApplicationsForResumeAsync and
    // refuses while the answer is not zero.
    //
    // Together they are weaker than the RESTRICT was, and DeleteResume.cs names
    // the gap rather than implying parity: two statements with a gap between
    // them cannot refuse a row created inside that gap, where a foreign key
    // refused inside the transaction. The read path is built for it — the label
    // below comes back null when the résumé is gone, and the id is still
    // returned so a client can tell "no résumé" from "résumé missing".
    public Guid? ResumeId { get; set; }

    // Deliberately no MatchResult navigation since 13.3b. The result is Match' table
    // in Match' schema; the 1:1 is expressed by match_results.ApplicationId and read
    // through the Match module, not by walking a property from here.

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
