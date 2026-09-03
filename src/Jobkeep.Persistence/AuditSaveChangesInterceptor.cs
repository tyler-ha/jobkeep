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
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    // Injectable so a test can control "now" rather than sleeping to make two
    // timestamps differ. Defaults to the real clock in production.
    private readonly Func<DateTime> _now;

    public AuditSaveChangesInterceptor(Func<DateTime>? now = null)
        => _now = now ?? (() => DateTime.UtcNow);

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
