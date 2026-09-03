using Jobkeep.Shared;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: delete one requirement from an application's posting.
//
// The delete is matched on (requirement id AND this application's posting id),
// not on the id alone. A requirement id is a global identifier, so an id-only
// delete would let a caller remove a requirement from a posting they didn't
// address — a horizontal-access bug today, and a cross-tenant one the moment
// owner scoping lands (security-and-data-audit.md F1). Scoping the query to the
// route's parent is the cheap habit that prevents it.
public record RemoveRequirement(
    Guid ApplicationId,
    Guid RequirementId) : IRequest<SliceResult<bool>>;

public class RemoveRequirementHandler : IRequestHandler<RemoveRequirement, SliceResult<bool>>
{
    private readonly ApplicationsDbContext _db;

    public RemoveRequirementHandler(ApplicationsDbContext db) => _db = db;

    public async ValueTask<SliceResult<bool>> Handle(
        RemoveRequirement message, CancellationToken ct)
    {
        var (applicationId, requirementId) = message;
        var postingId = await _db.JobApplications
            .Where(a => a.Id == applicationId)
            .Select(a => a.PostingId)
            .FirstOrDefaultAsync(ct);
        if (postingId == Guid.Empty)
            return SliceResult<bool>.NotFound($"Application {applicationId} not found.");

        var requirement = await _db.JobRequirements
            .FirstOrDefaultAsync(r => r.Id == requirementId && r.PostingId == postingId, ct);
        if (requirement is null)
            return SliceResult<bool>.NotFound(
                $"Requirement {requirementId} not found on application {applicationId}.");

        // A hard delete, consistent with the rest of the app today. Soft delete is
        // step 2 of the remediation plan in security-and-data-audit.md, and it is
        // a schema change, so it is deliberately not smuggled in here.
        _db.JobRequirements.Remove(requirement);
        await _db.SaveChangesAsync(ct);
        return SliceResult<bool>.Ok(true);
    }
}
