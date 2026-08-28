using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Jobkeep.Shared;

// Where the language model is configured, for every module that wants one.
//
// ---------------------------------------------------------------------------
// Why this moved out of the Ai module in Phase 4.5
// ---------------------------------------------------------------------------
// Phase 4 registered IChatClient inside AddAiModule, which was right while
// exactly one module called a model. Phase 4.5 added a second — the Documents
// module structures an uploaded resume — and that forced the question the Phase
// 4.5 plan predicted:
//
//   **The Ai module is not "the module where model calls live".** It owns the
//   `ai_analyses` table. IChatClient is a shared dependency any module may
//   inject, exactly like AppDbContext is.
//
// Getting that backwards has a specific and familiar failure mode: Ai grows a
// slice every time any feature anywhere wants a model, its handlers accumulate
// use cases belonging to other parts of the app, and it ends up as
// IJobApplicationRepository wearing a different hat — the same interface that
// died in Phase 2.3 for the same reason. Leaving the registration in AiModule
// would also have made Documents silently depend on AddAiModule having been
// called, which is a coupling nothing in Program.cs would show.
//
// So the *technology* is registered here, and each module owns its own tables,
// its own prompts and its own schemas.
public class ModelOptions
{
    // Where Ollama is listening. Only ever localhost in this project — the
    // constraint and its cost are documented in Modules/Ai/AiModule.cs.
    public string Endpoint { get; set; } = "http://localhost:11434";

    // The model tag, e.g. "llama3.2:3b". Written into ai_analyses.ModelUsed and
    // document_imports.ModelUsed, so a stored row records what produced it —
    // useful when a better model arrives and you want to know which rows are stale.
    public string Model { get; set; } = "llama3.2:3b";

    // A local 3B model on CPU is not fast, and the first request after boot also
    // pays for loading the weights. The default HttpClient timeout of 100s is
    // long enough to look like a hang and short enough to fail that first call,
    // which is the worst of both.
    public int TimeoutSeconds { get; set; } = 180;
}

public static class ModelClientRegistration
{
    // Bound from the "Ai" configuration section, which is left named that way
    // deliberately: renaming it would break every appsettings file and every
    // deployment environment variable to express a code-layout change that
    // nothing outside the process cares about.
    public static IServiceCollection AddModelClient(
        this IServiceCollection services, IConfiguration config)
    {
        var options = new ModelOptions();
        config.GetSection("Ai").Bind(options);
        services.AddSingleton(options);

        // Singleton because OllamaApiClient wraps an HttpClient, and a
        // per-request HttpClient is the socket-exhaustion bug. It holds no
        // per-request state, and unlike a slice handler it has no scoped
        // dependency to capture — so this is not the captive-dependency trap
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

        return services;
    }
}
