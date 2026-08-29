using System.Text.Json.Serialization;
using Jobkeep.Data;
using Jobkeep.GraphQL;
using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Analytics;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Ats;
using Jobkeep.Modules.Documents;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Storage is PostgreSQL via EF Core. The connection string comes from config:
// appsettings.Development.json points at the local Docker container; a deployed
// environment supplies it via an environment variable instead — so local vs cloud
// is a config change, not a code change. (Phase 3 is parked, and no longer names
// RDS: the plan it holds is Neon over the public internet, which is still just a
// connection string to everything below this line.)
var connectionString = builder.Configuration.GetConnectionString("Postgres");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// Every use case is a vertical slice under Modules/ (docs/architecture.md §2).
// Each slice handler takes AppDbContext directly, so this registers them and
// nothing else — as of Phase 2.3 there is no repository layer left to register.
builder.Services.AddApplicationsModule();

// Phase 2.4. Read-only: three aggregate queries, no tables of its own. It reads
// Applications-owned tables, which bends architecture.md rule 2 deliberately —
// AnalyticsModule.cs has the reasoning and the accepted cost.
builder.Services.AddAnalyticsModule();

// Phase 4. Owns `ai_analyses`; reaches Applications-owned tables through
// IPostingContract because it *writes* to them, which the read-only exception
// Analytics uses does not cover (Modules/Applications/PostingContract.cs).
// Takes IConfiguration because the model endpoint and tag are config, not code —
// that is the whole point of putting the analyzer behind IChatClient.
builder.Services.AddAiModule(builder.Configuration);

// The language model client itself, shared by every module that wants one.
// Registered here rather than inside AddAiModule since Phase 4.5, because
// Documents also calls a model and the Ai module owns a table, not a technology
// (Shared/ModelClient.cs has the full argument).
builder.Services.AddModelClient(builder.Configuration);

// Phase 4.5. Owns `document_imports` and the four resume tables. Turns an
// uploaded PDF/DOCX/text file into a draft, and — only once a human confirms it —
// into real rows. Reaches Applications through IPostingContract and through that
// module's own use-case handlers, never its tables directly.
builder.Services.AddDocumentsModule(builder.Configuration);

// Phase 5. Owns `ats_results` and reads five tables it does not own — posting
// skills, skills and requirements from Applications, resumes and resume skills
// from Documents. No contract, and no IConfiguration: architecture.md decision 17
// makes a cross-module *read* ordinary, and the two limits this module imposes on
// its one model call are constants rather than settings (AtsModule.cs).
builder.Services.AddAtsModule();

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

// Every /stats route (Modules/Analytics/AnalyticsModule.cs).
app.MapAnalyticsModule();

// The analyzer's two routes. They live under /applications/{id}/... even though
// the module is Ai — AiModule.cs explains why the URL follows the resource while
// the code follows the owner.
app.MapAiModule();

// Every /imports route: upload, review, correct, confirm, discard.
app.MapDocumentsModule();

// The ATS check, GET and POST, under /applications/{id}/ats-check.
app.MapAtsModule();

// Serves POST /graphql for queries + the Nitro (Banana Cake Pop) IDE at GET /graphql.
app.MapGraphQL();

app.Run();

// Top-level statements compile to an *internal* Program class, which
// WebApplicationFactory<Program> in tests/Jobkeep.Tests cannot name. This marker
// makes that generated class public without changing any behaviour. Preferred over
// InternalsVisibleTo because it exposes exactly one type instead of every internal.
public partial class Program { }
