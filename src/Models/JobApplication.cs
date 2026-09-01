using System.Text.Json.Serialization;

namespace Jobkeep.Models;

// Aggregate root: YOUR record of applying to a posting. The storage interface
// (IJobApplicationRepository) is still expressed in terms of this type — it now
// simply carries a JobPosting and related data via navigation properties.
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
    // See Models/Resume.cs for the full argument.
    //
    // Nullable, because an application logged by hand does not have to name a
    // resume, and every application that existed before this phase had its text
    // dropped rather than migrated (single-user local database; the column was
    // scaffolding no endpoint had ever meaningfully filled).
    public Guid? ResumeId { get; set; }
    public Resume? Resume { get; set; }

    // The ATS result is your resume vs THIS posting, so it lives on the
    // application rather than on either side of the comparison.
    public AtsResult? AtsResult { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

// Phase 5 output — 1:1 with an application. The keyword lists map to Postgres
// text[] columns via Npgsql (no child tables needed for simple string arrays).
public class AtsResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ApplicationId { get; set; }
    [JsonIgnore] public JobApplication Application { get; set; } = null!;   // back-ref

    // Which resume version this result judged. Added in Phase 5, because this
    // class predates Phase 4.5 and was written when a resume was a string column
    // on the application. Resumes are versioned and labelled now, so a result
    // that does not say which version it read is not a result — you would not
    // know whether the gaps it lists have already been fixed.
    //
    // Nullable only so existing rows survive the migration; every row this phase
    // writes sets it.
    public Guid? ResumeId { get; set; }
    public Resume? Resume { get; set; }

    // Three buckets, not two, and the split is the point: posting_skills.IsRequired
    // already distinguishes a must-have from a nice-to-have, so collapsing both
    // into one list would throw away information Phase 4 paid a model call to get.
    // "Missing a must-have" is a reason not to apply; "missing a nice-to-have" is
    // a line to add to the cover letter.
    public List<string> MatchedKeywords { get; set; } = new();
    public List<string> MissingMustHaveKeywords { get; set; } = new();
    public List<string> MissingNiceToHaveKeywords { get; set; } = new();

    // The model half's output: free-text requirements the resume shows no
    // evidence for. Empty when the model was unreachable — see CheckAts.cs, which
    // degrades rather than failing, because three of its four stages need no model.
    public List<string> UnmetRequirements { get; set; } = new();

    public List<string> FormattingRiskNotes { get; set; } = new();

    // Something imperfect that did not stop the check — overwhelmingly "the model
    // was unreachable, so the free-text requirements were not assessed".
    //
    // Persisted rather than returned only from the POST, and the reason is the
    // failure mode DocumentImport.Warning was added to prevent: without it, a
    // result computed during an outage is stored with an empty UnmetRequirements
    // list, and every later read of it cheerfully reports that the resume meets
    // every written requirement. Same column, same argument, same table shape.
    public string? Warning { get; set; }

    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}
