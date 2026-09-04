using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Match;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: delete a résumé version off the shelf. Since PHASE 8 this ARCHIVES;
// DeleteApplication.cs argues why the name and the route still say "delete".
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
// `job_applications.ResumeId` and `match_results.ResumeId` were both RESTRICT until
// 13.3b. Both pointed into this table from another module's schema, and a foreign
// key that spans a boundary is the thing this phase is removing, so both are gone
// from Postgres and both are asked here instead — through IApplicationContract
// and IMatchContract, one count each.
//
// **This was weaker than RESTRICT, and PHASE 8 is what closed the gap.** The
// argument below is kept verbatim because it is the reasoning that chose this
// phase's position on the roadmap, and it named its own fix in the last bullet.
//
// The race is now harmless rather than merely unlikely: the row does not go away,
// so an application created against this résumé between the count and the commit
// points at a row that still exists and is still readable by anything that asks
// for it by id. What it gets is an ARCHIVED résumé rather than a missing one —
// ApplicationDetail's tolerance below covers exactly that, and a restore makes it
// whole. Nothing dangles, because nothing was destroyed.
//
// The two counts therefore stop being an integrity mechanism and become a
// USABILITY one: they refuse to hide a document that live work still points at,
// because a détail screen showing a blank résumé chip is a worse answer than a
// sentence saying why. Note both counts now see live rows only — the query filter
// on job_applications does that for the first, and Match's own filter would do it
// for the second if match_results were archivable, which it is not — so archiving
// the applications is what frees the résumé.
//
// The original statement of the problem:
//
// Accepted, with the reasoning stated rather than hidden:
//   * The window is microseconds on a single-user local application where the
//     same person would have to be creating an application and deleting the
//     résumé it names at the same moment. (Phase 8 note: the window is still
//     there; what changed is that landing in it now costs nothing.)
//   * The residue is the case the read path already handles. ApplicationDetail
//     leaves ResumeLabel null when the résumé is gone and says so in a comment;
//     GetMatchResult does the same. Neither renders a blank chip or throws.
//   * The alternatives are worse at this size. A distributed transaction across
//     four schemas re-couples exactly what the phase decoupled, and the real
//     answer at service scale — a saga with a compensating action, or making the
//     delete a soft one nothing has to race against — is Phase 8's work, which
//     is soft delete and rewrites this path anyway.
//
// That last clause is the one that came true, and it is worth saying out loud in
// an interview: the cheapest fix for a distributed-integrity problem was not a
// saga or a two-phase commit. It was deciding that nothing gets destroyed.
//
// The order matters slightly and is cheap: ask Applications first, because an
// application referencing a résumé is the far commoner case and the message a
// user is far likelier to see.
public record DeleteResume(Guid Id) : IRequest<SliceResult<bool>>;

public class DeleteResumeHandler : IRequestHandler<DeleteResume, SliceResult<bool>>
{
    private readonly DocumentsDbContext _db;
    private readonly IApplicationContract _applications;
    private readonly IMatchContract _ats;

    public DeleteResumeHandler(
        DocumentsDbContext db,
        IApplicationContract applications,
        IMatchContract ats)
    {
        _db = db;
        _applications = applications;
        _ats = ats;
    }

    public async ValueTask<SliceResult<bool>> Handle(
        DeleteResume message, CancellationToken ct)
    {
        var id = message.Id;
        var resume = await _db.Resumes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (resume is null)
            return SliceResult<bool>.NotFound($"Resume {id} not found.");

        var applications = await _applications.CountApplicationsForResumeAsync(id, ct);
        if (applications > 0)
            return SliceResult<bool>.Invalid(
                $"'{resume.Label}' was sent with {applications} application(s). "
                + "Point those at another résumé first, or archive them.");

        var checks = await _ats.CountResultsForResumeAsync(id, ct);
        if (checks > 0)
            return SliceResult<bool>.Invalid(
                $"'{resume.Label}' was judged by {checks} stored match check(s). "
                + "Archive those applications first — re-running a check is cheap, "
                + "but a result whose résumé is hidden cannot be explained.");

        // Loaded and removed rather than ExecuteDelete, and Phase 8 inverted why —
        // the same inversion as DeletePosting. It used to be so EF would apply the
        // three cascades a résumé has: resume_skills, resume_experiences and
        // resume_educations. Now it is so the interceptor can convert the delete
        // to an archive, and the three cascades DO NOT FIRE. The parsed skills,
        // jobs and qualifications survive, which is what a restore restores.
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

        // No event published, as before — and since Phase 8 the other two delete
        // slices publish nothing either, so this is no longer the odd one out. The
        // distinction it recorded still holds and is still the useful one: a
        // RESTRICT replacement is a question asked before, a CASCADE replacement
        // is an announcement made after, and this path only ever needed the first.
        return SliceResult<bool>.Ok(true);
    }
}
