using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: log a new application.
//
// This is where architecture.md A4 dies. Before this phase the same operation
// existed twice — ApplicationEndpoints.Create hand-rolled a blank-company/title
// check, and GraphQL's createApplication called the repository straight and
// checked nothing, so `createApplication(input: { company: "Canva", title: "" })`
// wrote a posting that POST /applications would have refused. Two
// implementations of one rule is exactly what a slice exists to prevent: the
// check below now runs whichever surface the caller came in through.
//
// Kept intentionally small — company, title and a few optional posting fields.
// Skills and requirements are attached afterwards through their own slices, and
// Phase 4's analyzer fills in the rest.

public record CreateApplicationRequest(
    string Company,
    string Title,
    string? Location,
    string? Description,
    string? SourceUrl,
    string? Notes,
    // Phase 4.5: a resume is referenced, not pasted. The id comes from a
    // committed document import (POST /imports/{id}/confirm) or from a resume
    // created by hand.
    Guid? ResumeId);

public class CreateApplicationHandler
{
    private readonly AppDbContext _db;

    public CreateApplicationHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<ApplicationDetail>> HandleAsync(
        CreateApplicationRequest request, CancellationToken ct = default)
    {
        var company = request.Company?.Trim();
        var title = request.Title?.Trim();

        // One message for both fields, matching what the REST path has returned
        // since Phase 1 — the rule moved, the contract did not.
        if (string.IsNullOrEmpty(company) || string.IsNullOrEmpty(title))
            return SliceResult<ApplicationDetail>.Invalid("Company and Title are required.");

        // Phase 4.5 turned the resume from a pasted string into a foreign key, and
        // a foreign key can be wrong in a way a string could not. The FK is
        // Restrict, so an id naming no resume fails at SaveChanges as a
        // DbUpdateException — an unhandled 500 for what is plainly a bad request.
        // Resolved first instead, which is what CompanyLookup already does for the
        // other reference in this method: no reference in this codebase reaches the
        // database unchecked.
        if (request.ResumeId is not null
            && !await _db.Resumes.AnyAsync(r => r.Id == request.ResumeId.Value, ct))
            return SliceResult<ApplicationDetail>.Invalid($"Resume {request.ResumeId} not found.");

        var application = new JobApplication
        {
            Notes = request.Notes,
            ResumeId = request.ResumeId,
            Posting = new JobPosting
            {
                Title = title,
                Location = request.Location,
                Description = request.Description,
                SourceUrl = request.SourceUrl,
                // Resolved before the insert so an existing employer is reused
                // rather than tripping the unique index on companies.Name.
                Company = await CompanyLookup.ResolveAsync(_db, company, ct)
            }
        };
        application.Posting.CompanyId = application.Posting.Company.Id;

        _db.JobApplications.Add(application);
        await _db.SaveChangesAsync(ct);

        // Re-read through the projection rather than mapping the in-memory graph
        // by hand: this returns what the database actually stored, including
        // anything a default or trigger decided, and it is the same query
        // GetApplication answers with — so create and fetch cannot drift.
        var created = await _db.JobApplications
            .AsNoTracking()
            .Where(a => a.Id == application.Id)
            .Select(ApplicationDetailProjection.Expression)
            .FirstAsync(ct);

        return SliceResult<ApplicationDetail>.Ok(created);
    }
}
