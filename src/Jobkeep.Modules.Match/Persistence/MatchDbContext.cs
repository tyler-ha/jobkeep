using Jobkeep.Modules.Match.Domain;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Match;

// One table, in the `ats` schema since 13.3b — and the module that used to read
// the most tables it did not own, five of them, all now contract calls. See
// ApplicationsDbContext for why the six 13.2 interfaces became six real contexts.
//
// 13.2e's ordering rule survives the split unchanged and is worth restating here
// because this is where it is now enforced by physics rather than by care: every
// contract call RunMatchCheck makes is a READ, and all of them happen before the first
// row reaches this context's change tracker. So there is no partial write to
// report even though a save on this context can no longer roll back anything
// another module did.
public class MatchDbContext(DbContextOptions<MatchDbContext> options)
    : DbContext(options)
{
    public DbSet<MatchResult> MatchResults => Set<MatchResult>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(MatchDbContext).Assembly);

        // LAST, deliberately — it reads the finished model.
        ModelConventions.ApplyDatabaseDefaults(model);
    }
}
