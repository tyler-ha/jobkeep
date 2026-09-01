using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Data;

// One table. Ai owns `ai_analyses`, not the technology — IChatClient is a shared
// dependency any module may inject, the way AppDbContext used to be (decision
// 16). See IApplicationsDbContext for why these six interfaces live here.
public interface IAiDbContext
{
    DbSet<AiAnalysis> AiAnalyses { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
