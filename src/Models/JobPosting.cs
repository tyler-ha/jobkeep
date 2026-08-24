using System.Text.Json.Serialization;

namespace Jobkeep.Models;

// The external job ad — the thing you found on Indeed/LinkedIn. This is the
// unit the AI analyzer (Phase 4) reads, and AI-derived facts describe it.
public class JobPosting
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

    // Skills the ad asks for, via the shared Skill table (many-to-many).
    // IsRequired on the join row splits must-have vs nice-to-have.
    public List<PostingSkill> PostingSkills { get; set; } = new();
    public List<JobRequirement> Requirements { get; set; } = new();

    // Phase 4 output. Null until analyzed.
    public AiAnalysis? AiAnalysis { get; set; }

    // Back-reference — ignored in REST JSON to avoid cycles.
    [JsonIgnore] public List<JobApplication> Applications { get; set; } = new();
}
