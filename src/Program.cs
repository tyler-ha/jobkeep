using System.Text.Json.Serialization;
using Jobkeep.Data;
using Jobkeep.GraphQL;
using Jobkeep.Models;
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

// GET /applications — list everything, newest first
app.MapGet("/applications", async (IJobApplicationRepository repo) =>
{
    var all = await repo.GetAllAsync();
    return Results.Ok(all);
});

// GET /applications/{id} — fetch one
app.MapGet("/applications/{id:guid}", async (Guid id, IJobApplicationRepository repo) =>
{
    var application = await repo.GetByIdAsync(id);
    return application is not null ? Results.Ok(application) : Results.NotFound();
});

// POST /applications — create a new entry (company + posting are created/reused
// by the repository; skills/requirements are added later)
app.MapPost("/applications", async (CreateJobApplicationRequest request, IJobApplicationRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(request.Company) || string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest("Company and Title are required.");

    var application = new JobApplication
    {
        Notes = request.Notes,
        ResumeText = request.ResumeText,
        Posting = new JobPosting
        {
            Title = request.Title,
            Location = request.Location,
            Description = request.Description,
            SourceUrl = request.SourceUrl,
            Company = new Company { Name = request.Company }
        }
    };

    var created = await repo.CreateAsync(application);
    return Results.Created($"/applications/{created.Id}", created);
});

// PATCH /applications/{id} — update status, notes, posting fields, etc.
app.MapPatch("/applications/{id:guid}", async (Guid id, UpdateJobApplicationRequest request, IJobApplicationRepository repo) =>
{
    var updated = await repo.UpdateAsync(id, request);
    return updated is not null ? Results.Ok(updated) : Results.NotFound();
});

// DELETE /applications/{id}
app.MapDelete("/applications/{id:guid}", async (Guid id, IJobApplicationRepository repo) =>
{
    var deleted = await repo.DeleteAsync(id);
    return deleted ? Results.NoContent() : Results.NotFound();
});

// Serves POST /graphql for queries + the Nitro (Banana Cake Pop) IDE at GET /graphql.
app.MapGraphQL();

app.Run();
