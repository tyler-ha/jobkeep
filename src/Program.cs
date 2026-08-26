using System.Text.Json.Serialization;
using Jobkeep.Data;
using Jobkeep.GraphQL;
using Jobkeep.Modules.Applications;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Storage is PostgreSQL via EF Core. The connection string comes from config:
// appsettings.Development.json points at the local Docker container; a deployed
// environment (Phase 3, RDS) supplies it via an environment variable instead —
// so local vs cloud is a config change, not a code change.
var connectionString = builder.Configuration.GetConnectionString("Postgres");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// Every use case is a vertical slice under Modules/ (docs/architecture.md §2).
// Each slice handler takes AppDbContext directly, so this registers them and
// nothing else — as of Phase 2.3 there is no repository layer left to register.
builder.Services.AddApplicationsModule();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    // Serialize/accept enums by name ("Interviewing", "FullTime") instead of by
    // int, so REST payloads are readable and match what GraphQL exposes.
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // ReferenceHandler.IgnoreCycles used to be set here, and it was load-bearing:
    // the endpoints returned EF entities whose navigation properties cycle
    // (posting <-> its skills), and without the flag System.Text.Json threw.
    // That was a symptom of leaking the database schema out as the API contract,
    // not a serialization preference (architecture.md A2). Phase 2.3 finished
    // moving every route onto response DTOs, which have no cycles, so the flag
    // came out. If it ever needs to come back, something is returning an entity.
});

// GraphQL (HotChocolate). Runs in-process on the same ASP.NET app, so it rides
// the same Lambda deployment in Phase 3 — no separate service. Resolvers pull
// slice handlers from DI, so GraphQL and REST share one code path.
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>();

// AddEndpointsApiExplorer discovers minimal-API endpoints; AddSwaggerGen
// turns that into an OpenAPI document Swagger UI can render.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Local-only convenience: apply migrations on startup so `dotnet run` works right
// after the Postgres container comes up. In a deployed environment migrations
// should be applied deliberately (a release step), not automatically on boot.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// Only expose the interactive docs in Development — no reason to ship
// the UI to a deployed environment (and it keeps the Lambda in Phase 3 lean).
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Every /applications route, REST side. One call, because the module owns its
// own routing (Modules/Applications/ApplicationsModule.cs).
app.MapApplicationsModule();

// Serves POST /graphql for queries + the Nitro (Banana Cake Pop) IDE at GET /graphql.
app.MapGraphQL();

app.Run();

// Top-level statements compile to an *internal* Program class, which
// WebApplicationFactory<Program> in tests/Jobkeep.Tests cannot name. This marker
// makes that generated class public without changing any behaviour. Preferred over
// InternalsVisibleTo because it exposes exactly one type instead of every internal.
public partial class Program { }
