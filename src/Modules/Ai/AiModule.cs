using Jobkeep.Modules.Applications;
using Jobkeep.Shared;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Jobkeep.Modules.Ai;

// Configuration for the analyzer, bound from the "Ai" section of appsettings.
// A plain class registered as a singleton rather than IOptions<T>: nothing here
// reloads, and IOptions buys change-notification this project has no use for
// while costing an extra unwrap in every handler.
public class AiOptions
{
    // Where Ollama is listening. Only ever localhost in this project — see the
    // provider note in AiModule below.
    public string Endpoint { get; set; } = "http://localhost:11434";

    // The model tag, e.g. "llama3.2:3b". Also written into ai_analyses.ModelUsed,
    // so a stored analysis records what produced it — useful when a later model
    // gives visibly better output and you want to know which rows are stale.
    public string Model { get; set; } = "llama3.2:3b";

    // A local 3B model on CPU is not fast. The default HttpClient timeout of 100s
    // is long enough to look like a hang and short enough to fail a cold first
    // request while the model loads into memory, which is the worst of both.
    public int TimeoutSeconds { get; set; } = 180;

    // Job ads are occasionally pasted with a whole careers page attached. See
    // AnalyzePosting.cs for why this truncates instead of rejecting.
    public int MaxDescriptionChars { get; set; } = 12000;
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
// Ollama cannot run inside a Lambda, so if Phase 3 ever unparks, the deployed
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

        // Registered as a singleton because OllamaApiClient wraps an HttpClient,
        // and a per-request HttpClient is the socket-exhaustion bug. It holds no
        // per-request state, so this is safe — and unlike a handler, it has no
        // scoped dependency to capture, so it is not the captive-dependency trap
        // ApplicationsModule warns about.
        services.AddSingleton<IChatClient>(_ =>
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri(options.Endpoint),
                Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
            };

            // OllamaSharp implements IChatClient directly, so there is no adapter
            // package in between. Microsoft.Extensions.AI.Ollama existed and was
            // deprecated in favour of exactly this.
            return new OllamaApiClient(http, options.Model);
        });

        // Scoped, matching AppDbContext — both handlers hold one.
        services.AddScoped<AnalyzePostingHandler>();
        services.AddScoped<GetAnalysisHandler>();

        // The contract Ai uses to reach Applications-owned tables. Registered
        // here rather than in ApplicationsModule because Ai is the only consumer,
        // and a contract with no consumer registered is dead wiring. If a second
        // module ever needs it, it moves.
        services.AddScoped<IPostingContract, PostingContract>();

        return services;
    }

    public static IEndpointRouteBuilder MapAiModule(this IEndpointRouteBuilder app)
    {
        // The routes sit under /applications even though the code lives in Ai.
        // URLs follow the resource a caller is thinking about — "the analysis of
        // this application" — while the code follows whichever module owns the
        // table. Those two do not have to agree, and forcing a /ai/... prefix to
        // make them agree would leak the module layout into the public API, which
        // is the thing module boundaries are supposed to be free to change.
        var group = app.MapGroup("/applications").WithTags("Ai");

        // POST /applications/{id}/analyze — runs the model and stores the result.
        // POST rather than GET because it is not safe or idempotent in the HTTP
        // sense: it writes rows, and re-running it can add skills that were not
        // there before.
        group.MapPost("/{id:guid}/analyze", async (
            Guid id,
            AnalyzePostingHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, ct)).ToHttpResult());

        // GET /applications/{id}/analysis — reads back what was stored, without
        // paying for inference again.
        group.MapGet("/{id:guid}/analysis", async (
            Guid id,
            GetAnalysisHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, ct)).ToHttpResult());

        return app;
    }
}
