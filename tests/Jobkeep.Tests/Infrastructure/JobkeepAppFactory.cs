using Jobkeep.Persistence;
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
        builder.ConfigureServices(services => services.AddDbContext<TestDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>())));
    }
}
