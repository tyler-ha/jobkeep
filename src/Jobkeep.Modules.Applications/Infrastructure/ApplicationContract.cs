using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// PHASE 13.2: the interface and its DTOs live in Jobkeep.Contracts; the
// implementation stays here, with the module that owns the tables it guards.
public class ApplicationContract : IApplicationContract
{
    private readonly ApplicationsDbContext _db;
    private readonly CreateApplicationHandler _createApplication;
    private readonly AddRequirementToPostingHandler _addRequirement;
    // This module's own other contract. Reused rather than reimplemented: linking
    // extracted skills to a posting is one operation with one set of rules (never
    // restamp a human's row), and it already lives behind IPostingContract for
    // Phase 4's analyzer. A second copy here is the two-implementations-of-one-rule
    // failure architecture.md A4 names, just inside a module instead of across two
    // API surfaces.
    private readonly IPostingContract _postings;

    public ApplicationContract(
        ApplicationsDbContext db,
        CreateApplicationHandler createApplication,
        AddRequirementToPostingHandler addRequirement,
        IPostingContract postings)
    {
        _db = db;
        _createApplication = createApplication;
        _addRequirement = addRequirement;
        _postings = postings;
    }

    public async Task<ApplicationRef?> GetRefAsync(Guid applicationId, CancellationToken ct = default)
        // A reference type projected out of the query, so FirstOrDefault's null
        // is "no such application" and cannot be confused with a row whose ids
        // happened to be empty. The old GetPostingIdAsync had to cast to Guid?
        // to buy the same distinction over a value type; a record gets it for
        // free, which is one reason the widening cost nothing.
        => await _db.JobApplications
            .AsNoTracking()
            .Where(a => a.Id == applicationId)
            .Select(a => new ApplicationRef(a.PostingId, a.ResumeId))
            .FirstOrDefaultAsync(ct);

    // PHASE 13.3c. Counted in SQL, not by loading and counting in C# — the same
    // rule the Analytics slices follow, and the reason it matters here is that
    // the answer is usually 0 and the rows are never wanted.
    public async Task<int> CountApplicationsForResumeAsync(
        Guid resumeId, CancellationToken ct = default)
        => await _db.JobApplications.CountAsync(a => a.ResumeId == resumeId, ct);

    // The contract delegates to this module's own use cases rather than writing
    // the tables itself, which is the same call CommitImport.cs used to make
    // directly — the difference is only WHO makes it. Documents no longer names
    // CreateApplicationHandler; this file, inside Applications, does. That is
    // the whole substance of 13.2c: the use case is unchanged and the reference
    // is gone.
    public async Task<PostingCommitResult> CommitPostingAsync(
        PostingCommitRequest request, CancellationToken ct = default)
    {
        var created = await _createApplication.HandleAsync(
            new CreateApplicationRequest(
                request.Company,
                request.Title,
                request.Location,
                request.Description,
                request.SourceUrl,
                // Neither belongs to a document import. Notes is the user's own
                // commentary, typed later; ResumeId is attached by the screen
                // that chooses one. Both are set through the ordinary update
                // path, not smuggled in through a contract.
                Notes: null,
                ResumeId: null),
            ct);

        // Only Invalid becomes a Refused. Anything else from the handler is a
        // shape this contract does not know how to summarise, and pretending it
        // is a validation message would tell the caller "edit your draft" for a
        // problem editing cannot fix.
        if (created.Status != ResultStatus.Ok)
            return PostingCommitResult.Refused(created.Error!);

        var application = created.Value!;

        // ------------------------------------------------------------------
        // From here the application EXISTS, and every failure has to say so
        // ------------------------------------------------------------------
        // Each step below is its own SaveChanges — CreateApplicationHandler,
        // AddExtractedSkillsAsync and AddRequirementToPostingHandler are three
        // use cases with three units of work, which is what makes them reusable
        // and is not something this method should reach in and change.
        //
        // So a throw here is a PARTIAL commit, and the caller cannot be left to
        // infer that from an exception. It gets the ids back with the error
        // attached, because the id is what stops a retry logging the job twice.
        // PostingCommitResult says the same thing from the other side.
        try
        {
            // Skills, marked AiExtracted for the reason Phase 4 marks its own: a
            // human who later types a skill by hand outranks a machine that read
            // it out of an ad, and AddExtractedSkillsAsync already refuses to
            // restamp an existing row.
            var linked = await _postings.AddExtractedSkillsAsync(application.Posting.Id, request.Skills, ct);

            // Requirements one at a time, which is the honest cost of reusing the
            // slice instead of writing job_requirements here. It is a loop inside
            // the module that owns the table, so at 13.3 it is a loop inside one
            // service rather than a dozen calls across a network — which is
            // precisely why the batching decision belongs on THIS side of the
            // contract.
            var saved = 0;
            var rejected = 0;
            foreach (var requirement in request.Requirements)
            {
                var result = await _addRequirement.HandleAsync(
                    application.Id,
                    new AddRequirementToPostingRequest(requirement.Text, ToEntity(requirement.Kind), requirement.IsMustHave),
                    ct);
                if (result.Status == ResultStatus.Ok) saved++;
                else rejected++;
            }

            return PostingCommitResult.Ok(application.Id, application.Posting.Id, linked, saved, rejected);
        }
        catch (Exception ex)
        {
            // Deliberately catching everything, which is a thing this codebase
            // does almost nowhere else. The justification is narrow and does not
            // generalise: the alternative is not "the exception propagates
            // usefully", it is "the caller loses the id of a row that exists".
            // The message still travels, so nothing is swallowed.
            return PostingCommitResult.Incomplete(application.Id, application.Posting.Id, ex.Message);
        }
    }

    // An explicit switch, not a cast. The two enums are declared in two projects
    // that will become two services (see PostingRequirementKind), so a value
    // added to one and not the other must fail to compile rather than quietly
    // mean whatever shares its ordinal.
    private static RequirementKind ToEntity(PostingRequirementKind kind) => kind switch
    {
        PostingRequirementKind.Qualification => RequirementKind.Qualification,
        PostingRequirementKind.Responsibility => RequirementKind.Responsibility,
        PostingRequirementKind.Benefit => RequirementKind.Benefit,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped requirement kind."),
    };
}
