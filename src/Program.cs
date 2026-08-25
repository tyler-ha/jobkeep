using System.Text.Json.Serialization;
using Jobkeep.Data;
using Jobkeep.Endpoints;
using Jobkeep.GraphQL;
using Jobkeep.Modules.Applications;
using Jobkeep.Repositories;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Storage is PostgreSQL via EF Core. The connection string comes from config:
// appsettings.Development.json points at the local Docker container; a deployed
// environment (Phase 3, RDS) supplies it via an environment variable instead —
// so local vs cloud is a config change, not a code change.
var connectionString = builder.Configuration.GetConnectionString("Postgres");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// Scoped, not singleton: the repository holds a scoped AppDbContext, so it must
// share that lifetime (a singleton over a scoped context is a captive dependency).
builder.Services.AddScoped<IJobApplicationRepository, PostgresJobApplicationRepository>();

// Phase 2.1 onward, use cases are vertical slices under Modules/ instead of
// methods on that repository (docs/architecture.md §2). Each slice handler takes
// AppDbContext directly; this registers them and nothing else.
builder.Services.AddApplicationsModule();

// EF navigation properties form reference cycles (posting <-> its skills). Tell
// System.Text.Json to ignore cycles so the REST endpoints can return entities
// directly. (GraphQL in Phase 2b resolves only requested fields, so it's immune.)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    // Serialize/accept enums by name ("Interviewing", "FullTime") instead of by
    // int, so REST payloads are readable and match what GraphQL exposes.
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// GraphQL (HotChocolate). Runs in-process on the same ASP.NET app, so it rides
// the same Lambda deployment in Phase 3 — no separate service. Resolvers pull
// the repository from DI, so GraphQL and REST share one storage implementation.
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

// REST routes for /applications live in Endpoints/ApplicationEndpoints.cs —
// the Phase 2 routes still served by the retiring repository.
app.MapApplicationEndpoints();

// The Applications module's slice routes (skills + requirements sub-resources).
// Same "/applications" prefix, different code path underneath; the line above
// shrinks as later phases move its routes into slices.
app.MapApplicationsModule();

// Serves POST /graphql for queries + the Nitro (Banana Cake Pop) IDE at GET /graphql.
app.MapGraphQL();

app.Run();

// Top-level statements compile to an *internal* Program class, which
// WebApplicationFactory<Program> in tests/Jobkeep.Tests cannot name. This marker
// makes that generated class public without changing any behaviour. Preferred over
// InternalsVisibleTo because it exposes exactly one type instead of every internal.
public partial class Program { }
