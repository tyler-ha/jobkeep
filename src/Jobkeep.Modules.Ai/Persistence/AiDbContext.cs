using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Ai;

// One table, in the `ai` schema since 13.3b. Ai owns `ai_analyses`, not the
// technology — IChatClient is a shared dependency any module may inject, the way
// AppDbContext used to be (decision 16). See ApplicationsDbContext for why the
// six 13.2 interfaces became six real contexts.
public class AiDbContext(DbContextOptions<AiDbContext> options)
    : DbContext(options)
{
    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(AiDbContext).Assembly);

        // LAST, deliberately — it reads the finished model.
        ModelConventions.ApplyDatabaseDefaults(model);
    }
}
