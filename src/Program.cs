using JobTracker.Models;
using JobTracker.Repositories;

var builder = WebApplication.CreateBuilder(args);

// Swap this one line in Phase 2 to register a DynamoDB-backed
// repository instead — nothing below this line needs to change.
builder.Services.AddSingleton<IJobApplicationRepository, InMemoryJobApplicationRepository>();

// AddEndpointsApiExplorer discovers minimal-API endpoints; AddSwaggerGen
// turns that into an OpenAPI document Swagger UI can render.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

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
    var app = await repo.GetByIdAsync(id);
    return app is not null ? Results.Ok(app) : Results.NotFound();
});

// POST /applications — create a new entry
app.MapPost("/applications", async (CreateJobApplicationRequest request, IJobApplicationRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(request.Company) || string.IsNullOrWhiteSpace(request.Role))
        return Results.BadRequest("Company and Role are required.");

    var application = new JobApplication
    {
        Company = request.Company,
        Role = request.Role,
        Notes = request.Notes,
        JobDescription = request.JobDescription
    };

    var created = await repo.CreateAsync(application);
    return Results.Created($"/applications/{created.Id}", created);
});

// PATCH /applications/{id} — update status, notes, etc.
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

app.Run();
