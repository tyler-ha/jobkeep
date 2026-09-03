using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jobkeep.Models;
using Jobkeep.Modules.Applications;
using Jobkeep.Shared;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Jobkeep.Modules.Ai;

// Slice: read a posting's description, ask a model what it says, and store the
// answer as structured rows.
//
// The module owns `ai_analyses` and nothing else. The posting text it reads and
// the `posting_skills` rows it writes both belong to Applications, and it reaches
// them through IPostingContract rather than through its own context — see the long
// comment in Modules/Applications/PostingContract.cs for why a write across a
// module boundary could not reuse the Analytics exception.

// A response DTO, not the AiAnalysis entity — same rule as every other slice
// (architecture.md A2). It also carries SkillsAdded, which the entity has no
// column for, because "what did this run actually change" is a property of the
// run rather than of the analysis.
public record AiAnalysisResponse(
    Guid PostingId,
    SeniorityLevel Seniority,
    string? Summary,
    string? ModelUsed,
    DateTime AnalyzedAtUtc,
    IReadOnlyList<ExtractedSkillResponse> Skills,
    int SkillsAdded);

public record ExtractedSkillResponse(string Name, bool IsRequired);

// The shape handed to the model, and the shape parsed back out of it.
//
// Every field description lives in a [Description] attribute rather than in the
// prompt text, and that is not a style preference — it was measured. With the
// guidance in the prompt, llama3.2:3b echoed the instructions back as the *values*
// ("seniority": "one of Unknown, Junior, Mid, ..."). In the attributes it becomes
// part of the JSON schema the model is constrained by, and the echo stops.
//
// Seniority is a string even though the column is a SeniorityLevel enum. Binding
// the enum directly means a model answering "Mid-Senior" fails the whole parse and
// loses the summary and the skills with it. As a string it degrades to Unknown and
// keeps everything else. Also load-bearing in a duller way: the model reliably
// answers lowercase "senior", which only survives because the parse below is
// case-insensitive.
//
// The properties are non-nullable on purpose. See AiSchema for why that is the
// difference between a working analyzer and one that stores empty rows.
internal sealed class AnalysisDraft
{
    [Description("Seniority of the role: one of Unknown, Junior, Mid, Senior, Lead, Principal.")]
    public string Seniority { get; set; } = "";

    [Description("A summary of the role in 2 to 3 complete sentences. Say what the company does, "
               + "what the person would work on, and where the job is located. "
               + "Do not answer with a job title.")]
    public string Summary { get; set; } = "";

    [Description("Every skill named in the advertisement — both technologies "
               + "(languages, frameworks, tools, cloud services, databases) and soft skills "
               + "(communication, stakeholder management, teamwork and the like).")]
    public List<DraftSkill> Skills { get; set; } = new();
}

internal sealed class DraftSkill
{
    // See the twin of this field in Documents/ImportDraft.cs for the measurement
    // behind the wording: without "not the sentence it appears in", a 3B model
    // returns the ad's own phrasing and every adjective becomes a new skill row.
    [Description("The name of the skill itself, not the sentence it appears in. "
               + "Write \"Communication\", not \"Excellent communication skills\"; "
               + "\"CI/CD\", not \"CI/CD pipelines\". "
               + "For example: C#, PostgreSQL, Kubernetes, Stakeholder Management.")]
    public string Name { get; set; } = "";

    // PHASE 14. Mirrors PostingSkillExtraction in the Documents module, which is
    // duplication worth being honest about: two model callers read the same kind
    // of document and both need the same field, but they are different modules
    // and a shared extraction class would be a reference one of them is not
    // allowed to have. The DTO is copied; the ENUM is not — SkillKind is in
    // Contracts precisely so both can name the same type.
    //
    // AiSchema.Json does NOT carry JsonStringEnumConverter, unlike the Documents
    // side, so this property is added to that converter list below — without it
    // the schema would emit an integer and the model would be guessing ordinals.
    [Description("Technical for a technology, language, framework, tool or database. "
               + "Soft for a way of working or an interpersonal strength.")]
    public SkillKind Kind { get; set; } = SkillKind.Unknown;

    [Description("True if the advertisement lists it as required or essential; "
               + "false if it is nice to have.")]
    public bool Required { get; set; }
}

// The request settings, built once. Both of these were arrived at by measurement
// against llama3.2:3b, and both are the difference between usable output and
// unusable output — so neither is a knob in appsettings. They are correctness.
internal static class AiSchema
{
    // RequireAllProperties is the important one. Microsoft.Extensions.AI's default
    // schema marks *nothing* required, which makes `{}` a legal reply — and a 3B
    // model, offered that option, takes it: the probe got a bare `{}` back in 53ms.
    // With the properties required, the same model returns a real summary and
    // eight or nine correctly-flagged skills. Same model, same prompt; the schema
    // was doing all the work.
    //
    // DisallowAdditionalProperties keeps the model from inventing sibling fields
    // that would be silently dropped on deserialize.
    private static readonly AIJsonSchemaCreateOptions SchemaOptions = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            RequireAllProperties = true,
            DisallowAdditionalProperties = true
        }
    };

    // PHASE 14 MOVED THIS ABOVE Schema, and the move is load-bearing rather than
    // tidiness. Static field initialisers run in DECLARATION ORDER, so Schema
    // reading a Json declared below it would read null — enum properties would
    // silently fall back to integers and the constraint would not be there. The
    // Documents module hit this first and its StructuringSchema carries the same
    // warning; this is the second instance, which is what makes it a trap rather
    // than an anecdote.
    //
    // JsonStringEnumConverter is what turns DraftSkill.Kind into a JSON Schema
    // `enum` of NAMES instead of an integer type — so "answer with one of these
    // two words" becomes something constrained decoding enforces, not something
    // the description asks for politely.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // serializerOptions is passed as well as inferenceOptions, and it carries the
    // converter above. Without it the enum is an integer on both legs.
    private static readonly JsonElement Schema = AIJsonUtilities.CreateJsonSchema(
        typeof(AnalysisDraft), serializerOptions: Json, inferenceOptions: SchemaOptions);

    public static ChatOptions Options { get; } = new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema),

        // Extraction is not a creative task, and the default sampling made this
        // measurably unreliable: across repeated runs on one unchanged ad, the
        // skills array came back empty roughly one run in three. At 0 the same ad
        // produced identical output three times running. A user re-analyzing a
        // posting and getting a different answer would reasonably read it as a bug.
        Temperature = 0
    };

}

public record AnalyzePosting(Guid ApplicationId) : IRequest<SliceResult<AiAnalysisResponse>>;

public class AnalyzePostingHandler : IRequestHandler<AnalyzePosting, SliceResult<AiAnalysisResponse>>
{
    private readonly AiDbContext _db;
    private readonly IChatClient _chat;
    private readonly IPostingContract _postings;
    private readonly AiOptions _options;
    // The endpoint and model tag moved to ModelOptions in Phase 4.5 when a second
    // module started calling a model; AiOptions keeps only what is this module's.
    private readonly ModelOptions _model;

    // IChatClient is the abstraction the whole phase is built around: Ollama
    // today, a hosted provider later, decided in AiModule.cs by configuration.
    // Nothing below this line knows which one it is talking to.
    public AnalyzePostingHandler(
        AiDbContext db, IChatClient chat, IPostingContract postings,
        AiOptions options, ModelOptions model)
    {
        _db = db;
        _chat = chat;
        _postings = postings;
        _options = options;
        _model = model;
    }

    public async ValueTask<SliceResult<AiAnalysisResponse>> Handle(
        AnalyzePosting message, CancellationToken ct)
    {
        var applicationId = message.ApplicationId;
        var content = await _postings.GetContentAsync(applicationId, ct);
        if (content is null)
            return SliceResult<AiAnalysisResponse>.NotFound($"Application {applicationId} not found.");

        var description = content.Description?.Trim();
        if (string.IsNullOrEmpty(description))
            return SliceResult<AiAnalysisResponse>.Invalid(
                "This posting has no description to analyze. Add one first.");

        // Truncate rather than reject. A 3B model has a modest context window and
        // a pasted job ad occasionally arrives with a whole careers page attached;
        // the useful content is always at the top. Silently sending 40k characters
        // to a local model is how this turns into a 90-second request.
        if (description.Length > _options.MaxDescriptionChars)
            description = description[.._options.MaxDescriptionChars];

        // The prompt carries the *task*; the field-level guidance is in the schema
        // (see AnalysisDraft). Delimiting the ad matters more than it looks: without
        // a fence the model treats the instructions and the advertisement as one
        // document and starts extracting skills out of the instructions.
        var prompt = $"""
            Read the job advertisement below and fill in every field.
            Write the summary as 2 to 3 complete sentences, not a job title.
            Only include technologies that literally appear in the text.

            ### JOB ADVERTISEMENT
            {description}
            ### END
            """;

        ChatResponse response;
        try
        {
            response = await _chat.GetResponseAsync(prompt, AiSchema.Options, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The model server being down is the most likely failure here, and it
            // is an operational problem rather than a bad request — so it must not
            // read as Invalid, which both surfaces would present as the caller's
            // fault. There is no SliceResult status for "dependency unavailable",
            // and adding one changes every surface's translation, so this phase
            // does not introduce it. Rethrow and let the pipeline answer 500,
            // which is the honest answer.
            throw new InvalidOperationException(
                $"The model at {_model.Endpoint} did not respond. Is `ollama serve` running?", ex);
        }

        AnalysisDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<AnalysisDraft>(response.Text, AiSchema.Json);
        }
        catch (JsonException)
        {
            // Constrained decoding makes this very unlikely, but "very unlikely"
            // is not "impossible" and a malformed reply should not be a 500.
            draft = null;
        }

        if (draft is null)
            return SliceResult<AiAnalysisResponse>.Invalid(
                "The model returned nothing usable for this description.");

        // Unparseable seniority degrades to Unknown rather than failing the run.
        // ignoreCase is not optional here — the model answers "senior", not "Senior".
        var seniority = Enum.TryParse<SeniorityLevel>(draft.Seniority, ignoreCase: true, out var parsed)
            ? parsed
            : SeniorityLevel.Unknown;

        var skills = draft.Skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new ExtractedSkill(s.Name.Trim(), s.Required))
            .ToList();

        // ai_analyses is 1:1 with the posting, so a re-run updates in place
        // instead of inserting a second row — the FK is unique and a second
        // insert would throw. Re-analyzing after editing the description is a
        // normal thing to do, so it has to be an update path, not an error.
        var analysis = await _db.AiAnalyses
            .FirstOrDefaultAsync(a => a.PostingId == content.PostingId, ct);

        if (analysis is null)
        {
            analysis = new AiAnalysis { PostingId = content.PostingId };
            _db.AiAnalyses.Add(analysis);
        }

        analysis.Seniority = seniority;
        analysis.Summary = string.IsNullOrWhiteSpace(draft.Summary) ? null : draft.Summary.Trim();
        analysis.ModelUsed = _model.Model;   // recorded so a later re-read knows what produced it
        analysis.AnalyzedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // Skills go through the contract, and after the analysis row is saved:
        // if the skill write fails, the summary survives and a re-run is cheap.
        // The reverse order would lose the inference on a duplicate-key error.
        var added = await _postings.AddExtractedSkillsAsync(content.PostingId, skills, ct);

        return SliceResult<AiAnalysisResponse>.Ok(new AiAnalysisResponse(
            analysis.PostingId,
            analysis.Seniority,
            analysis.Summary,
            analysis.ModelUsed,
            analysis.AnalyzedAtUtc,
            skills.Select(s => new ExtractedSkillResponse(s.Name, s.IsRequired)).ToList(),
            added));
    }
}
