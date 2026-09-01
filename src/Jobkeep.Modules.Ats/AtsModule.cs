using Jobkeep.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Modules.Ats;

// Module wiring for Ats: DI plus its two routes.
//
// ---------------------------------------------------------------------------
// This module reads five tables it does not own, and that is now legal
// ---------------------------------------------------------------------------
// CheckAts reads `posting_skills`, `skills` and `job_requirements` (owned by
// Applications) and `resumes` and `resume_skills` (owned by Documents). It writes
// exactly one table, `ats_results`, which it owns.
//
// Under architecture.md rule 2 as originally written — "a module only queries the
// tables it owns; cross-module reads go through a public contract" — that is five
// violations, and the fix would have been a contract method per question. This
// project has already watched that shape grow and then deleted it twice: once as
// IJobApplicationRepository (decision 5), once as the contract AnalyticsModule
// refused to build (decision 13).
//
// **Decision 17** narrowed the rule instead: the boundary is about *writes*, not
// reads. The argument is not new — it is what decisions 13, 14 and 15 were each
// saying about their own case. Decision 13's whole justification for Analytics is
// that being read-only means it "can never leave another module's data in a state
// that module did not choose, so the coupling is to a shape, not to a lifecycle".
// Decision 14 built IPostingContract for the Ai module *because Ai writes*
// posting_skills, and said in as many words that the read-only exception did not
// cover a writer. Ats reads and does not write, so nothing here needs a contract.
//
// This is also why IPostingContract stays at two methods. Its cap comment says a
// third method means the boundary is in the wrong place; a `GetPostingSkills`
// method would have been that third. The boundary was never wrong — the read
// simply did not need guarding.
//
// The cost, stated rather than discovered later: Ats couples to another module's
// *schema*, so renaming `posting_skills.IsRequired` breaks a module that did not
// change. The compiler says so at build time, which is the cheap kind of failure.
// Extracting Ats into its own service later stops being a pure code-move and
// needs those five reads served another way — a view, a read replica, or an API
// call. That is a bounded migration on one module, and much cheaper than the
// contract-per-question alternative, which is unbounded by construction.
public static class AtsModule
{
    public static IServiceCollection AddAtsModule(this IServiceCollection services)
    {
        // Scoped, matching AppDbContext — both handlers hold one.
        services.AddScoped<CheckAtsHandler>();
        services.AddScoped<GetAtsResultHandler>();

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
