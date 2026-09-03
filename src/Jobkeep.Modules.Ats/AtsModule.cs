using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Ats;
using Jobkeep.Contracts.Documents;
using Jobkeep.Contracts.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Modules.Ats;

// Module wiring for Ats: DI plus its two routes.
//
// ---------------------------------------------------------------------------
// This module used to read five tables it did not own. 13.2e ended that.
// ---------------------------------------------------------------------------
// Until Phase 13.2e this file carried a long argument for why reading
// `posting_skills`, `skills` and `job_requirements` (owned by Applications) plus
// `resumes` and `resume_skills` (owned by Documents) was legitimate. It is
// rewritten rather than deleted, because the argument was right and is now
// answering a question nobody is asking.
//
// What it said: architecture.md **decision 17** narrowed rule 2 so the boundary
// was about *writes*, not reads. That was not a loophole — it was what decisions
// 13, 14 and 15 had each been saying about their own case. A reader can never
// leave another module's data in a state that module did not choose, so the
// coupling is to a shape, not to a lifecycle. Ats read and did not write, so
// nothing here needed a contract, and IPostingContract could stay at two methods
// because a GetPostingSkills method would have been guarding nothing.
//
// What changed: Phase 13 asks a different question. Decision 17 answers *"is
// this safe?"* — and it still does, correctly. Phase 13 asks *"can this be lifted
// out and deployed on its own?"*, and against that question read-only buys
// nothing at all, because a SELECT across a boundary is precisely what stops
// working when the boundary becomes a network. Five safe reads are still five
// joins that will not exist.
//
// So the reads are now contract calls:
//
//     IApplicationContract.GetRefAsync       which posting, which resume
//     IPostingContract.GetSkillsAsync        the ad's skill ids + IsRequired
//     IPostingContract.GetRequirementsAsync  the ad's free-text requirements
//     IResumeContract.GetContentAsync        the CV, for stages 3 and 4
//     IResumeContract.GetSkillIdsAsync       the CV's skill ids
//     ISkillCatalog.GetAsync                 ids to names
//
// and IPostingContract's two-method cap was lifted in the same step, with its own
// reasoning rewritten in place for exactly this reason. This module's csproj
// carries no reference to another module; everything above is in Jobkeep.Contracts.
//
// ---------------------------------------------------------------------------
// The cost, stated rather than discovered later
// ---------------------------------------------------------------------------
// The old comment named the cost of NOT doing this: extracting Ats would stop
// being a code-move. The cost of doing it is the other side of that trade, and it
// is real:
//
//   * The skill gap left SQL. It was one query with a correlated EXISTS; it is
//     now two calls and an in-memory set difference, which breaks CLAUDE.md's
//     "aggregate in SQL, not in memory". CheckAts.cs argues that in full — the
//     short version is that both sets are tens of items and bounded by what a
//     human typed, and the alternative is a join that will not exist.
//   * Round trips went from three queries to six calls. In-process today, so the
//     difference is negligible; at 13.3 it is six network hops for one check, and
//     that is when batching becomes a real question rather than a premature one.
//   * A read is no longer a snapshot. Concurrent edits between calls can produce
//     a check that judged a résumé state no single moment ever had. Accepted,
//     because an ATS result is already a stored judgement about a moment.
//
// What it buys is the thing the phase is for: this module now names one table,
// `ats_results`, and it owns it. Lifting it out is a code-move again.
public static class AtsModule
{
    public static IServiceCollection AddAtsModule(this IServiceCollection services)
    {
        // PHASE 13.4 — the AddScoped<XHandler>() lines that were here are gone.
        // AddMediator() in Program.cs registers every IRequestHandler<,> and
        // INotificationHandler<> it finds in the referenced module assemblies, so
        // a new slice is now a file and a route, with nothing to remember to
        // register. What stays below is what a mediator cannot know about.

        // PHASE 13.3c ENDED THE PURE-CONSUMER ASYMMETRY, on the condition the
        // old comment named. It said: "Ats is the only module that is purely a
        // CONSUMER — nothing reads `ats_results` except the routes below, so
        // there is nothing for this module to expose. A contract with no caller
        // would be wire schema nobody can safely remove."
        //
        // A caller arrived. 13.3b dropped `ats_results.ResumeId` -> `resumes`,
        // and replacing that RESTRICT means Documents must ask this module
        // whether a résumé is spoken for before deleting it. One method, one
        // caller, and the condition for adding it was met rather than argued
        // around — which is what that comment was for.
        services.AddScoped<IAtsContract, AtsContract>();

        // The other half of the same drop: `ats_results.ApplicationId` was a
        // CASCADE. OnApplicationDeleted subscribes rather than being called by
        // name, so Applications announces its delete and never learns this module
        // exists — see Jobkeep.Contracts' ApplicationEvents.cs for why that
        // direction was chosen over a fifth contract method. Since 13.4 it needs
        // no registration here: AddMediator() finds it.

        // No options class. The two limits CheckAts imposes on the model call are
        // constants in that file with the measurement beside them, for the reason
        // AiSchema gives about Temperature: they are correctness, not preferences,
        // and a knob invites someone to turn it.
        //
        // IChatClient comes from AddModelClient in Program.cs. Ats owns the
        // `ats_results` table, not the technology (decision 16).
        return services;
    }
}
