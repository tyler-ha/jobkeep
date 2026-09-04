using Jobkeep.Contracts.Applications;
using Jobkeep.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Modules.Ai;

// Configuration for the analyzer, bound from the "Ai" section of appsettings.
// A plain class registered as a singleton rather than IOptions<T>: nothing here
// reloads, and IOptions buys change-notification this project has no use for
// while costing an extra unwrap in every handler.
public class AiOptions
{
    // Job ads are occasionally pasted with a whole careers page attached. See
    // AnalyzePosting.cs for why this truncates instead of rejecting.
    public int MaxDescriptionChars { get; set; } = 12000;

    // Endpoint, Model and TimeoutSeconds used to live here. Phase 4.5 moved them
    // to ModelOptions in Shared/ModelClient.cs, because a second module now calls
    // a model and the connection is not this module's property to own. They are
    // still bound from the same "Ai" configuration section, so no appsettings
    // file changed.
}

// Module wiring for Ai: DI plus its two routes.
//
// ---------------------------------------------------------------------------
// Local models only, and what that costs
// ---------------------------------------------------------------------------
// This module talks to Ollama on localhost and to nothing else. That is a
// deliberate constraint, not a stage on the way to a hosted provider: the
// project's first priority is that cost stays at zero, and a local model is the
// only option where that is true by construction rather than by staying under
// someone's free grant.
//
// The Phase 4 plan originally had a step 4 — "swap the IChatClient to a cheap
// hosted model for the deployed Lambda". That step is **not being done**, and the
// phase doc records why. The consequence, stated rather than discovered later:
// Ollama cannot run inside a Lambda, so if the deploy (Phase 10) ever unparks, the deployed
// build has no analyzer until a provider is chosen and paid for.
//
// The IChatClient abstraction is kept anyway, and it is worth being precise about
// why, because "we might swap it later" is the weak version of the argument:
//
//   * It is what makes the constraint *reversible*. Choosing a hosted provider
//     later is this file changing, not the handler, not the slices, not the
//     surfaces. The decision stays a decision instead of becoming a rewrite.
//   * It is what makes the structured-output call in AnalyzePosting.cs
//     provider-neutral — GetResponseAsync<T> derives a JSON schema and constrains
//     generation, and that code is identical against Ollama or anything else.
//   * It is what makes the analyzer testable without a model at all. The tests
//     substitute a fake IChatClient, which is the one place in this codebase
//     where a fake is the right call rather than a shortcut — see the comment in
//     tests/Jobkeep.Tests/Ai/.
public static class AiModule
{
    public static IServiceCollection AddAiModule(this IServiceCollection services, IConfiguration config)
    {
        var options = new AiOptions();
        config.GetSection("Ai").Bind(options);
        services.AddSingleton(options);

        // IChatClient itself is registered by AddModelClient in Program.cs, not
        // here. See Shared/ModelClient.cs for why the model client stopped being
        // this module's to own once Documents also needed one.

        // PHASE 13.4 — the AddScoped<XHandler>() lines that were here are gone.
        // AddMediator() in Program.cs registers every IRequestHandler<,> and
        // INotificationHandler<> it finds in the referenced module assemblies, so
        // a new slice is now a file and a route, with nothing to remember to
        // register. What stays below is what a mediator cannot know about.

        // That includes OnPostingDeleted, which replaces
        // `ai_analyses.PostingId ON DELETE CASCADE` (dropped at 13.3b when the two
        // tables landed in two schemas) and had its own AddScoped line here until
        // 13.4. It is discovered as an INotificationHandler<PostingDeleted>, so
        // Applications still announces a deleted posting and still never learns
        // that this module exists — Jobkeep.Contracts' ApplicationEvents.cs argues
        // that direction against the simpler contract call.

        // IPostingContract used to be registered here, on the argument that Ai
        // was its only consumer. Phase 4.5 made Documents a second one, which is
        // the condition that comment named for moving it — so it now lives in
        // AddApplicationsModule, with the module that owns the tables it guards.

        return services;
    }
}
