using Jobkeep.Contracts.Skills;
namespace Jobkeep.Contracts.Ats;

// PHASE 13.3c: Ats stops being a pure consumer.
//
// AtsModule.cs said, correctly, that this module was the only one with no
// contract of its own: nothing read `ats_results` except its own two routes, and
// "a contract with no caller would be wire schema nobody can safely remove."
// That held until 13.3b dropped `ats_results.ResumeId` -> `resumes`, a RESTRICT
// that stopped you deleting a résumé some stored ATS check had judged. Replacing
// it means Documents has to ask this module a question before it deletes, and
// that question is the first thing anyone has ever needed to know about
// `ats_results` from outside.
//
// One method, and the test from ISkillCatalog applies unchanged: does it name a
// fact about an ATS RESULT, or a question the caller has about its own feature?
// "How many stored results judged this résumé" is a fact about this table --
// Documents cannot compute it, cannot see the table, and does not learn anything
// about ATS checking by asking.
public interface IAtsContract
{
    // How many stored ATS results were judged against this résumé.
    //
    // A COUNT rather than a bool, and rather than the rows. The count is what a
    // caller can put in a sentence -- "2 ATS checks were run against this résumé"
    // reads better than "this résumé is in use" and costs the same query. The
    // rows would be an over-fetch of another module's data for a yes/no.
    //
    // WHAT THIS IS NOT: it is not RESTRICT. A foreign key refuses the delete
    // inside the same transaction that attempts it; this answers a question and
    // then the caller does something else, which leaves a window where a check
    // written after the count would survive the delete. DeleteResume.cs names
    // that race and accepts it with reasons -- the point here is that the
    // narrowing is real and belongs in the caller's comment, not hidden behind
    // a method that reads like a constraint.
    Task<int> CountResultsForResumeAsync(Guid resumeId, CancellationToken ct = default);
}
