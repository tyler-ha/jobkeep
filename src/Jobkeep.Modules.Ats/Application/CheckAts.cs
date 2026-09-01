using System.ComponentModel;
using System.Text.Json;
using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Jobkeep.Modules.Ats;

// Slice: compare a stored resume against the posting an application points at,
// and report what the job asks for that the resume never mentions.
//
// ---------------------------------------------------------------------------
// Four stages, and only one of them costs anything
// ---------------------------------------------------------------------------
//
//   1. resolve      which application, which resume          two queries
//   2. skill gap    posting_skills minus resume_skills       ONE query, no model
//   3. requirements free-text coverage                       one model call
//   4. formatting   static rules over the resume's metadata  no query, no model
//
// The shape matters more than the code. Three quarters of this feature has no AI
// dependency at all, so the whole thing keeps working when Ollama is not running —
// stage 3 degrades to a warning instead of failing the request. That is pinned by
// a test, because a feature that quietly becomes useless when a dependency is down
// is a feature that will be discovered broken at the worst moment.
//
// ---------------------------------------------------------------------------
// Why the skill gap is a SQL join and not a model call
// ---------------------------------------------------------------------------
// The Phase 5 doc originally said to prompt the model for keyword matching. That
// is corrected in this phase, and the reason is a decision taken three phases
// earlier: `skills` is a SHARED table. Posting skills and resume skills are rows
// in the same table joined on the same SkillId, so "what does this ad ask for
// that my resume never mentions" is a set difference Postgres computes exactly,
// instantly and free. A model asked the same question would be slower, cost a
// call, and — measured in Phase 4 — not be reproducible: Temperature = 0 gave
// identical output on 6 of 7 runs, not 7 of 7.
//
// This is the query the shared-skills decision was made for, and until this slice
// existed it had never been exercised.
//
// ---------------------------------------------------------------------------
// Why there is no score
// ---------------------------------------------------------------------------
// Deliberate, and the real-CV test on 2026-08-28 is what makes the argument
// concrete rather than a preference. The same CV as a designed PDF lost the
// candidate's full name, their location and every real skill; as an ordinary
// .docx it got all three right. The biggest ATS risk in that document was never
// keyword coverage — it was that a machine reading the file could not find who
// the candidate was. A number out of 100 would have averaged that away into a
// digit. A list of specific missing things cannot.

// The response DTO. Not the AtsResult entity — same rule as every other slice
// (architecture.md A2).
public record AtsCheckResponse(
    Guid ApplicationId,
    Guid? ResumeId,
    string? ResumeLabel,
    IReadOnlyList<string> MatchedSkills,
    IReadOnlyList<string> MissingMustHaveSkills,
    IReadOnlyList<string> MissingNiceToHaveSkills,
    IReadOnlyList<string> UnmetRequirements,
    IReadOnlyList<string> FormattingRiskNotes,
    string? Warning,
    DateTime CheckedAtUtc);

// What stage 1 needs about the resume, in one projection. Includes the
// experience count because stage 4's "implausibly short" rule is relative to it.
internal sealed record ResumeFacts(
    Guid Id,
    string Label,
    string? FullName,
    string? Email,
    string? Location,
    SourceFormat? SourceFormat,
    string SourceText,
    int ExperienceCount);

// One posting skill plus whether the resume has it. `OnResume` is computed by
// Postgres as a correlated EXISTS, so the set difference happens in the database
// and this side only buckets the rows.
internal sealed record SkillMatch(string Name, bool IsRequired, bool OnResume);

// The shape the model is constrained to for stage 3.
//
// Every field description is a [Description] attribute rather than prompt text,
// for the reason AnalyzePosting.cs measured: guidance left in the prompt comes
// back as the *values*.
internal sealed class CoverageDraft
{
    [Description("The numbers of the requirements that the resume clearly provides evidence for. "
               + "Only include a number if the resume actually shows it.")]
    public List<int> EvidencedRequirementNumbers { get; set; } = new();
}

public class CheckAtsHandler
{
    // A resume long enough to say something and short enough for a 3B model's
    // context window. The real CV this was built against is 3,262 characters, so
    // this truncates nothing realistic; it exists because SourceText is verbatim
    // extracted text and a badly-parsed 20-page PDF is not a bounded quantity.
    private const int MaxResumeChars = 12000;

    // Requirements are one line each and a real ad has five to fifteen. Past
    // roughly this many, the document being checked is not a job ad.
    private const int MaxRequirements = 40;

    // Stage 4's "implausibly short" rule, and both numbers are measurements
    // rather than taste. The real CV extracted to 3,262 characters across three
    // roles — about 1,090 per role — so 200 per role is generous enough that
    // tripping it means real content was lost. The 400-character floor catches the
    // resume with no parsed experience at all, which is the designed-PDF failure.
    private const int MinCharsPerExperience = 200;
    private const int MinPlausibleResumeChars = 400;

    private readonly AppDbContext _db;
    private readonly IChatClient _chat;
    private readonly ModelOptions _model;

    public CheckAtsHandler(AppDbContext db, IChatClient chat, ModelOptions model)
    {
        _db = db;
        _chat = chat;
        _model = model;
    }

    public async Task<SliceResult<AtsCheckResponse>> HandleAsync(
        Guid applicationId, Guid? resumeId = null, CancellationToken ct = default)
    {
        // ---------------------------------------------------------------
        // Stage 1 — resolve
        // ---------------------------------------------------------------
        var application = await _db.JobApplications
            .Where(a => a.Id == applicationId)
            .Select(a => new { a.PostingId, a.ResumeId })
            .FirstOrDefaultAsync(ct);

        if (application is null)
            return SliceResult<AtsCheckResponse>.NotFound($"Application {applicationId} not found.");

        // The argument wins over the link, so you can check the same application
        // against a second resume without editing it. The link is the default
        // because that is the resume you actually sent.
        var wantedResumeId = resumeId ?? application.ResumeId;

        if (wantedResumeId is null)
            return SliceResult<AtsCheckResponse>.Invalid(
                "This application is not linked to a resume, and no resumeId was supplied. "
              + "Link one to the application or pass ?resumeId= to check against a specific resume.");

        var resume = await _db.Resumes
            .Where(r => r.Id == wantedResumeId.Value)
            .Select(r => new ResumeFacts(
                r.Id, r.Label, r.FullName, r.Email, r.Location,
                r.SourceFormat, r.SourceText,
                r.Experiences.Count))
            .FirstOrDefaultAsync(ct);

        // Invalid, not NotFound: the application in the route exists, so what is
        // wrong is the id the caller supplied. Mirrors the check the Phase 4.5
        // review added to CreateApplication.cs, which had the same choice to make.
        if (resume is null)
            return SliceResult<AtsCheckResponse>.Invalid($"Resume {wantedResumeId} not found.");

        // ---------------------------------------------------------------
        // Stage 2 — the skill gap. One query, deterministic, free.
        // ---------------------------------------------------------------
        // A flat projection, not an Include: this needs the skill's name and
        // nothing else about it (architecture.md decision 11 / A1). The EXISTS is
        // the set difference, evaluated by Postgres — the rows that come back are
        // already labelled, and the C# below only sorts them into buckets.
        // The OrderBy has to come BEFORE the Select, and that is not a style
        // choice: ordering by a property of the projected record makes EF try to
        // translate `ORDER BY new SkillMatch(...)`, which it cannot, and the whole
        // query fails at runtime with a translation error rather than at compile
        // time. Ordering by the column instead is the same SQL and translates.
        var matches = await _db.PostingSkills
            .Where(ps => ps.PostingId == application.PostingId)
            .OrderBy(ps => ps.Skill.Name)
            .Select(ps => new SkillMatch(
                ps.Skill.Name,
                ps.IsRequired,
                _db.ResumeSkills.Any(rs => rs.ResumeId == resume.Id && rs.SkillId == ps.SkillId)))
            .ToListAsync(ct);

        var matched = matches.Where(m => m.OnResume).Select(m => m.Name).ToList();
        var missingMustHave = matches.Where(m => !m.OnResume && m.IsRequired).Select(m => m.Name).ToList();
        var missingNiceToHave = matches.Where(m => !m.OnResume && !m.IsRequired).Select(m => m.Name).ToList();

        // ---------------------------------------------------------------
        // Stage 3 — free-text requirements. The one model call.
        // ---------------------------------------------------------------
        var requirements = await _db.JobRequirements
            .Where(r => r.PostingId == application.PostingId)
            .OrderBy(r => r.IsMustHave ? 0 : 1)
            .ThenBy(r => r.Text)
            .Take(MaxRequirements)
            .Select(r => new { r.Text, r.IsMustHave })
            .ToListAsync(ct);

        var unmet = new List<string>();
        string? warning = null;

        if (requirements.Count > 0 && !string.IsNullOrWhiteSpace(resume.SourceText))
        {
            var coverage = await AssessRequirementsAsync(
                requirements.Select(r => r.Text).ToList(), resume.SourceText, ct);

            if (coverage is null)
            {
                // Degrade, do not fail. The skill gap and the formatting notes are
                // already computed and are three quarters of the answer; throwing
                // now would discard them because one optional stage could not run.
                // AnalyzePosting rethrows in the same situation, and it is right to:
                // there, the model IS the feature. Here it is one stage of four.
                warning = $"The model at {_model.Endpoint} did not respond, so the written "
                        + "requirements were not assessed. The skill gap and formatting notes below "
                        + "are complete — they do not use a model. Is `ollama serve` running?";
            }
            else
            {
                // Anything the model did not name as evidenced is reported as
                // unmet. That direction is chosen deliberately: a requirement
                // wrongly listed as unmet is a line the user reads and dismisses,
                // while one wrongly dropped is a gap they never learn about. This
                // tool exists to surface gaps, so it errs towards showing them.
                var evidenced = coverage.ToHashSet();
                unmet = requirements
                    .Where((_, i) => !evidenced.Contains(i + 1))
                    .Select(r => r.Text)
                    .ToList();
            }
        }

        // ---------------------------------------------------------------
        // Stage 4 — formatting. Static rules, each one a measured finding.
        // ---------------------------------------------------------------
        var formatNotes = BuildFormatNotes(resume);

        // ---------------------------------------------------------------
        // Store. 1:1 with the application — re-checking overwrites, latest wins,
        // the shape ai_analyses already uses. ResumeId is what records which
        // resume the surviving row judged.
        // ---------------------------------------------------------------
        var stored = await _db.AtsResults.FirstOrDefaultAsync(r => r.ApplicationId == applicationId, ct);
        if (stored is null)
        {
            stored = new AtsResult { ApplicationId = applicationId };
            _db.AtsResults.Add(stored);
        }

        stored.ResumeId = resume.Id;
        stored.MatchedKeywords = matched;
        stored.MissingMustHaveKeywords = missingMustHave;
        stored.MissingNiceToHaveKeywords = missingNiceToHave;
        stored.UnmetRequirements = unmet;
        stored.FormattingRiskNotes = formatNotes;
        stored.Warning = warning;
        stored.CheckedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return SliceResult<AtsCheckResponse>.Ok(new AtsCheckResponse(
            applicationId,
            resume.Id,
            resume.Label,
            matched,
            missingMustHave,
            missingNiceToHave,
            unmet,
            formatNotes,
            warning,
            stored.CheckedAtUtc));
    }

    // Returns the 1-based numbers of the requirements the model says the resume
    // evidences, or null when the model could not be reached.
    private async Task<List<int>?> AssessRequirementsAsync(
        List<string> requirements, string resumeText, CancellationToken ct)
    {
        if (resumeText.Length > MaxResumeChars)
            resumeText = resumeText[..MaxResumeChars];

        var numbered = string.Join("\n",
            requirements.Select((text, i) => $"{i + 1}. {text}"));

        // Both blocks are fenced. AnalyzePosting.cs found that without a fence a
        // small model treats the instructions and the document as one text and
        // starts answering about the instructions; here there are two documents
        // to keep apart as well as the task.
        var prompt = $"""
            Below is a list of numbered requirements from a job advertisement, and
            the text of a candidate's resume. Decide which requirements the resume
            provides clear evidence for.

            Only list a number if the resume actually shows it. Do not assume a
            requirement is met because it is common or because the role sounds
            similar.

            ### REQUIREMENTS
            {numbered}
            ### END REQUIREMENTS

            ### RESUME
            {resumeText}
            ### END RESUME
            """;

        ChatResponse response;
        try
        {
            response = await _chat.GetResponseAsync(prompt, AtsSchema.Options, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // The guard on TaskCanceledException matters: a caller abandoning the
            // request surfaces as one too, and swallowing that would report a
            // cancelled request as a model outage. Same guard DocumentStructurer
            // added for the same reason.
            return null;
        }

        try
        {
            var draft = JsonSerializer.Deserialize<CoverageDraft>(response.Text, AtsSchema.Json);
            // A malformed reply is treated as "evidenced nothing" rather than as an
            // outage: the model answered, it just answered unusably, and reporting
            // every requirement as unmet is the direction this stage already errs in.
            return draft?.EvidencedRequirementNumbers
                        .Where(n => n >= 1 && n <= requirements.Count)
                        .Distinct()
                        .ToList()
                   ?? new List<int>();
        }
        catch (JsonException)
        {
            return new List<int>();
        }
    }

    // Static formatting rules. Every one of these fires on something the real-CV
    // test on 2026-08-28 actually observed, which is the difference between this
    // and the generic "avoid tables and columns" advice on every careers blog.
    private static List<string> BuildFormatNotes(ResumeFacts resume)
    {
        var notes = new List<string>();

        if (resume.SourceFormat == Models.SourceFormat.Pdf)
            notes.Add(
                "This resume was imported from a PDF. Tested on this project's own CV, the same "
              + "document as a designed PDF lost the candidate's name, location and every listed "
              + "skill, while the plain .docx version kept all three — PDF layout is drawn, not "
              + "structured, so a parser reads it in whatever order the page was painted. If an "
              + "employer accepts .docx, send that.");

        // Precisely the three fields the designed PDF lost, which is why this rule
        // catches the real failure rather than a hypothetical one.
        var missingContact = new List<string>();
        if (string.IsNullOrWhiteSpace(resume.FullName)) missingContact.Add("name");
        if (string.IsNullOrWhiteSpace(resume.Email)) missingContact.Add("email address");
        if (string.IsNullOrWhiteSpace(resume.Location)) missingContact.Add("location");

        if (missingContact.Count > 0)
            notes.Add(
                $"The import could not find your {string.Join(", ", missingContact)} in this resume. "
              + "An ATS reads the same text this did, so a recruiter's system may not have them "
              + "either. Put them as plain lines at the top of the document, outside any header, "
              + "text box or table.");

        var floor = Math.Max(
            MinPlausibleResumeChars,
            resume.ExperienceCount * MinCharsPerExperience);

        if (resume.SourceText.Length < floor)
            notes.Add(
                $"Only {resume.SourceText.Length} characters of text were extracted from this "
              + $"resume, across {resume.ExperienceCount} listed role(s). That is short enough to "
              + "suggest the extraction lost content rather than that the resume is brief — the "
              + "usual cause is text inside tables, columns, headers or images. Re-import it as "
              + "a .docx and compare.");

        return notes;
    }
}

// Request settings for stage 3, built once. Same construction and the same
// reasoning as AiSchema in AnalyzePosting.cs — see that file for what was measured.
internal static class AtsSchema
{
    // RequireAllProperties is the load-bearing one: without it the generated schema
    // marks nothing required, `{}` becomes a legal reply, and a 3B model offered
    // that option takes it. Here that would silently read as "the resume evidences
    // no requirement at all", which is a wrong answer rather than an obvious failure.
    private static readonly AIJsonSchemaCreateOptions SchemaOptions = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            RequireAllProperties = true,
            DisallowAdditionalProperties = true
        }
    };

    private static readonly JsonElement Schema =
        AIJsonUtilities.CreateJsonSchema(typeof(CoverageDraft), inferenceOptions: SchemaOptions);

    public static ChatOptions Options { get; } = new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema),

        // Judging evidence is not a creative task. Note what Phase 4 measured and
        // this file does not pretend otherwise: Temperature = 0 gave identical
        // output on 6 of 7 runs, not 7 of 7, the outlier being the first inference
        // after the model loaded. It reduces variance; it does not remove it. That
        // residue is exactly why the skill gap above is a SQL join instead.
        Temperature = 0
    };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
