using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Data;

// One table, and the module that reads the most tables it does not own — five of
// them before 13.2, all now behind contracts. See IApplicationsDbContext for why
// these six interfaces live here.
public interface IAtsDbContext
{
    DbSet<AtsResult> AtsResults { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
