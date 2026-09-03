using Jobkeep.Contracts.Documents;
using Jobkeep.Contracts.Skills;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: fetch one application in full.
//
// The read this replaces (IJobApplicationRepository.GetByIdAsync) built a
// five-part include graph behind AsSplitQuery and returned the EF entity. This
// one projects straight into ApplicationDetail, so it is a single statement
// selecting named columns, and what leaves the handler is a DTO the schema can
// change underneath (architecture.md A2).

// PHASE 13.4. The request, and it is a new type rather than a renamed one: the
// handler used to take a bare Guid, so there was nothing for a mediator to
// dispatch ON. Naming is the use case for the message and `...Handler` for the
// thing that runs it, so a call site reads Send(new GetApplication(id)).
public record GetApplication(Guid Id) : IRequest<SliceResult<ApplicationDetail>>;

public class GetApplicationHandler : IRequestHandler<GetApplication, SliceResult<ApplicationDetail>>
{
    private readonly ApplicationsDbContext _db;
    private readonly ISkillCatalog _skills;
    private readonly IResumeContract _resumes;

    // Phase 13.2d. ApplicationsDbContext exposes this module's five DbSets and
    // nothing else, so the two columns this slice needs from other modules —
    // a résumé's label, a skill's name — arrive through contracts instead of
    // through a navigation property. ApplicationDetailProjection.HydrateAsync is
    // where they are joined back on.
    public GetApplicationHandler(
        ApplicationsDbContext db, ISkillCatalog skills, IResumeContract resumes)
    {
        _db = db;
        _skills = skills;
        _resumes = resumes;
    }

    public async ValueTask<SliceResult<ApplicationDetail>> Handle(
        GetApplication request, CancellationToken ct)
    {
        var id = request.Id;

        var row = await _db.JobApplications
            .AsNoTracking()
            .Where(a => a.Id == id)
            .Select(ApplicationDetailProjection.Expression)
            .FirstOrDefaultAsync(ct);

        // Same message shape as every other slice, so the two surfaces render
        // one sentence in two forms rather than inventing their own.
        if (row is null)
            return SliceResult<ApplicationDetail>.NotFound($"Application {id} not found.");

        return SliceResult<ApplicationDetail>.Ok(
            await ApplicationDetailProjection.HydrateAsync(row, _skills, _resumes, ct));
    }
}
