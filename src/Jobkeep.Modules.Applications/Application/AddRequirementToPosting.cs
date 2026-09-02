using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: add a structured requirement line to an application's posting.
//
// `job_requirements` has existed in the schema since Phase 2 but nothing could
// write to it — the table was reachable on read and a dead end on write. This
// slice and RemoveRequirement close that, which is the point of Phase 2.1: the
// Postgres decision was justified by a rich relational model, and a model with
// tables you can't populate isn't finished.
//
// Requirements are stored per posting rather than per application, because the
// requirement is a fact about the ad, not about your act of applying to it.

public record AddRequirementToPostingRequest(string Text, RequirementKind Kind, bool IsMustHave);

public record RequirementResponse(Guid Id, string Text, RequirementKind Kind, bool IsMustHave);

public class AddRequirementToPostingHandler
{
    private readonly ApplicationsDbContext _db;

    public AddRequirementToPostingHandler(ApplicationsDbContext db) => _db = db;

    public async Task<SliceResult<RequirementResponse>> HandleAsync(
        Guid applicationId, AddRequirementToPostingRequest request, CancellationToken ct = default)
    {
        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return SliceResult<RequirementResponse>.Invalid("text is required.");

        var postingId = await _db.JobApplications
            .Where(a => a.Id == applicationId)
            .Select(a => a.PostingId)
            .FirstOrDefaultAsync(ct);
        if (postingId == Guid.Empty)
            return SliceResult<RequirementResponse>.NotFound($"Application {applicationId} not found.");

        // No dedup here, unlike skills. A skill is shared reference data with a
        // unique name; a requirement is free text belonging to exactly one
        // posting, so two similar lines are two requirements, not a duplicate.
        var requirement = new JobRequirement
        {
            PostingId = postingId,
            Text = text,
            Kind = request.Kind,
            IsMustHave = request.IsMustHave
        };

        _db.JobRequirements.Add(requirement);
        await _db.SaveChangesAsync(ct);

        return SliceResult<RequirementResponse>.Ok(
            new RequirementResponse(requirement.Id, requirement.Text, requirement.Kind, requirement.IsMustHave));
    }
}
