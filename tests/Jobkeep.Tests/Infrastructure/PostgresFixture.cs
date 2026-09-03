using Jobkeep.Modules.Applications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;

namespace Jobkeep.Tests.Infrastructure;

/// <summary>
/// Owns the throwaway Postgres container and the app host for the whole test run.
///
/// Built once per collection, not once per test class: standing up a container plus a
/// WebApplicationFactory is the expensive part, and reusing it is the single biggest
/// lever on suite duration. Per-test isolation comes from <see cref="ResetAsync"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Same image as the dev container in the README, so tests exercise the Postgres
    // version the app is actually developed against — not "some Postgres".
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("jobkeep_test")
        .Build();

    /// <summary>
    /// The five schemas that hold tables, in the order Program.cs migrates them.
    ///
    /// <para>
    /// Analytics is absent because it owns nothing: its three views live in
    /// `applications`, which is already here. A schema listed but empty would be
    /// harmless; a schema MISSING is the failure this list exists to prevent, so it
    /// is kept beside the Respawner that reads it rather than derived from the
    /// module assemblies. Deriving it would be cleverer and would fail the same way,
    /// quietly, if a module were ever added without a schema.
    /// </para>
    /// </summary>
    public static readonly string[] ModuleSchemas =
        ["applications", "skills", "documents", "ai", "ats"];

    private Respawner? _respawner;
    private NpgsqlConnection? _connection;

    public string ConnectionString => _container.GetConnectionString();

    public JobkeepAppFactory App { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // Building the host boots Program.cs, which in Development applies EF
        // migrations. So "the migrations apply cleanly to an empty database" is a
        // property of every run getting this far, rather than a separate test.
        App = new JobkeepAppFactory(ConnectionString);
        _ = App.Services;

        _connection = new NpgsqlConnection(ConnectionString);
        await _connection.OpenAsync();

        AssertConnectionPointsAtTheContainer();

        _respawner = await Respawner.CreateAsync(_connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,

            // PHASE 13.3b. This said `["public"]` with one unqualified
            // __EFMigrationsHistory ignored, and both halves had to change on the
            // same day the schemas split. Getting it wrong is SILENT: Respawn
            // would find no tables in `public`, truncate nothing, and every test
            // would inherit the previous test's rows. That fails as unexplained
            // cross-test flakiness rather than as an error, which is the
            // expensive kind, so ResetAsync is asserted directly in
            // RespawnTests rather than trusted.
            SchemasToInclude = ModuleSchemas,

            // Six histories now, one per table-owning context plus none for
            // Analytics — and each has to be named WITH its schema, because an
            // unqualified Table() means the default schema and would leave the
            // other five to be truncated. Wiping any of them makes EF think that
            // module is unmigrated.
            TablesToIgnore = [.. ModuleSchemas.Select(s => new Table(s, "__EFMigrationsHistory"))],
        });
    }

    /// <summary>
    /// Truncates every table so each test starts from empty. Cheaper than recreating
    /// the database, and it leaves the migrated schema in place.
    /// </summary>
    public async Task ResetAsync()
    {
        if (_respawner is not null && _connection is not null)
        {
            await _respawner.ResetAsync(_connection);
        }
    }

    /// <summary>
    /// Guard rail, deliberately loud.
    ///
    /// appsettings.Development.json points at localhost:5432 — the developer's real dev
    /// database, with real application history in it. The factory overrides that with the
    /// container's connection string, but if the override ever silently stops taking
    /// effect (a config-precedence change, a renamed key), Respawn would truncate the
    /// live dev data instead. Cheap to check, expensive to miss.
    ///
    /// This asks the *DbContext* what it is connected to, not IConfiguration. That
    /// distinction is not academic: the first version of this guard read IConfiguration
    /// and passed, while the app was in fact talking to the dev database — the host-level
    /// configuration carried the override even though Program.cs had already read the
    /// pre-Build value. Only the DbContext knows where writes actually go.
    /// </summary>
    private void AssertConnectionPointsAtTheContainer()
    {
        using var scope = App.Services.CreateScope();
        var resolved = scope.ServiceProvider
            .GetRequiredService<ApplicationsDbContext>()
            .Database.GetConnectionString();

        var expected = new NpgsqlConnectionStringBuilder(ConnectionString);
        var actual = new NpgsqlConnectionStringBuilder(resolved);

        if (actual.Port != expected.Port || actual.Database != expected.Database)
        {
            throw new InvalidOperationException(
                $"Refusing to run: the app resolved Postgres at {actual.Host}:{actual.Port}/{actual.Database}, " +
                $"but the test container is {expected.Host}:{expected.Port}/{expected.Database}. " +
                "The connection-string override is not taking effect, and running Respawn now " +
                "would truncate whatever database that actually is.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (App is not null)
        {
            await App.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

/// <summary>
/// One collection for the whole suite, so every test class shares the single container
/// and app host. Tests in a collection run sequentially, which is what makes
/// truncate-between-tests safe.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
