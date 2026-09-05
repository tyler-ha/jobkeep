using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Jobkeep.SharedKernel;

// Deliberately the module's own namespace and not Jobkeep.Modules.Applications
// .Persistence: every handler in this module is in this namespace, so the type
// arrives without a using and the 13.2 -> 13.3b edit at each call site is the
// deletion of a single leading letter.
namespace Jobkeep.Modules.Applications;

// ---------------------------------------------------------------------------
// PHASE 13.3b — one context per module, replacing the six interfaces
// ---------------------------------------------------------------------------
// 13.2 gave each module an I<X>DbContext exposing only its own DbSets, all six
// implemented by one AppDbContext and all six resolving the same scoped
// instance. That bought the property the phase needed at the time — a handler
// physically cannot name another module's table — while nothing moved in
// Postgres, so the logical decoupling landed without a behaviour change.
//
// This replaces it, and the interfaces are gone rather than kept: with a real
// per-module context the property is structural instead of declared. There is no
// `Skills` property here to hide, because this class does not map `skills` at
// all. An interface in front of that adds an indirection with no reader.
//
// What genuinely changed, and it is the thing to remember about 13.3b: THESE ARE
// NOW SIX DIFFERENT CONTEXTS. Two of them in one handler is two change trackers
// and two transactions, where before it was one of each. Nothing in this module
// holds two, but ISkillCatalog.FindOrCreateAsync is called from four modules and
// its "call me before adding your own rows" comment stopped being precautionary
// the moment this file existed.
//
// ApplyConfigurationsFromAssembly scans THIS assembly, so this context maps
// exactly the tables this module owns — correct by construction rather than by
// review. Every configuration names the `applications` schema in its ToTable,
// which is what lets the test-only aggregate context apply all six modules'
// configurations at once and still be right.
public class ApplicationsDbContext : DbContext
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

    public ApplicationsDbContext(DbContextOptions<ApplicationsDbContext> options, ICurrentUser currentUser)
        : base(options) => _ownerId = currentUser.UserId;
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<PostingSkill> PostingSkills => Set<PostingSkill>();
    public DbSet<JobRequirement> JobRequirements => Set<JobRequirement>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();

    // `Skills` is NOT here, and never was in the interface either. Skill rows are
    // shared vocabulary — posting_skills and resume_skills point at the same row
    // — so they are reached through ISkillCatalog by every module including this
    // one. Since 13.3b they are also a different schema.
    //
    // The three published VIEWS are not here either. Applications defines them
    // (the SQL is in this module's initial migration) and Analytics reads them;
    // nothing in this module queries them, and mapping a view you never read
    // would put it in this context's model for no reason.

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(ApplicationsDbContext).Assembly);


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
        // an unreadable line is a worse guard than 3 readable ones.
        model.Entity<Company>().HasQueryFilter(
            QueryFilters.Owner, x => x.OwnerUserId == _ownerId);
        model.Entity<JobPosting>().HasQueryFilter(
            QueryFilters.Owner, x => x.OwnerUserId == _ownerId);
        model.Entity<JobApplication>().HasQueryFilter(
            QueryFilters.Owner, x => x.OwnerUserId == _ownerId);

        // LAST, deliberately. It reads the finished model, so anything
        // configured after it would silently miss the defaults.
        ModelConventions.ApplyDatabaseDefaults(model);
    }
}
