using Jobkeep.Modules.Ai.Domain;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Jobkeep.SharedKernel;

namespace Jobkeep.Modules.Ai;

// One table, in the `ai` schema since 13.3b. Ai owns `ai_analyses`, not the
// technology — IChatClient is a shared dependency any module may inject, the way
// AppDbContext used to be (decision 16). See ApplicationsDbContext for why the
// six 13.2 interfaces became six real contexts.
public class AiDbContext : DbContext
{
    // PHASE 11.2b — the owner of everything this context will read or write.
    //
    // CAPTURED ONCE, IN THE CONSTRUCTOR, and that is the documented EF shape for
    // a tenant filter rather than a shortcut: the MODEL is cached per context
    // type, so a filter that closed over anything but a field of the executing
    // context instance would bake the first request's user into every later
    // one's queries. EF re-roots `_ownerId` onto whichever context is running
    // the query, which is exactly the indirection needed and the only one that
    // is safe.
    //
    // Null means nobody, and `OwnerUserId == null` is NULL in SQL, so an
    // unauthenticated or background scope sees no rows rather than all of them.
    // ImportParseWorker is the one caller that legitimately has no principal;
    // it assigns ICurrentUser.UserId from the row it is about to work on BEFORE
    // it resolves a context, so by the time this constructor runs the value is
    // the real owner's.
    private readonly Guid? _ownerId;

    public AiDbContext(DbContextOptions<AiDbContext> options, ICurrentUser currentUser)
        : base(options) => _ownerId = currentUser.UserId;
    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(AiDbContext).Assembly);


        // -------------------------------------------------------------------
        // PHASE 11.2b — the owner filter, applied HERE and not in the entity
        // configurations
        // -------------------------------------------------------------------
        // The soft-delete filter lives with its entity, because it is a fact
        // about the entity. This one is a fact about the CONTEXT — it needs
        // `_ownerId`, and an IEntityTypeConfiguration cannot reach the context
        // that will run the query. Naming both filters (QueryFilters.SoftDelete,
        // QueryFilters.Owner) is what keeps them independent: the five callers
        // that want to see archived rows drop one filter by name and keep this
        // one, where the old unnamed `IgnoreQueryFilters()` would have dropped
        // both and handed them somebody else's data.
        //
        // Listed one entity at a time rather than reflected over IOwned, for the
        // same reason ISoftDeletable states its three filters explicitly: a
        // generic version would have to build the expression tree by hand, and
        // an unreadable line is a worse guard than a readable one.
        model.Entity<AiAnalysis>().HasQueryFilter(
            QueryFilters.Owner, x => x.OwnerUserId == _ownerId);

        // LAST, deliberately — it reads the finished model.
        ModelConventions.ApplyDatabaseDefaults(model);
    }
}
