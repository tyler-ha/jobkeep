using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Jobkeep.Tests.Infrastructure;

/// <summary>
/// Boots the real <c>Program.cs</c> in-process against the test container.
///
/// Overrides storage through the *configuration* path rather than by swapping the
/// <c>DbContextOptions&lt;AppDbContext&gt;</c> descriptor in DI, so Program.cs's own
/// wiring (<c>GetConnectionString("Postgres")</c> → <c>UseNpgsql</c>) stays under test
/// instead of being replaced by the test.
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
        // Program.cs reads GetConnectionString("Postgres") off builder.Configuration at
        // line 15 — *before* builder.Build(). ConfigureAppConfiguration callbacks are
        // applied while the host is being built, so they land too late: the app had
        // already resolved the localhost connection string out of
        // appsettings.Development.json and connected to the real dev database.
        // UseSetting writes into the WebApplicationBuilder's configuration directly, so
        // it is visible to that earlier read.
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
    }
}
