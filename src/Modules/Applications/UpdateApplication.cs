using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: PATCH an application. Only the fields present in the request are
// applied; everything omitted is left alone.
//
// The second half of architecture.md A4. The retired ApplicationEndpoints.Update
// applied every non-null field with no checks at all, and an empty string is not
// null — so `PATCH { "title": "" }` wrote a blank title that POST /applications
// would have rejected, leaving the database in a state the create path calls
// invalid. The rule that create enforces now also guards update: a field you
// *send* must be valid, a field you omit is not your business.
//
// Phase 2.5 added the status lifecycle here (which transitions are legal), and the
// seam held: it was a change to this one file plus the rule it calls.

public record UpdateApplicationRequest(
    string? Company,
    string? Title,
    string? Location,
    ApplicationStatus? Status,
    string? Notes,
    string? Description,
    Guid? ResumeId);

public class UpdateApplicationHandler
{
    private readonly AppDbContext _db;

    public UpdateApplicationHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<ApplicationDetail>> HandleAsync(
        Guid id, UpdateApplicationRequest request, CancellationToken ct = default)
    {
        // `is not null` distinguishes "omitted" from "sent as blank": the first
        // is a no-op, the second is an error. Collapsing them into
        // IsNullOrWhiteSpace would make a blank title silently ignored instead
        // of rejected, which is the same bug wearing a nicer face.
        if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
            return SliceResult<ApplicationDetail>.Invalid("Title must not be blank.");
        if (request.Company is not null && string.IsNullOrWhiteSpace(request.Company))
            return SliceResult<ApplicationDetail>.Invalid("Company must not be blank.");

        // Tracked (no AsNoTracking) because this one writes. Only the posting
        // and its company are loaded — the skills, requirements and analyses
        // this endpoint cannot touch are not fetched.
        var application = await _db.JobApplications
            .Include(a => a.Posting).ThenInclude(p => p.Company)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        if (application is null)
            return SliceResult<ApplicationDetail>.NotFound($"Application {id} not found.");

        // Phase 2.5: the status lifecycle. Checked before anything is mutated, so a
        // rejected transition leaves the whole PATCH unapplied rather than saving the
        // other fields and refusing only this one. The rule itself is in
        // ApplicationStatusTransitions — the handler asks, it does not decide, which is
        // what keeps the rule storage-agnostic and unit-testable without a database.
        //
        // Both surfaces reach this because both call this handler, so GraphQL cannot
        // quietly permit a move REST refuses (that was A4, and this is the shape of the
        // fix rather than a second copy of the table).
        if (request.Status is not null
            && !ApplicationStatusTransitions.IsAllowed(application.Status, request.Status.Value))
        {
            return SliceResult<ApplicationDetail>.Invalid(
                ApplicationStatusTransitions.RejectionMessage(
                    application.Status, request.Status.Value));
        }

        // Same check as the create slice, and for the same reason: ResumeId is a
        // Restrict foreign key, so an id naming no resume is a DbUpdateException at
        // SaveChanges rather than a message. Checked here, before anything is
        // mutated, so a bad id leaves the whole update unapplied — the same
        // property the status check above is written for.
        if (request.ResumeId is not null
            && !await _db.Resumes.AnyAsync(r => r.Id == request.ResumeId.Value, ct))
            return SliceResult<ApplicationDetail>.Invalid($"Resume {request.ResumeId} not found.");

        // Application-level fields.
        if (request.Status is not null) application.Status = request.Status.Value;
        if (request.Notes is not null) application.Notes = request.Notes;
        // Null means "not supplied", so a resume cannot be UNLINKED through this
        // endpoint. That is the same limitation every other optional field here
        // has — Notes and Location cannot be cleared either — and it is the cost of
        // a partial-update shape that uses null as the absent marker. Worth
        // changing deliberately for all of them if it ever bites, not for this one
        // field in isolation.
        if (request.ResumeId is not null) application.ResumeId = request.ResumeId;

        // Posting-level fields.
        if (request.Title is not null) application.Posting.Title = request.Title.Trim();
        if (request.Location is not null) application.Posting.Location = request.Location;
        if (request.Description is not null) application.Posting.Description = request.Description;

        if (request.Company is not null)
        {
            // Renaming the employer re-points the posting at an existing company
            // row when one matches, rather than renaming the shared row — which
            // would silently rename it for every other application too.
            var company = await CompanyLookup.ResolveAsync(_db, request.Company.Trim(), ct);
            application.Posting.Company = company;
            application.Posting.CompanyId = company.Id;
        }

        // Hand-maintained, and the only place in the codebase that maintains it
        // — which is architecture.md A8, not a feature. Every other write path
        // (the four sub-resource slices, create, delete) saves without touching
        // a timestamp, so this column is already partly a lie. The fix is a
        // SaveChangesInterceptor, scheduled with the rest of the audit baseline.
        // Phase 7 — the hand-set timestamp that used to live here is gone.
        // AuditSaveChangesInterceptor stamps every IAuditable on the way out, so
        // assigning it here as well would put two writers on one column, which is
        // the shape F8 was about in the first place. Note the interceptor also
        // stamps `application.Posting` when this method changes the title or the
        // location — job_postings.UpdatedAtUtc is new in this phase, and nothing
        // in C# assigns to it at all.
        await _db.SaveChangesAsync(ct);

        var updated = await _db.JobApplications
            .AsNoTracking()
            .Where(a => a.Id == id)
            .Select(ApplicationDetailProjection.Expression)
            .FirstAsync(ct);

        return SliceResult<ApplicationDetail>.Ok(updated);
    }
}
