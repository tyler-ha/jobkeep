using System.Text.Json.Serialization;

namespace Jobkeep.Models;

// Aggregate root: YOUR record of applying to a posting. The storage interface
// (IJobApplicationRepository) is still expressed in terms of this type — it now
// simply carries a JobPosting and related data via navigation properties.
public class JobApplication
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

    public List<string> MatchedKeywords { get; set; } = new();
    public List<string> MissingMustHaveKeywords { get; set; } = new();
    public List<string> FormattingRiskNotes { get; set; } = new();
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}
