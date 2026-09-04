using System.Text.Json.Serialization;
using Jobkeep.SharedKernel;

namespace Jobkeep.Modules.Applications.Domain;

// The external job ad — the thing you found on Indeed/LinkedIn. This is the
// unit the AI analyzer (Phase 4) reads, and AI-derived facts describe it.
public class JobPosting : IAuditable, ISoftDeletable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public string? Location { get; set; }
    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;

    // Salary as flat columns (no separate table) — simple and sufficient here.
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string SalaryCurrency { get; set; } = "AUD";
    public SalaryPeriod SalaryPeriod { get; set; } = SalaryPeriod.Year;

    public string? Description { get; set; }     // raw pasted ad text
    public DateOnly? PostedDate { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Phase 7 — F8: PATCH mutates Title, Location, Description and CompanyId,
    // and until now nothing recorded that it had.
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // Phase 8. An archived ad keeps its skills and its requirements — the DELETE
    // that used to cascade into both never runs now, which is what makes the
    // restore below a restore and not a re-import.
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }

    // Skills the ad asks for, via the shared Skill table (many-to-many).
    // IsRequired on the join row splits must-have vs nice-to-have.
    public List<PostingSkill> PostingSkills { get; set; } = new();
    public List<JobRequirement> Requirements { get; set; } = new();

    // Phase 4's analysis used to hang here as `AiAnalysis? AiAnalysis`. 13.3b cut
    // it: `ai_analyses` is the Ai module's table in the Ai module's schema, and
    // the 1:1 lives on that side as ai_analyses.PostingId. Nothing in
    // Applications read the property — Ai's own slices always started from the
    // analysis — so this is the cheapest of the five cuts.

    // Back-reference — ignored in REST JSON to avoid cycles.
    [JsonIgnore] public List<JobApplication> Applications { get; set; } = new();
}
