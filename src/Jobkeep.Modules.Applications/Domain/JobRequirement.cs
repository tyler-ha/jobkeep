using System.Text.Json.Serialization;
using Jobkeep.Persistence;
using Jobkeep.SharedKernel;

namespace Jobkeep.Modules.Applications.Domain;

// A structured requirement line from the ad. Kept structured (not just prose)
// so the match check (Phase 5) can reason specifically about must-haves.
public class JobRequirement : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PostingId { get; set; }
    [JsonIgnore] public JobPosting Posting { get; set; } = null!;   // back-ref

    public string Text { get; set; } = string.Empty;
    public RequirementKind Kind { get; set; } = RequirementKind.Qualification;
    public bool IsMustHave { get; set; }

    // Phase 7 — maintained by AuditSaveChangesInterceptor, never by hand.
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

}
