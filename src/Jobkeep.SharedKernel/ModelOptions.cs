namespace Jobkeep.SharedKernel;

// PHASE 13.1: ModelOptions lives in Jobkeep.SharedKernel and the registration
// stays in Jobkeep.Api. Three modules (Ai, Ats, Documents) inject these settings;
// only the composition root may name the provider that satisfies them. That is
// the same separation the comment below argues for, now enforced by the compiler
// rather than by convention — a module physically cannot reach OllamaSharp.

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
