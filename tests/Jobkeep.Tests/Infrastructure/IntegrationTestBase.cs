using System.Text.Json;
using System.Text.Json.Serialization;
using Jobkeep.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Infrastructure;

/// <summary>
/// Base class for every integration test: a clean database, an HttpClient against the
/// real app, a GraphQL helper, and scoped AppDbContext access for arranging state and
/// asserting on what actually landed in Postgres.
/// </summary>
[Collection(IntegrationCollection.Name)]
public abstract class IntegrationTestBase(PostgresFixture fixture) : IAsyncLifetime
{
    protected PostgresFixture Fixture { get; } = fixture;

    protected HttpClient Client { get; private set; } = null!;

    protected GraphQLClient GraphQL { get; private set; } = null!;

    /// <summary>
    /// The running test's cancellation token. xUnit v3 surfaces one per test; passing it
    /// to every awaited call is what the xUnit1051 analyzer asks for, and it means a
    /// cancelled or timed-out run actually stops instead of hanging on a container call.
    /// </summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Matches Program.cs's ConfigureHttpJsonOptions: enums by name, cycles ignored.
    /// Deserializing test assertions with different options than the app serializes
    /// with is a good way to write a passing test about the wrong thing.
    /// </summary>
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask InitializeAsync()
    {
        await Fixture.ResetAsync();
        Client = Fixture.App.CreateClient();
        GraphQL = new GraphQLClient(Client);
    }

    public ValueTask DisposeAsync()
    {
        Client?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Runs work against a fresh AppDbContext in its own scope. Used to arrange rows
    /// directly and, more importantly, to assert on what the database really holds
    /// rather than trusting the response body.
    /// </summary>
    protected async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = Fixture.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    protected Task WithDbAsync(Func<AppDbContext, Task> work) =>
        WithDbAsync<object?>(async db => { await work(db); return null; });

    /// <summary>
    /// Runs raw SQL and returns the first column of the first row.
    ///
    /// Needed because several assertions are about what Postgres *stores*, not what EF
    /// hands back: an enum mapped with HasConversion&lt;string&gt;() reads back as a CLR
    /// enum either way, so only the raw column value proves it was persisted as text.
    /// </summary>
    protected Task<object?> ScalarAsync(string sql) => WithDbAsync(async db =>
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(Ct);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    });

    /// <summary>
    /// Runs raw SQL for its effect. The counterpart to <see cref="ScalarAsync"/>, added
    /// in Phase 7 for the one thing EF cannot express: an INSERT that behaves like a
    /// writer which is *not* this application. Proving the database-side defaults (F11)
    /// work requires a statement that names neither the id nor the timestamps, and EF
    /// always supplies both.
    /// </summary>
    protected Task ExecuteAsync(string sql) =>
        WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, Ct));

    /// <summary>
    /// A second, independent AppDbContext in its own scope, for tests that need two
    /// callers reading the same row at once. Concurrency cannot be tested through one
    /// context: EF's change tracker would hand back the same tracked instance, and the
    /// second "caller" would be writing the first one's entity.
    ///
    /// The caller owns the returned scope and must dispose it; disposing the scope
    /// disposes the context with it.
    /// </summary>
    protected (IServiceScope Scope, AppDbContext Db) NewScopedDb()
    {
        var scope = Fixture.App.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
