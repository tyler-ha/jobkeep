using System.Text.Json;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.Extensions.AI;

namespace Jobkeep.Modules.Documents;

// The non-deterministic half: extracted text in, a proposed draft out.
//
// Nothing this class returns is trusted. Its output goes into a draft that a
// human confirms before a single real row is written — which is the difference
// between this feature and one that silently fills your database with a small
// model's best guess. See CommitImport.cs for the other side of that gate.
public interface IDocumentStructurer
{
    Task<SliceResult<StructuringOutcome>> StructureAsync(
        DocumentKind kind, string text, string label, string? sourceUrl, CancellationToken ct = default);
}

// The draft, plus anything the caller should tell the user about how it was
// produced. The warning is a property of the RUN rather than of the document —
// re-structuring a shorter text clears it — so it rides alongside the draft
// instead of being folded into it.
public record StructuringOutcome(ImportDraft Draft, string? Warning);

// The request settings, built once per document kind.
//
// All three settings below were measured in Phase 4 against llama3.2:3b, and all
// three are correctness rather than tuning — which is why none of them is a knob
// in appsettings. The full findings are in the Phase 4 doc; the short version:
//
//   1. RequireAllProperties. Microsoft.Extensions.AI's default schema marks
//      NOTHING required, which makes `{}` a legal reply — and a 3B model offered
//      that option takes it. The Phase 4 probe got a bare `{}` back in 53ms.
//      It lives on AIJsonSchemaTransformOptions, reached through
//      AIJsonSchemaCreateOptions.TransformOptions, which is not where you would
//      look for it first.
//
//   2. DisallowAdditionalProperties, so the model cannot invent sibling fields
//      that would be silently dropped on deserialize.
//
//   3. Temperature = 0. With default sampling the Phase 4 skills array came back
//      empty roughly one run in three on an unchanged input. A user re-running an
//      import and getting a different answer would reasonably call that a bug.
internal static class StructuringSchema
{
    // Declared FIRST because static field initialisers run in declaration order
    // and For<T>() below reads this one. Declared after them, it would still be
    // null when the schemas were built — enum properties would silently fall back
    // to integers and the constraint this exists to create would not be there.
    //
    // Enum values travel as names on both legs: into the schema, and back out on
    // deserialize.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly AIJsonSchemaCreateOptions Options = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            RequireAllProperties = true,
            DisallowAdditionalProperties = true
        }
    };

    // serializerOptions is passed as well as inferenceOptions, and it is load-
    // bearing rather than tidiness: it carries JsonStringEnumConverter, which is
    // what makes an enum property emit a JSON Schema `enum` of NAMES instead of an
    // integer type. That turns "please answer with one of these three words" from
    // a request in a description into a constraint the decoder enforces.
    private static ChatOptions For<T>() => new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(
            AIJsonUtilities.CreateJsonSchema(
                typeof(T), serializerOptions: Json, inferenceOptions: Options)),
        Temperature = 0
    };

    public static readonly ChatOptions Resume = For<ResumeExtraction>();
    public static readonly ChatOptions Posting = For<PostingExtraction>();

}

public class DocumentStructurer : IDocumentStructurer
{
    private readonly IChatClient _chat;
    private readonly ModelOptions _model;
    private readonly DocumentOptions _options;

    public DocumentStructurer(IChatClient chat, ModelOptions model, DocumentOptions options)
    {
        _chat = chat;
        _model = model;
        _options = options;
    }

    public async Task<SliceResult<StructuringOutcome>> StructureAsync(
        DocumentKind kind, string text, string label, string? sourceUrl, CancellationToken ct = default)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return SliceResult<StructuringOutcome>.Invalid("There is no text to structure.");

        // ---------------------------------------------------------------------
        // Truncation, and the one place Phase 4's pattern does NOT transfer
        // ---------------------------------------------------------------------
        // Phase 4 truncates a job ad from the head and says so: the useful part
        // of a pasted careers page is always at the top.
        //
        // A resume is not like that. Its education section is at the BOTTOM, and
        // head-truncation would silently drop the exact records this phase exists
        // to extract. So the limit here is set high enough that it effectively
        // never fires on a real resume — a dense three-page resume is around
        // 8,000 characters, well inside the 24,000 default — and when it does
        // fire the user is told, rather than being handed a draft that quietly
        // stops halfway.
        //
        // Raising the limit instead of getting cleverer is the right trade at
        // this size. Chunking a resume and merging per-chunk extractions is a
        // real technique and a real amount of machinery, and it would be solving
        // a problem no document in this project has.
        string? warning = null;
        if (trimmed.Length > _options.MaxStructureChars)
        {
            trimmed = trimmed[.._options.MaxStructureChars];
            warning = $"Only the first {_options.MaxStructureChars:N0} characters were read, "
                    + "so anything after that is missing from the draft. Check the end of the document.";
        }

        var prompt = BuildPrompt(kind, trimmed);
        var chatOptions = kind == DocumentKind.Resume ? StructuringSchema.Resume : StructuringSchema.Posting;

        ChatResponse response;
        try
        {
            response = await _chat.GetResponseAsync(prompt, chatOptions, ct);
        }
        // The `ct` guard keeps a caller who hung up out of this branch. A cancelled
        // request surfaces as TaskCanceledException too, and without the guard an
        // ordinary browser navigation away from the upload page was reported — and
        // logged — as "Ollama is down", which is a false alarm about the one
        // dependency most likely to actually be down. Cancellation is rethrown and
        // handled as cancellation.
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // The model server being down is operational, not the caller's fault,
            // and there is no SliceResult status for "dependency unavailable".
            // Adding one would change every surface's translation, so this phase
            // does what Phase 4 did: rethrow and let the pipeline answer 500,
            // which is the honest answer. Note the extracted text is already safe
            // in the row by the time this can happen — see ImportDocument.cs.
            throw new InvalidOperationException(
                $"The model at {_model.Endpoint} did not respond. Is `ollama serve` running?", ex);
        }

        try
        {
            var draft = kind switch
            {
                DocumentKind.Resume => BuildResumeDraft(response.Text, label),
                _ => BuildPostingDraft(response.Text, text, sourceUrl)
            };

            return draft is null
                ? SliceResult<StructuringOutcome>.Invalid(
                    "The model returned nothing usable for this document.")
                : SliceResult<StructuringOutcome>.Ok(new StructuringOutcome(draft, warning));
        }
        catch (JsonException)
        {
            // Constrained decoding makes a malformed reply very unlikely, but
            // "very unlikely" is not "impossible" and it should not be a 500.
            return SliceResult<StructuringOutcome>.Invalid(
                "The model's reply could not be read as the expected shape.");
        }
    }

    private ImportDraft? BuildResumeDraft(string json, string label)
    {
        var extraction = JsonSerializer.Deserialize<ResumeExtraction>(json, StructuringSchema.Json);
        return extraction is null ? null : new ImportDraft(DraftMapper.ToDraft(extraction, label), null);
    }

    private ImportDraft? BuildPostingDraft(string json, string fullText, string? sourceUrl)
    {
        var extraction = JsonSerializer.Deserialize<PostingExtraction>(json, StructuringSchema.Json);
        if (extraction is null) return null;

        // The description is the FULL extracted text, not the truncated copy the
        // model saw and not a model-written summary. Two reasons, and the second
        // is the one that matters: the Phase 4 analyzer reads
        // job_postings.Description and re-runs its own extraction over it, so a
        // summary here would mean Phase 4 analyzing Phase 4.5's paraphrase of the
        // ad instead of the ad. Storing the original keeps every later feature
        // reading the source document.
        return new ImportDraft(null, DraftMapper.ToDraft(extraction, fullText, sourceUrl));
    }

    // The prompt carries the TASK. Every field-level instruction is in a
    // [Description] attribute on the extraction classes instead, because Phase 4
    // measured what happens otherwise: a small model reads prompt guidance as the
    // answer and returns the instructions as the field values.
    //
    // Fencing the document is load-bearing in a duller way. Without a delimiter
    // the model treats the instructions and the document as one text and starts
    // extracting skills out of the instructions — "JSON" turns up in the skills
    // list. The fence is why it does not.
    private static string BuildPrompt(DocumentKind kind, string text) => kind switch
    {
        DocumentKind.Resume => $"""
            Read the resume below and fill in every field from what it actually says.
            Copy dates and job titles exactly as written. Do not calculate totals,
            do not judge seniority, and do not add anything the resume does not contain.
            If the resume does not mention something, use an empty string or an empty list.

            ### RESUME
            {text}
            ### END
            """,

        _ => $"""
            Read the whole job advertisement below and fill in every field.
            List every technology named anywhere in it, including the ones in the
            responsibilities. List every requirement, responsibility and benefit
            it states as its own separate entry.
            Only include technologies that literally appear in the text.

            ### JOB ADVERTISEMENT
            {text}
            ### END
            """
    };
}
