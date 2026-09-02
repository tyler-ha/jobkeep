using System.Net.Http.Json;
using Jobkeep.Models;
using Jobkeep.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Tests.Persistence;

/// <summary>
/// Phase 7 — the audit-and-integrity baseline, and the case-insensitive natural key.
///
/// Every assertion here is against real Postgres for a reason that is stronger than
/// usual: this phase is almost entirely *database* behaviour. A generated column, a
/// unique index on it, two CHECK constraints, DB-side defaults and an `xmin`
/// concurrency token are all things EF's InMemory provider either ignores or fakes.
/// A fake would pass every test below while the actual schema enforced none of it,
/// which is precisely the failure mode Phase 2.2 chose Testcontainers to avoid.
/// </summary>
public sealed class DataIntegrityTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    // ------------------------------------------------------------------
    // F8 — the audit interceptor
    // ------------------------------------------------------------------

    [Fact]
    public async Task UpdatedAtUtc_IsMaintained_ByAWritePathThatNeverTouchesIt()
    {
        // THIS IS THE REGRESSION GUARD FOR F8, and its shape is the whole point.
        //
        // The finding was not "the column is missing". It was that the column was
        // maintained by hand in one place, so the four write paths Phase 2.1 added
        // each saved without touching it — the column said one thing and the row
        // said another. Asserting through the update slice would prove nothing,
        // because that slice is the one place that always did set it.
        //
        // So this deliberately picks a column NOTHING in C# has ever assigned:
        // job_postings.UpdatedAtUtc did not exist before this phase, and no slice
        // sets it now. PATCHing an application's title reaches through to the
        // posting, and only the interceptor can stamp it. If the interceptor is
        // removed or stops matching IAuditable, this fails.
        //
        // WHAT THIS TEST ALSO PINS, learned by writing it wrong first: the
        // interceptor stamps THE ROW THAT CHANGED, not that row's parent. The
        // first draft added a skill to the posting and expected the posting's
        // timestamp to move; it did not, because inserting a posting_skills row
        // leaves job_postings Unchanged. That is the correct behaviour and it is
        // a deliberate boundary — stamping a parent whenever any descendant
        // changes would need an aggregate definition this codebase has never
        // written down, and it would make "when did this row change" unanswerable.
        // UpdatedAtUtc means this row, not this row and everything under it.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var (created, before) = await WithDbAsync(async db =>
        {
            var p = await db.JobPostings.AsNoTracking()
                .SingleAsync(x => x.Applications.Any(a => a.Id == id), Ct);
            return (p.CreatedAtUtc, p.UpdatedAtUtc);
        });

        // On insert the two are stamped together, so this is the "never modified"
        // state the interceptor promises.
        Assert.Equal(created, before);

        (await Client.PatchAsJsonAsync($"/applications/{id}", new { title = "Staff Backend Engineer" }, Ct))
            .EnsureSuccessStatusCode();

        var (createdAfter, after) = await WithDbAsync(async db =>
        {
            var p = await db.JobPostings.AsNoTracking()
                .SingleAsync(x => x.Applications.Any(a => a.Id == id), Ct);
            return (p.CreatedAtUtc, p.UpdatedAtUtc);
        });

        Assert.True(after > before, "UpdatedAtUtc should have moved — the interceptor did not run.");
        Assert.Equal(created, createdAfter);   // CreatedAtUtc is immutable after insert
    }

    [Fact]
    public async Task CreatedAtUtc_CannotBeRewritten_EvenWhenAssignedDirectly()
    {
        // The interceptor marks CreatedAtUtc unmodified on update rather than
        // trusting callers, so a slice that assigns to it cannot rewrite history.
        // Written as a test because it fails *silently* in the safe direction —
        // nothing throws, the assignment is simply not sent — and a silent
        // safeguard nobody asserts is one somebody deletes as dead code.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var original = await WithDbAsync(db =>
            db.JobApplications.AsNoTracking().Where(a => a.Id == id)
              .Select(a => a.CreatedAtUtc).SingleAsync(Ct));

        await WithDbAsync(async db =>
        {
            var app = await db.JobApplications.SingleAsync(a => a.Id == id, Ct);
            app.CreatedAtUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            app.Notes = "touched";
            await db.SaveChangesAsync(Ct);
        });

        var after = await WithDbAsync(db =>
            db.JobApplications.AsNoTracking().Where(a => a.Id == id)
              .Select(a => a.CreatedAtUtc).SingleAsync(Ct));

        Assert.Equal(original, after);
    }

    // ------------------------------------------------------------------
    // F7 — xmin concurrency token
    // ------------------------------------------------------------------

    [Fact]
    public async Task TwoConcurrentUpdates_ToOneApplication_RaiseAConcurrencyException()
    {
        // Before this the read-modify-write in UpdateApplication was last-write-wins:
        // two PATCHes that both read the same row silently discarded one. `xmin` is
        // Postgres's own row version, so the guard costs zero added columns — the
        // UPDATE simply carries the version it read in its WHERE clause, matches no
        // rows when someone else has written, and EF turns that into an exception.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        // Two independent contexts, because one context's change tracker would
        // hand both "callers" the same tracked instance and there would be
        // nothing to conflict.
        var (scopeA, first) = NewScopedDb();
        var (scopeB, second) = NewScopedDb();
        using var _a = scopeA;
        using var _b = scopeB;

        var a = await first.JobApplications.SingleAsync(x => x.Id == id, Ct);
        var b = await second.JobApplications.SingleAsync(x => x.Id == id, Ct);

        a.Notes = "written by the first caller";
        await first.SaveChangesAsync(Ct);

        b.Notes = "written by the second caller, from a stale read";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    // ------------------------------------------------------------------
    // The case-insensitive natural key
    // ------------------------------------------------------------------

    [Fact]
    public async Task Companies_DifferingOnlyInCase_AreOneRow()
    {
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("canva", "Platform Engineer", Ct);

        var companies = await WithDbAsync(db => db.Companies.ToListAsync(Ct));
        var postings = await WithDbAsync(db => db.JobPostings.CountAsync(Ct));

        var only = Assert.Single(companies);
        Assert.Equal("Canva", only.Name);              // first spelling wins
        Assert.Equal("canva", only.NameNormalized);    // Postgres computed this
        Assert.Equal(2, postings);                     // both postings share the row
    }

    [Fact]
    public async Task Resumes_DifferingOnlyInCase_Collide_RatherThanBecomingTwoRows()
    {
        // Résumés are the one of the three that is NOT merged: a résumé is a
        // document with its own skills and history, so two files labelled
        // "Backend" and "backend" are two documents and collapsing them would
        // destroy content. What the natural key buys here is that the SECOND one
        // is refused at import time, loudly, instead of quietly becoming a
        // near-duplicate the user later cannot tell apart.
        await WithDbAsync(async db =>
        {
            db.Resumes.Add(new Resume { Label = "Backend" });
            await db.SaveChangesAsync(Ct);
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => WithDbAsync(async db =>
        {
            db.Resumes.Add(new Resume { Label = "backend" });
            await db.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task NormalizedColumn_IsMaintainedByPostgres_NotByTheApplication()
    {
        // The generated column is the reason no writer can forget the natural key.
        // Renaming a company through EF must move NameNormalized too, without any
        // C# assigning to it — that property has a private setter precisely so it
        // cannot be assigned.
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        await WithDbAsync(async db =>
        {
            var c = await db.Companies.SingleAsync(Ct);
            c.Name = "REA Group";
            await db.SaveChangesAsync(Ct);
        });

        var normalized = await WithDbAsync(db =>
            db.Companies.AsNoTracking().Select(c => c.NameNormalized).SingleAsync(Ct));

        Assert.Equal("rea group", normalized);
    }

    // ------------------------------------------------------------------
    // F12 — CHECK constraints
    // ------------------------------------------------------------------

    [Fact]
    public async Task SalaryMin_AboveSalaryMax_IsRefusedByTheDatabase()
    {
        // Enforced in the schema rather than the handler on purpose: a rule that
        // only exists in C# is one any other writer can ignore. Asserted at the
        // DbContext level for the same reason — going through the API would prove
        // the handler validates, not that the database does.
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => WithDbAsync(async db =>
        {
            var posting = await db.JobPostings.SingleAsync(Ct);
            posting.SalaryMin = 200_000m;
            posting.SalaryMax = 100_000m;
            await db.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task SalaryCurrency_ThatIsNotISO4217_IsRefusedByTheDatabase()
    {
        // varchar(3) accepted "XX!" before this. Three characters is not three
        // *letters*, and the column's whole job is to name a currency.
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => WithDbAsync(async db =>
        {
            var posting = await db.JobPostings.SingleAsync(Ct);
            posting.SalaryCurrency = "XX!";
            await db.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task ASalaryRangeThatIsTheRightWayRound_IsAccepted()
    {
        // The constraint has to admit the ordinary case, including the nulls —
        // both bounds are optional, and `NULL <= NULL` is not true, so a naive
        // constraint would have refused every posting with no salary on it.
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        await WithDbAsync(async db =>
        {
            var posting = await db.JobPostings.SingleAsync(Ct);
            posting.SalaryMin = 140_000m;
            posting.SalaryMax = 175_000m;
            posting.SalaryCurrency = "AUD";
            await db.SaveChangesAsync(Ct);
        });

        var max = await WithDbAsync(db =>
            db.JobPostings.AsNoTracking().Select(p => p.SalaryMax).SingleAsync(Ct));

        Assert.Equal(175_000m, max);
    }

    // ------------------------------------------------------------------
    // F11 — database-side defaults
    // ------------------------------------------------------------------

    [Fact]
    public async Task AWriterThatIsNotEfCore_StillGetsAnIdAndTimestamps()
    {
        // The argument for F11 in one test. Every id and timestamp used to come
        // from a C# property initialiser, so a `psql` fix or a migration backfill
        // could insert a row that violated the schema's own invariants without the
        // schema noticing. This INSERT names neither the id nor the timestamps —
        // exactly what a non-EF writer would do — and Postgres must fill all three.
        await ExecuteAsync(
            "INSERT INTO applications.\"companies\" (\"Name\") VALUES ('Written By Raw Sql')");

        var row = await WithDbAsync(db => db.Companies.AsNoTracking()
            .SingleAsync(c => c.Name == "Written By Raw Sql", Ct));

        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.NotEqual(default, row.CreatedAtUtc);
        Assert.NotEqual(default, row.UpdatedAtUtc);
        Assert.Equal("written by raw sql", row.NameNormalized);
    }
}
