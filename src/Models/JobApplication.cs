namespace Jobkeep.Models;

// This shape mirrors what we'll later store in DynamoDB, so moving
// to AWS in Phase 2 won't require rethinking the data model.
public class JobApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Company { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Applied;
    public DateOnly DateApplied { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public string? Notes { get; set; }
    public string? JobDescription { get; set; }

    // Filled in later by Phase 4 (AI analyzer)
    public List<string> AiExtractedSkills { get; set; } = new();
}

public enum ApplicationStatus
{
    Applied,
    Interviewing,
    Offer,
    Rejected,
    Withdrawn
}

// Used for PATCH requests — every field optional so you only send what changed
public class UpdateJobApplicationRequest
{
    public string? Company { get; set; }
    public string? Role { get; set; }
    public ApplicationStatus? Status { get; set; }
    public string? Notes { get; set; }
    public string? JobDescription { get; set; }
}

public class CreateJobApplicationRequest
{
    public string Company { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? JobDescription { get; set; }
}
