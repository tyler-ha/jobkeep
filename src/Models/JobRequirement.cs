using System.Text.Json.Serialization;

namespace Jobkeep.Models;

// A structured requirement line from the ad. Kept structured (not just prose)
// so the ATS check (Phase 5) can reason specifically about must-haves.
public class JobRequirement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PostingId { get; set; }
    [JsonIgnore] public JobPosting Posting { get; set; } = null!;   // back-ref

    public string Text { get; set; } = string.Empty;
    public RequirementKind Kind { get; set; } = RequirementKind.Qualification;
    public bool IsMustHave { get; set; }
}
