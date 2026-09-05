using Jobkeep.Modules.Match.Domain;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Jobkeep.SharedKernel;

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
public class MatchDbContext : DbContext
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

    public MatchDbContext(DbContextOptions<MatchDbContext> options, ICurrentUser currentUser)
        : base(options) => _ownerId = currentUser.UserId;
    public DbSet<MatchResult> MatchResults => Set<MatchResult>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(MatchDbContext).Assembly);


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
        model.Entity<MatchResult>().HasQueryFilter(
            QueryFilters.Owner, x => x.OwnerUserId == _ownerId);

        // LAST, deliberately — it reads the finished model.
        ModelConventions.ApplyDatabaseDefaults(model);
    }
}
