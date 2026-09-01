using Jobkeep.Data;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// PHASE 13.2: the interface and its DTOs live in Jobkeep.Contracts; the
// implementation stays here, with the module that owns the tables it guards.

public class ApplicationContract : IApplicationContract
{
    private readonly IApplicationsDbContext _db;

    public ApplicationContract(IApplicationsDbContext db) => _db = db;

    public async Task<Guid?> GetPostingIdAsync(Guid applicationId, CancellationToken ct = default)
        // Nullable-projected rather than selecting Guid and comparing to
        // default: a row whose PostingId happened to be Guid.Empty would be
        // indistinguishable from "no such application" otherwise. FirstOrDefault
        // over Guid? gives null for the missing row and a real value for the
        // found one, which is the distinction the caller is asking about.
        => await _db.JobApplications
            .AsNoTracking()
            .Where(a => a.Id == applicationId)
            .Select(a => (Guid?)a.PostingId)
            .FirstOrDefaultAsync(ct);
}
