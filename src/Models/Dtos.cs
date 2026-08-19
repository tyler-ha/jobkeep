namespace Jobkeep.Models;

// Create is intentionally small so the API stays easy to call. The richer
// posting fields (skills, requirements) are populated later via dedicated
// GraphQL mutations and the Phase 4 AI analyzer.
public class CreateJobApplicationRequest
{
    public string Company { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;   // renamed from "Role" in Phase 1
    public string? Location { get; set; }
    public string? Description { get; set; }             // the pasted ad text
    public string? SourceUrl { get; set; }
    public string? Notes { get; set; }
    public string? ResumeText { get; set; }
}

// Every field optional — PATCH semantics, only send what changed.
public class UpdateJobApplicationRequest
{
    public string? Company { get; set; }
    public string? Title { get; set; }
    public string? Location { get; set; }
    public ApplicationStatus? Status { get; set; }
    public string? Notes { get; set; }
    public string? Description { get; set; }
    public string? ResumeText { get; set; }
}
