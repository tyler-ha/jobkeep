using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Ats;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: delete a résumé version off the shelf.
//
// ---------------------------------------------------------------------------
// Why this route did not exist until 13.3c
// ---------------------------------------------------------------------------
// DiscardImport has been telling users to do this since Phase 4.5 — "This import
// has already been committed. Delete the resume or application it created
// instead." — against an endpoint that did not exist. The application half was
// true; the résumé half was not. 13.3c is where it became true, because the two
// RESTRICTs that used to guard `resumes` from deletion were dropped in 13.3b and
// their replacement is a check on THIS path. Writing the check without the path
// would have been dead code.
//
// ---------------------------------------------------------------------------
// Two refusals, and what they are not
// ---------------------------------------------------------------------------
// `job_applications.ResumeId` and `ats_results.ResumeId` were both RESTRICT until
// 13.3b. Both pointed into this table from another module's schema, and a foreign
// key that spans a boundary is the thing this phase is removing, so both are gone
// from Postgres and both are asked here instead — through IApplicationContract
// and IAtsContract, one count each.
//
// **This is weaker than RESTRICT and the difference is not academic.** A foreign
// key refuses inside the transaction that attempts the delete; two counts and a
// delete are three statements with gaps between them, so an application created
// against this résumé after the count and before the commit survives, pointing at
// a row that no longer exists. That is a time-of-check-to-time-of-use race, and it
// is the actual cost of moving integrity out of the database.
//
// Accepted, with the reasoning stated rather than hidden:
//   * The window is microseconds on a single-user local application where the
//     same person would have to be creating an application and deleting the
//     résumé it names at the same moment.
//   * The residue is the case the read path already handles. ApplicationDetail
//     leaves ResumeLabel null when the résumé is gone and says so in a comment;
//     GetAtsResult does the same. Neither renders a blank chip or throws.
//   * The alternatives are worse at this size. A distributed transaction across
//     four schemas re-couples exactly what the phase decoupled, and the real
//     answer at service scale — a saga with a compensating action, or making the
//     delete a soft one nothing has to race against — is Phase 8's work, which
//     is soft delete and rewrites this path anyway.
//
// The order matters slightly and is cheap: ask Applications first, because an
// application referencing a résumé is the far commoner case and the message a
// user is far likelier to see.
public class DeleteResumeHandler
{
    private readonly DocumentsDbContext _db;
    private readonly IApplicationContract _applications;
    private readonly IAtsContract _ats;

    public DeleteResumeHandler(
        DocumentsDbContext db,
        IApplicationContract applications,
        IAtsContract ats)
    {
        _db = db;
        _applications = applications;
        _ats = ats;
    }

    public async Task<SliceResult<bool>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var resume = await _db.Resumes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (resume is null)
            return SliceResult<bool>.NotFound($"Resume {id} not found.");

        var applications = await _applications.CountApplicationsForResumeAsync(id, ct);
        if (applications > 0)
            return SliceResult<bool>.Invalid(
                $"'{resume.Label}' was sent with {applications} application(s). "
                + "Point those at another résumé first, or delete them.");

        var checks = await _ats.CountResultsForResumeAsync(id, ct);
        if (checks > 0)
            return SliceResult<bool>.Invalid(
                $"'{resume.Label}' was judged by {checks} stored ATS check(s). "
                + "Delete those applications first — re-running a check is cheap, "
                + "but a result whose résumé is gone cannot be explained.");

        // Loaded and removed rather than ExecuteDelete, so EF applies the three
        // cascades a résumé has: resume_skills, resume_experiences and
        // resume_educations. All three are Documents' own tables in Documents'
        // own schema, which is why they still cascade when the two above do not.
        //
        // The shared `skills` rows survive their links, as they always have —
        // that is what a shared vocabulary table is for, and since 13.3b it is
        // another module's table this delete cannot reach anyway.
        //
        // NOT handled, deliberately: a Committed `document_imports` row whose
        // CommittedEntityId points at this résumé is left as it is. That column
        // has never been a foreign key — it holds either a résumé id or an
        // application id depending on Kind, which is why it could not be one —
        // and the import is a receipt for something that happened, not a pointer
        // that has to stay live. Deleting the receipt would delete the extracted
        // text that makes a bad parse diagnosable, which is the whole argument
        // DiscardImport.cs makes for keeping discarded rows.
        _db.Resumes.Remove(resume);
        await _db.SaveChangesAsync(ct);

        // No event published. Nothing outside this module has a reaction to a
        // deleted résumé — the two modules that point at one were asked above and
        // said no, which is the difference between a RESTRICT replacement and a
        // CASCADE replacement: one is a question asked before, the other is an
        // announcement made after.
        return SliceResult<bool>.Ok(true);
    }
}
