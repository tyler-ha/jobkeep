using System.Text.Json.Serialization;

namespace Jobkeep.Models;

// Phase 4 output — 1:1 with a posting (facts derived from its description).
// Extracted skills are written to PostingSkill with Source = AiExtracted,
// so they sit alongside human-entered skills instead of in a parallel list.
public class AiAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PostingId { get; set; }
    [JsonIgnore] public JobPosting Posting { get; set; } = null!;   // back-ref

    public SeniorityLevel Seniority { get; set; } = SeniorityLevel.Unknown;
    public string? Summary { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime AnalyzedAtUtc { get; set; } = DateTime.UtcNow;
}
