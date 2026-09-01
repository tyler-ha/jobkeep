using System.Text.Json.Serialization;
// Phase 13.1: the Map*Module extensions moved here from each module's own
// wiring file, so the module projects carry no ASP.NET dependency. The Add*
// half still lives with its module. Both halves disappear into controllers
// and a module DI extension in 13.5.
using Jobkeep.Api.Endpoints;
using Jobkeep.Data;
using Jobkeep.GraphQL;
using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Analytics;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Ats;
using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Skills;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Storage is PostgreSQL via EF Core. The connection string comes from config:
// appsettings.Development.json points at the local Docker container; a deployed
// environment supplies it via an environment variable instead — so local vs cloud
// is a config change, not a code change. (The deploy, Phase 10, is parked, and no longer names
// RDS: the plan it holds is Neon over the public internet, which is still just a
// connection string to everything below this line.)
var connectionString = builder.Configuration.GetConnectionString("Postgres");

// Phase 7 — the audit-timestamp interceptor (F8). Registered here rather than
// inside AppDbContext because AppDbContext's job is the schema, in one readable
// place, and because a test needs to be able to swap the clock or leave the
// interceptor off entirely to write a known timestamp and watch it change.
//
// Singleton: it holds a clock function and nothing else, so there is no state to
// scope. AddDbContext is scoped, and a singleton dependency inside a scoped
// service is the safe direction — the captive-dependency hazard runs the other
// way.
builder.Services.AddSingleton<AuditSaveChangesInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, options) => options
    .UseNpgsql(connectionString)
    .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));

// Phase 13.2 — the per-module views of that one context. Each interface exposes
// only its own module's DbSets, so a handler physically cannot name another
// module's table: the property is not there to type.
//
// Registered HERE, in the composition root, and that placement is the point. The
// module projects still reference Jobkeep.Infrastructure.Data (it dies at 13.3),
// so any of them could resolve AppDbContext by name if it were wired locally.
// Keeping the concrete type in one file means there is exactly one place a
// module could cheat from, and an architecture test watches for it.
//
// All six resolve the SAME scoped AppDbContext rather than constructing one
// each. That matters: a slice holding two of these interfaces is holding one
// change tracker and one transaction, exactly as before. Registering them as
// separate contexts would have made SaveChanges mean different things depending
// on which interface was asked — a behaviour change smuggled into the one step
// whose whole value is that it has none. 13.3 splits them for real.
builder.Services.AddScoped<IApplicationsDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<ISkillsDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<IDocumentsDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<IAiDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<IAtsDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<IAnalyticsDbContext>(sp => sp.GetRequiredService<AppDbContext>());

// Every use case is a vertical slice under Modules/ (docs/architecture.md §2).
// Each slice handler takes AppDbContext directly, so this registers them and
// nothing else — as of Phase 2.3 there is no repository layer left to register.
builder.Services.AddApplicationsModule();

// Phase 13.2. The shared skill taxonomy, promoted out of Applications a step
// early because ISkillCatalog needs an owner. No routes: a skill is never the
// thing a user asks for (SkillsModule.cs).
builder.Services.AddSkillsModule();

// Phase 2.4. Read-only: three aggregate queries and no tables of its own. Since
// Phase 13.2 it reads three PUBLISHED VIEWS rather than Applications' tables, so
// the decision-13 exception it relied on is retired — Views/AnalyticsViews.cs
// has the argument, and IAnalyticsDbContext has no SaveChangesAsync at all.
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
// into real rows. Since 13.2c it names no other module: Applications is reached
// through IApplicationContract and the shared taxonomy through ISkillCatalog,
// both in Jobkeep.Contracts, and its csproj carries no module reference at all.
builder.Services.AddDocumentsModule(builder.Configuration);

// Phase 5. Owns `ats_results` and reads five tables it does not own — posting
// skills, skills and requirements from Applications, resumes and resume skills
// from Documents. No contract, and no IConfiguration: architecture.md decision 17
// makes a cross-module *read* ordinary, and the two limits this module imposes on
// its one model call are constants rather than settings (AtsModule.cs).
builder.Services.AddAtsModule();

// ---------------------------------------------------------------------------
// CORS, for the Phase 6 front end
// ---------------------------------------------------------------------------
// The front end's dev server and this API are different origins — :5173 and
// :5080 — so every fetch from a screen is a cross-origin request. Without a
// policy the browser refuses them all, and it refuses them *client*-side, which
// is why this is the kind of gap that gets misdiagnosed as a broken front end.
//
// Three choices here, each of which would be wrong to make differently:
//
//   * A NAMED policy with an explicit origin list, not AllowAnyOrigin. A
//     permissive policy that reaches production is a genuine finding, and
//     "temporary" wildcards are exactly the ones that ship. It is also not a
//     choice that stays available: AllowAnyOrigin and AllowCredentials are
//     mutually exclusive in ASP.NET Core, so writing the wildcard now would
//     have to be undone the moment auth lands and requests start carrying a
//     credential. Better to be in the shape that survives that.
//
//   * Origins from CONFIG, not from code — the same argument the connection
//     string above makes. The dev server's port is a local fact, a deployed
//     front end's origin is a deployment fact, and neither is a code change.
//     appsettings.Development.json holds the default.
//
//   * Registered always, APPLIED only in Development (see UseCors below).
//     AddCors here is inert; nothing happens until middleware runs. A deployed
//     environment therefore has no CORS at all until someone deliberately adds
//     its front-end origin, which is the right default for an API that is about
//     to sit on a public Function URL with no authentication in front of it.
//
// What auth will have to revisit: AllowCredentials is deliberately NOT set. Add
// it only alongside the origin list staying explicit, and re-read the
// antiforgery paragraph in DocumentsModule.cs at the same time — that one is
// switched off precisely because there are no cookies for a browser to attach
// yet, and both decisions change together.
const string DevCorsPolicy = "localdev";
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173"];

builder.Services.AddCors(options => options.AddPolicy(DevCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

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
// the same Lambda deployment in Phase 10 — no separate service. Resolvers pull
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
// the UI to a deployed environment (and it keeps the Lambda in Phase 10 lean).
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // Development-only, and before the Map* calls below — CORS has to run early
    // enough to answer the preflight OPTIONS itself, which never reaches an
    // endpoint. See the AddCors block above for why a deployed environment gets
    // no policy until one is added on purpose.
    app.UseCors(DevCorsPolicy);
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
