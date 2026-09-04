using Jobkeep.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Identity;

// The SIXTH context, in its own `identity` schema with its own
// __EFMigrationsHistory. Same shape as the other five (13.3b), so the module can
// be lifted out with its schema whole.
//
// It derives from the package's IdentityDbContext, which is why the base type is
// written out in full: the two share a short name, and the repo's convention is
// <Module>DbContext for every context. Fully qualifying the base once is cheaper
// than being the one module whose context is named differently.
//
// SEVEN TABLES: users, roles, user-roles, user-claims, role-claims, user-logins,
// user-tokens. That is the trade the user accepted on 2026-09-04 for a tool with
// one user, and phase-11-auth.md records it as the one place in this repo where
// the ponytail ladder was overruled on purpose. IdentityUserContext would give
// four by dropping roles — deliberately not taken, because roles are what an
// interviewer asks about next and adding them later is a migration on a table
// that already has rows.
public class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityDbContext<
        JobkeepUser, IdentityRole<Guid>, Guid>(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        // The base builds all seven entity types. Call it FIRST — everything
        // below edits what it produced.
        base.OnModelCreating(model);

        // One schema, seven tables, and the platform's own table names kept.
        //
        // The names look out of place beside `job_applications` and
        // `match_results`, and they stay anyway. They are the names every
        // ASP.NET Core Identity tutorial, sample and diagnostic tool uses, so a
        // reviewer opening this database recognises what it is in one glance —
        // and renaming them buys cosmetic consistency at the price of a schema
        // that no longer looks like the thing it actually is. The schema
        // qualifier is what does the real separating.
        model.HasDefaultSchema("identity");

        // ModelConventions.ApplyDatabaseDefaults IS DELIBERATELY NOT CALLED, and
        // this is the one place in the repo that skips it.
        //
        // It exists to put a floor under writers that are not EF: gen_random_uuid()
        // on Guid primary keys, now() on the audit pair. Neither applies here.
        // None of Identity's types is IAuditable, so there is no audit pair; and
        // three of the seven have COMPOSITE keys made of foreign keys —
        // AspNetUserRoles is (UserId, RoleId), and both are Guid primary-key
        // properties by that convention's test. Defaulting those to a random uuid
        // would put a default on a foreign key column, where the only row it can
        // ever produce is one that fails the constraint. A default that can only
        // generate an error is worse than no default.
    }
}
