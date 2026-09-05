using Jobkeep.Api;
using Jobkeep.Persistence;
using Jobkeep.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jobkeep.Tests.Infrastructure;

/// <summary>
/// Boots the real <c>Program.cs</c> in-process against the test container.
///
/// Overrides storage through the *configuration* path rather than by swapping the
/// <c>DbContextOptions</c> descriptors in DI, so Program.cs's own wiring
/// (<c>GetConnectionString("Postgres")</c> → <c>UseNpgsql</c>) stays under test
/// instead of being replaced by the test. Since 13.3b that is six registrations
/// rather than one, which makes the argument stronger: swapping them here would mean
/// re-stating six schema-and-history decisions the app already makes.
/// </summary>
public sealed class JobkeepAppFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development on purpose, for two reasons: it is the only environment in which
        // Program.cs applies EF migrations on startup (so the real migration path is
        // exercised), and it matches how the app is actually run locally.
        builder.UseEnvironment(Environments.Development);

        // Why UseSetting and not ConfigureAppConfiguration:
        //
        // Program.cs reads GetConnectionString("Postgres") off builder.Configuration
        // *before* builder.Build(). ConfigureAppConfiguration callbacks are applied
        // while the host is being built, so they land too late: the app had already
        // resolved the localhost connection string out of appsettings.Development.json
        // and connected to the real dev database. UseSetting writes into the
        // WebApplicationBuilder's configuration directly, so it is visible to that
        // earlier read.
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);

        // PHASE 14 — no seeded vocabulary under test.
        //
        // The seed is real behaviour and it stays on everywhere else; here it
        // fights Respawn. Reset truncates `skills` between tests, the host re-seeds
        // when it boots, and the result is 228 reference rows sitting inside every
        // arrange that has nothing to do with skills — which broke three existing
        // tests the day this landed, one of them subtly: a test seeding "aws" got
        // the seed's "AWS" row back, because the natural key made them one.
        //
        // UseSetting for the same reason as the line above: Program.cs reads this
        // off builder.Configuration before Build().
        builder.UseSetting("Skills:SeedOnStartup", "false");

        // PHASE 6.5 GROUP 6 — no background parsing under test, for the same
        // reason and by the same mechanism.
        //
        // ImportParseWorker sweeps every Parsing row on startup and parses it.
        // Under Respawn that is actively hostile: the sweep races truncation, and
        // a test that uploads a document would have its row structured out from
        // under it by a worker on another thread. Every assertion about an import
        // would then depend on which of the two got there first.
        //
        // The tests drive POST /imports/{id}/reparse explicitly instead, which is
        // exactly what the worker calls — so the slice under test is identical
        // and only the trigger differs. ImportParseWorkerTests turns this back on
        // for itself, because the trigger is the one thing that would otherwise
        // never run.
        builder.UseSetting("Documents:ParseInBackground", "false");

        // PHASE 13.3b — the test-only aggregate context, added to the app's own
        // container rather than to a second one, so tests keep resolving it from a
        // scope exactly as they resolved AppDbContext before.
        //
        // This is ADDITIVE. Nothing the app registers is replaced or removed: the six
        // real contexts are still wired by Program.cs and still the only thing src/
        // can see. TestDbContext exists beside them, and only tests/ can name it.
        //
        // The interceptor is attached deliberately. Program.cs puts it on all six
        // contexts, so a row arranged through this one has to be stamped the same way
        // or the Phase 7 audit tests would be asserting against a different writer
        // than the app is. Resolved from the container rather than constructed, so a
        // test that swaps the clock swaps it here too.
        builder.ConfigureServices(services =>
        {
            services.AddDbContext<TestDbContext>((sp, options) => options
                .UseNpgsql(connectionString)
                .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));

            // PHASE 11.2a — a header-driven authentication scheme, so the suite can
            // satisfy [Authorize] without paying a password hash per test. It
            // forwards to Identity's real cookie whenever the header is absent, so
            // IdentityTests still exercises the genuine article. TestAuthHandler
            // has the full argument.
            //
            // Registered here rather than in Program.cs for the obvious reason: it
            // is a way in, and it must not exist in anything that ships.
            TestAuthHandler.Register(services);

            // PHASE 11.2b — WithDbAsync has no HttpContext, and every row it
            // arranges has to end up owned by the person the HTTP client is, or
            // three hundred existing tests would arrange rows the request under
            // test cannot see. Replacing ICurrentUser is how that is said once
            // rather than in every arrange.
            services.AddScoped<ICurrentUser, TestCurrentUser>();
        });
    }
}

/// <summary>
/// Phase 11.2b — the suite's current user.
/// </summary>
/// <remarks>
/// Defers to the real <see cref="CurrentUser"/> first, so a request carrying an
/// <c>X-Test-User</c> header is that user and the cross-user isolation tests
/// mean something. Falls back to <see cref="TestAuthHandler.DefaultUserId"/>
/// only when there is no principal at all — which is <c>WithDbAsync</c>, and
/// which is the whole reason this exists. A test that needs to arrange somebody
/// else's rows sets <c>OwnerUserId</c> on the entity; the interceptor only
/// stamps a row that does not already name an owner.
/// </remarks>
internal sealed class TestCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private readonly CurrentUser _inner = new(accessor);

    public Guid? UserId
    {
        get => _inner.UserId ?? TestAuthHandler.DefaultUserId;
        set => _inner.UserId = value;
    }
}
