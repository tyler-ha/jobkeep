using Jobkeep.Models;
using Jobkeep.Repositories;

namespace Jobkeep.Endpoints;

// All the REST routes for /applications live here instead of in Program.cs, so
// Program.cs stays pure wiring (DI + middleware). To add another resource later
// (e.g. AI endpoints in Phase 4), add a sibling *Endpoints.cs file and one
// Map...Endpoints() call — Program.cs doesn't grow per-route.
public static class ApplicationEndpoints
{
    public static IEndpointRouteBuilder MapApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        // MapGroup applies the "/applications" prefix (and the Swagger tag) to every
        // route below, so the path and tag aren't repeated on each line.
        var group = app.MapGroup("/applications").WithTags("Applications");

        group.MapGet("/", GetAll);
        group.MapGet("/{id:guid}", GetById);
        group.MapPost("/", Create);
        group.MapPatch("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);

        return app;
    }

    // GET /applications — list everything, newest first
    private static async Task<IResult> GetAll(IJobApplicationRepository repo)
    {
        var all = await repo.GetAllAsync();
        return Results.Ok(all);
    }

    // GET /applications/{id} — fetch one
    private static async Task<IResult> GetById(Guid id, IJobApplicationRepository repo)
    {
        var application = await repo.GetByIdAsync(id);
        return application is not null ? Results.Ok(application) : Results.NotFound();
    }

    // POST /applications — create a new entry (company + posting are created/reused
    // by the repository; skills/requirements are added later)
    private static async Task<IResult> Create(CreateJobApplicationRequest request, IJobApplicationRepository repo)
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
    }

    // PATCH /applications/{id} — update status, notes, posting fields, etc.
    private static async Task<IResult> Update(Guid id, UpdateJobApplicationRequest request, IJobApplicationRepository repo)
    {
        var updated = await repo.UpdateAsync(id, request);
        return updated is not null ? Results.Ok(updated) : Results.NotFound();
    }

    // DELETE /applications/{id}
    private static async Task<IResult> Delete(Guid id, IJobApplicationRepository repo)
    {
        var deleted = await repo.DeleteAsync(id);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}
