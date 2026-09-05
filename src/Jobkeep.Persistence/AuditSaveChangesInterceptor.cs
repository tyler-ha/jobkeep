using Jobkeep.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jobkeep.Persistence;

// PHASE 13.3a: moved here from Jobkeep.Infrastructure.Data, which is deleted in
// 13.3b. It depends on IAuditable and nothing else of ours, so it belongs with
// the other rules that are true of every context rather than with any one of
// them.

// Phase 7 — the fix for audit finding F8.
//
// One write path for the audit timestamps, so a second mutation method cannot
// silently skip them. Every `SaveChanges` on this context passes through here,
// whichever slice called it, so "did you remember to touch UpdatedAtUtc" stops
// being a question anyone has to answer.
//
// WHY AN INTERCEPTOR RATHER THAN AN OVERRIDE OF SaveChangesAsync
// ---------------------------------------------------------------
// Overriding the method on AppDbContext would work identically today and is
// slightly less code. It was not taken because it puts behaviour in the class
// whose stated job is *"all relational mapping lives here"* — the schema, in one
// readable place. An interceptor is registered in `Program.cs` beside the other
// wiring, is visible in the DI container, and can be left off in a test that
// wants to write a specific timestamp. That last property is not hypothetical:
// the test proving this interceptor works has to be able to create a row with a
// known `UpdatedAtUtc` and then observe it change.
//
// WHY BOTH THE SYNC AND ASYNC OVERRIDES
// -------------------------------------
// The app only ever calls `SaveChangesAsync`, so the sync override looks like
// dead code. It is not: EF calls whichever the caller used, and a future
// `SaveChanges()` — in a seeder, a migration helper, or a test — would silently
// bypass an async-only interceptor and reintroduce F8 in a new place. Covering
// both is two lines and removes the trap.
//
// PHASE 8 ADDED A SECOND JOB, and the name still fits: soft delete is stamping a
// lifecycle column (`DeletedAtUtc`) on the way to the database, which is what
// this class already existed to do. It is not a separate interceptor because the
// two have a required ORDER — an archive must be converted to a modification
// before the audit loop sees it — and expressing that as registration order in
// Program.cs would be a rule enforced by a line nobody would think to protect.
// PHASE 11.2b ADDED A THIRD JOB — stamping IOwned.OwnerUserId — and with it a
// dependency on who is asking, so this is SCOPED now rather than singleton. The
// old comment argued singleton from "it holds a clock and nothing else"; that
// stopped being true, and a captive scoped dependency inside a singleton is the
// unsafe direction. AddDbContext resolves it from the scoped provider either
// way, so nothing at the call site changed.
//
// Owner belongs here for the same reason CreatedAtUtc does: it is one write
// path that no slice can forget, and it is the only writer of the column. A
// client cannot set it — nothing binds it from a request — and a slice that
// assigned it by hand would be overwritten on the way out.
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    // Injectable so a test can control "now" rather than sleeping to make two
    // timestamps differ. Defaults to the real clock in production.
    private readonly Func<DateTime> _now;
    private readonly ICurrentUser _currentUser;

    public AuditSaveChangesInterceptor(ICurrentUser currentUser, Func<DateTime>? now = null)
    {
        _currentUser = currentUser;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null) return;

        var now = _now();

        // PHASE 8 — soft delete, and it runs FIRST on purpose.
        //
        // Turning a Deleted entry into a Modified one before the audit loop below
        // means an archive is stamped with `UpdatedAtUtc` like any other change,
        // by the code that already does that, rather than by a second rule that
        // could drift from it. Reversing the two would leave every archived row
        // claiming it was last modified whenever it was last *edited*.
        //
        // WHY HERE AND NOT IN THE THREE SLICES
        // ------------------------------------
        // A slice could set the two columns itself and never call Remove(). That
        // is the version where the fourth soft-deletable entity, added in a year,
        // hard-deletes because someone wrote the obvious thing. Converting the
        // state centrally means `Remove()` keeps meaning "end this row's life"
        // and the *storage* decision about what that costs lives in one place —
        // the same argument F8 made for not maintaining UpdatedAtUtc by hand.
        //
        // WHAT THIS QUIETLY DOES TO CASCADES, WHICH IS THE POINT
        // ------------------------------------------------------
        // The DELETE never reaches Postgres, so the ON DELETE CASCADEs beneath an
        // archived parent never fire: posting_skills, job_requirements,
        // resume_skills, resume_experiences and resume_educations all survive.
        // That is what makes a restore a restore rather than a re-import. EF only
        // cascades to entities it has LOADED, and none of the three delete slices
        // load their children, so nothing is left mislabelled in the tracker
        // either.
        //
        // The residue, stated rather than discovered later: an archived row still
        // occupies its unique index. `resumes.LabelNormalized` is therefore a
        // FILTERED unique index (see ResumeConfiguration), and RestoreResume has
        // to check for a live row that took the label in the meantime.
        foreach (var entry in context.ChangeTracker.Entries<ISoftDeletable>())
        {
            if (entry.State is not EntityState.Deleted) continue;

            entry.State = EntityState.Modified;
            entry.Entity.IsDeleted = true;
            entry.Entity.DeletedAtUtc = now;
        }

        // PHASE 11.2b — the owner, on insert only.
        //
        // ONLY WHEN THE ROW DOES NOT ALREADY NAME ONE. Two callers rely on that:
        // a test arranging a second user's rows, and any future importer acting
        // on someone's behalf. Neither can be served by "always overwrite", and
        // a row that already names an owner is never a row this scope invented.
        //
        // A NULL CURRENT USER THROWS RATHER THAN WRITING Guid.Empty, and the
        // difference matters more than it looks. Writing the empty Guid succeeds:
        // the row is committed, the owner filter then matches it for nobody, and
        // the user gets a 201 followed by an empty list with nothing in the logs.
        // A save that quietly produces unreachable data is worse than a 500.
        //
        // The realistic trigger is not an anonymous request — 11.2a answers those
        // with a 401 long before here — it is the id claim moving: a bearer/JWT
        // setup where `sub` is not inbound-mapped, or IdentityOptions'
        // UserIdClaimType changed. Both would make CurrentUser return null for a
        // perfectly authenticated caller, and this is the line that says so.
        //
        // ImportParseWorker is the one legitimate principal-less writer and it
        // assigns the owner before resolving a context, so it never reaches this.
        foreach (var entry in context.ChangeTracker.Entries<IOwned>())
        {
            if (entry.State is not EntityState.Added) continue;
            if (entry.Entity.OwnerUserId != Guid.Empty) continue;

            entry.Entity.OwnerUserId = _currentUser.UserId
                ?? throw new InvalidOperationException(
                    $"Cannot insert {entry.Entity.GetType().Name}: there is no current user to own "
                    + "it. Either the request is unauthenticated (which authorization should have "
                    + "refused) or the user-id claim is not where CurrentUser looks for it.");
        }

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    // Both stamped on insert, so `CreatedAtUtc == UpdatedAtUtc`
                    // is a reliable test for "never modified". The property
                    // initialisers on the models already set a value; this
                    // overwrites it so every row in one SaveChanges shares one
                    // instant rather than drifting by however long the graph
                    // took to build.
                    entry.Entity.CreatedAtUtc = now;
                    entry.Entity.UpdatedAtUtc = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = now;

                    // CreatedAtUtc is immutable after insert. Marking it unmodified
                    // rather than trusting callers means a slice that loads a row,
                    // assigns to CreatedAtUtc and saves cannot rewrite history --
                    // the assignment is simply not sent. Cheaper than a database
                    // trigger and it fails silently in the safe direction.
                    entry.Property(e => e.CreatedAtUtc).IsModified = false;
                    break;
            }
        }
    }
}
