using Jobkeep.Contracts.Skills;
namespace Jobkeep.Contracts.Match;

// PHASE 13.3c: Match stops being a pure consumer.
//
// MatchModule.cs said, correctly, that this module was the only one with no
// contract of its own: nothing read `match_results` except its own two routes, and
// "a contract with no caller would be wire schema nobody can safely remove."
// That held until 13.3b dropped `match_results.ResumeId` -> `resumes`, a RESTRICT
// that stopped you deleting a résumé some stored match check had judged. Replacing
// it means Documents has to ask this module a question before it deletes, and
// that question is the first thing anyone has ever needed to know about
// `match_results` from outside.
//
// One method, and the test from ISkillCatalog applies unchanged: does it name a
// fact about a MATCH RESULT, or a question the caller has about its own feature?
// "How many stored results judged this résumé" is a fact about this table --
// Documents cannot compute it, cannot see the table, and does not learn anything
// about match checking by asking.
public interface IMatchContract
{
    // How many stored match results were judged against this résumé.
    //
    // A COUNT rather than a bool, and rather than the rows. The count is what a
    // caller can put in a sentence -- "2 match checks were run against this résumé"
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

    // PHASE 9, gap 1 -- the headline numbers off a stored check, for a page of
    // applications at once.
    //
    // WHY THIS IS A CONTRACT METHOD AT ALL. The plan said this projection was
    // "legal under decision 17 and needs no contract", which was true when it was
    // written and stopped being true at Phase 13: every crossing needs a contract
    // now, reads included. So the question became whether it belongs HERE, and
    // CLAUDE.md's test is the one above -- does it name a fact about the thing, or
    // a question the caller has about its own feature?
    //
    // It names a fact. `match_results` is 1:1 with an application and already
    // stores these three lists; "how did the stored check score this application"
    // is a property of the row, not a computation Applications wants for its list.
    // Applications cannot see the table, cannot derive the answer from anything it
    // owns, and learns nothing about match checking by asking -- the same three
    // things that made CountResultsForResumeAsync legitimate.
    //
    // The counter-example is the one already settled: the ATS skill gap did NOT
    // become a method on IPostingContract, because a set difference between an
    // ad's skills and a CV's is Match's own feature made out of two facts. This is
    // the fact, not the feature.
    //
    // BATCHED, keyed by application id, and absent from the dictionary means never
    // checked. The same shape ISkillCatalog.GetAsync has, for the same reason: the
    // caller is a list, and one call per row is the N+1 the front end dropped the
    // column rather than ship.
    Task<IReadOnlyDictionary<Guid, MatchSummary>> GetSummariesAsync(
        IReadOnlyCollection<Guid> applicationIds, CancellationToken ct = default);
}

// What a list row shows: "5/7", or nothing at all.
//
// Two integers rather than the keyword lists, because a row that carried them
// would pull five text[] columns per application across a boundary to render six
// characters. Matched is the ad's skills the CV has; Total is every skill the ad
// named, must-have and nice-to-have together -- which is what makes the pair
// readable as a fraction without a legend.
//
// DELIBERATELY NOT HERE: CheckedAtUtc, the warning, and the must-have/nice-to-have
// split. A stale check and a fresh one look the same in this column, and that is
// the column's ceiling, not an oversight -- the detail screen is one click away
// and shows all three. The warning in particular is safe to omit for a reason
// worth stating: it means the model was unreachable, and the model contributes
// nothing to these two numbers (three of the check's four stages need no model).
// So a warned check's fraction is as correct as an unwarned one's.
public record MatchSummary(int Matched, int Total);
