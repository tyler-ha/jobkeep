using System.ComponentModel;
using System.Text.Json;
using Jobkeep.Models;
using Jobkeep.Modules.Applications;

namespace Jobkeep.Modules.Documents;

// ---------------------------------------------------------------------------
// The draft: one shape, three jobs
// ---------------------------------------------------------------------------
// These records are what the structuring step proposes, what the review endpoint
// returns, what the user sends back corrected, and what the commit reads. One
// shape for all four on purpose — a separate "proposal" type and "correction"
// type would be identical, and the moment they drifted the review screen would
// be able to express an edit the commit could not apply.
//
// Both halves are nullable and exactly one is populated, decided by the import's
// Kind. A discriminated union would model that better in C#, but this has to
// cross a GraphQL schema, where a union of two object types forces every client
// into `... on ResumeDraft` fragments for a discriminator they already know from
// the Kind field. Two nullable fields is the shape that reads well on both
// surfaces, and the invariant is enforced in the handler rather than the type.
public record ImportDraft(ResumeDraft? Resume, PostingDraft? Posting);

// What a resume becomes. Every field is optional and stays optional through the
// commit: a resume with no phone number is normal, and a parse that misses one
// is not a reason to refuse the import.
public record ResumeDraft(
    // What the user will call this version — "backend-focused". Not proposed by
    // the model: it is a decision about how YOU organise your resumes, and the
    // document contains no evidence about it. Seeded from the filename and
    // changed at review.
    string Label,
    string? FullName,
    string? Email,
    string? Phone,
    string? Location,
    string? Headline,
    List<string> Skills,
    // PHASE 14. Additive on the wire: `skills` keeps its meaning (technical) and
    // this appears beside it, so nothing reading the draft today breaks. Both
    // lists become resume_skills rows at commit — the split exists to carry Kind
    // into the catalogue, not to store two kinds of link.
    List<string> SoftSkills,
    List<DraftExperience> Experience,
    List<DraftEducation> Education);

// Dates are strings, carried through unparsed from the document. See
// Models/Resume.cs — transcribing "Mar 2021" beats guessing a DateOnly.
public record DraftExperience(
    string Employer,
    string? Title,
    string? Start,
    string? End,
    List<string> Highlights);

public record DraftEducation(
    string Institution,
    string? Qualification,
    string? Year);

// What a job ad becomes: the arguments to the application-creating use case that
// already exists, plus the two child collections that have their own slices.
public record PostingDraft(
    string Company,
    string Title,
    string? Location,
    string? Description,
    string? SourceUrl,
    List<DraftPostingSkill> Skills,
    List<DraftRequirement> Requirements);

public record DraftPostingSkill(string Name, bool Required, SkillKind Kind = SkillKind.Unknown);

// PHASE 13.3b — Kind is the CONTRACT enum, not the entity one.
//
// RequirementKind is Applications' entity enum and moved into Applications
// with job_requirements; Documents may not reference it. A draft is not an
// entity anyway, so this is the more honest type of the two: the draft's job
// is to become an argument to IApplicationContract.CommitPostingAsync, and
// that argument has always been PostingRequirementKind.
//
// Nothing on the wire moved. The member names are identical on both enums, so
// the REST payload and the persisted DraftJson are byte-identical; only the
// GraphQL *type name* for a draft requirement changed.
public record DraftRequirement(string Text, PostingRequirementKind Kind, bool IsMustHave);

// ---------------------------------------------------------------------------
// The model-facing shapes
// ---------------------------------------------------------------------------
// Separate from the records above, for the same reason Phase 4's AnalysisDraft is
// separate from AiAnalysisResponse: these exist to be turned into a JSON schema
// the model is constrained by, which imposes requirements the public shape should
// not inherit — mutable properties, no constructor, and non-nullable everywhere.
//
// Every field description lives in a [Description] attribute rather than in the
// prompt, and that is measured rather than stylistic. Phase 4 found that with the
// guidance in the prompt text, llama3.2:3b echoes the instructions back as the
// VALUES ("seniority": "one of Unknown, Junior, ..."). In the attributes it
// becomes part of the schema, and the echo stops. All three of Phase 4's findings
// transfer unchanged; see AiSchema in Modules/Ai/AnalyzePosting.cs.

internal sealed class ResumeExtraction
{
    [Description("The person's full name as written at the top of the resume.")]
    public string FullName { get; set; } = "";

    [Description("The person's email address exactly as written, or an empty string if absent.")]
    public string Email { get; set; } = "";

    [Description("The person's phone number exactly as written, or an empty string if absent.")]
    public string Phone { get; set; } = "";

    [Description("The city and country or state the person lives in, for example: Melbourne, Australia.")]
    public string Location { get; set; } = "";

    [Description("The professional summary or personal statement, copied from the resume. "
               + "An empty string if the resume has no summary section.")]
    public string Headline { get; set; } = "";

    [Description("Every technology, programming language, framework, tool, cloud service "
               + "or database named anywhere in the resume.")]
    public List<string> Skills { get; set; } = new();

    // PHASE 14 — a SECOND LIST rather than a kind tag on each item, and the
    // asymmetry with PostingSkillExtraction below is deliberate.
    //
    // Two reasons, in order of weight. First, this is what the model already does
    // well: asked directly, llama3.2:3b separates soft from technical of its own
    // accord, and giving it the shape it naturally produces is cheaper than
    // fighting it into a per-item enum on a bare string list. Second, `Skills`
    // reaches the wire as ResumeDraft.skills: string[], so a second list is
    // ADDITIVE where changing the element type would break every reader.
    //
    // Posting skills carry a per-item Required flag and are therefore already
    // objects, so the enum costs nothing there. Same information, two shapes,
    // because the two sides genuinely differ.
    [Description("Every soft skill, working style or interpersonal strength the resume claims — "
               + "for example: communication, stakeholder management, mentoring, problem solving. "
               + "These are ways of working, not technologies. An empty list if the resume "
               + "mentions none.")]
    public List<string> SoftSkills { get; set; } = new();

    [Description("Every job held, most recent first.")]
    public List<ExperienceExtraction> Experience { get; set; } = new();

    [Description("Every degree, diploma or certification.")]
    public List<EducationExtraction> Education { get; set; } = new();
}

internal sealed class ExperienceExtraction
{
    [Description("The name of the employer.")]
    public string Employer { get; set; } = "";

    [Description("The job title held at that employer.")]
    public string Title { get; set; } = "";

    // "Copy the text exactly" is doing real work in these two descriptions. Asked
    // for a date, a small model helpfully normalises "Mar 2021" into "2021-03-01"
    // and turns "Present" into today — inventing precision the document does not
    // contain. Asked to copy, it copies.
    [Description("The start date exactly as the resume writes it, for example: Mar 2021.")]
    public string Start { get; set; } = "";

    [Description("The end date exactly as the resume writes it, for example: Present.")]
    public string End { get; set; } = "";

    [Description("The bullet points listed under this job, each copied as its own string.")]
    public List<string> Highlights { get; set; } = new();
}

internal sealed class EducationExtraction
{
    [Description("The name of the university, school or awarding body.")]
    public string Institution { get; set; } = "";

    [Description("The qualification, for example: Bachelor of Computer Science.")]
    public string Qualification { get; set; } = "";

    [Description("The year of completion exactly as written.")]
    public string Year { get; set; } = "";
}

internal sealed class PostingExtraction
{
    [Description("The name of the company that is hiring. Not the recruitment agency, "
               + "if the advertisement names both.")]
    public string Company { get; set; } = "";

    [Description("The job title of the advertised role.")]
    public string Title { get; set; } = "";

    [Description("Where the job is based, for example: Melbourne, hybrid.")]
    public string Location { get; set; } = "";

    [Description("The individual requirements, responsibilities and benefits the advertisement lists.")]
    public List<RequirementExtraction> Requirements { get; set; } = new();

    [Description("Every skill named in the advertisement — both technologies "
               + "(languages, frameworks, tools, cloud services, databases) and soft skills "
               + "(communication, stakeholder management, teamwork and the like).")]
    public List<PostingSkillExtraction> Skills { get; set; } = new();
}

internal sealed class PostingSkillExtraction
{
    // "The skill itself, not the phrase" earns its place the way the date
    // descriptions above do. Measured against llama3.2:3b on a real-shaped ad: the
    // first version of this field returned "Excellent communication skills",
    // "Proven stakeholder management" and "CI/CD pipelines" — the advertisement's
    // own wording, adjectives and all. Each became its own row, because a
    // catalogue cannot alias its way out of an open set of sentence fragments.
    // Naming the failure and showing the pair fixes it at the source.
    [Description("The name of the skill itself, not the sentence it appears in. "
               + "Write \"Communication\", not \"Excellent communication skills\"; "
               + "\"CI/CD\", not \"CI/CD pipelines\". "
               + "For example: C#, PostgreSQL, Kubernetes, Stakeholder Management.")]
    public string Name { get; set; } = "";

    // PHASE 14. An ENUM property, not a string, and that is the whole mechanism:
    // StructuringSchema.Json carries JsonStringEnumConverter, so this emits a
    // JSON Schema `enum` of NAMES and constrained decoding makes an invalid
    // answer unrepresentable. The model cannot reply "hard skill" or "n/a" here.
    // Same trick the file already relies on for RequireAllProperties — see
    // DocumentStructurer.StructuringSchema, which explains why the schema does
    // more work than the prompt.
    [Description("Technical for a technology, language, framework, tool or database. "
               + "Soft for a way of working or an interpersonal strength.")]
    public SkillKind Kind { get; set; } = SkillKind.Unknown;

    [Description("True if the advertisement lists it as required or essential; "
               + "false if it is nice to have.")]
    public bool Required { get; set; }
}

internal sealed class RequirementExtraction
{
    [Description("The requirement, responsibility or benefit, copied as one sentence.")]
    public string Text { get; set; } = "";

    // A real enum, not a string — a deliberate departure from Phase 4's Seniority
    // field, so it is worth saying why the two differ.
    //
    // Phase 4 used a string because binding an enum directly means a model
    // answering "Mid-Senior" fails the whole parse and loses the summary and the
    // skills with it. That reasoning holds when the value is only ASKED for in a
    // description. Here the enum reaches the schema as a JSON Schema `enum` of the
    // three names (see StructuringSchema.For<T>), so constrained decoding cannot
    // emit anything else — the failure Phase 4 was avoiding is unreachable, and
    // the mapper needs no tolerant fallback.
    //
    // ------------------------------------------------------------------------
    // KNOWN GAP, measured: the value is always VALID and often WRONG.
    // ------------------------------------------------------------------------
    // On the real-model check, llama3.2:3b labelled all six extracted
    // requirements "Responsibility" — including "At least 5 years of professional
    // backend engineering experience", which is plainly a Qualification — and
    // skipped the advertisement's responsibilities and benefits sections
    // entirely, extracting only from "What we are looking for".
    //
    // Switching from a string to a schema-constrained enum did not change that,
    // which is the useful part of the finding: it was never a parsing problem.
    // The model is answering with one of the three legal words and choosing
    // badly. A 3B model classifying a sentence into three abstract categories is
    // simply at the edge of what it does well, and no amount of prompt work moved
    // it in the attempts made.
    //
    // Left as it is on purpose. This is precisely the case the confirm-and-fix
    // step exists for: a wrong label is visible on the review screen and takes one
    // click to correct, and IsMustHave — the field that actually matters for the
    // Phase 5 ATS check — was correct on all six.
    [Description("Qualification for something the candidate must have, "
               + "Responsibility for something they would do, "
               + "Benefit for something the employer offers.")]
    public PostingRequirementKind Kind { get; set; }

    [Description("True if the advertisement presents it as essential rather than desirable.")]
    public bool MustHave { get; set; }
}

// Column widths the draft has to respect, in one place.
//
// Only Label is here, and only because THREE places need to agree on it:
// ImportDocument validates a user-supplied label against it, clips a
// filename-derived default to it, and CommitImport validates it again at the
// gate. The rest of the widths live as literals beside CommitImport's Clip
// calls, because each is used exactly once and a table of constants far from
// the code that uses them is harder to check against the migration, not easier.
internal static class DraftLimits
{
    public const int MaxLabelLength = 100;
}

// ---------------------------------------------------------------------------
// Null collections are a JSON problem, not a C# one
// ---------------------------------------------------------------------------
// The records above declare their lists non-nullable, and under nullable
// reference types that reads like a guarantee. It is not one across a
// deserializer: System.Text.Json does not enforce NRT annotations, so a PUT body
// of {"resume":{"label":"x"}} binds Skills, Experience and Education as null and
// the compiler never sees it. The commit then walks those lists and the user gets
// a 500 for a request that was merely incomplete.
//
// Coalescing is the right answer rather than rejecting: a client sending only the
// fields it changed is being reasonable, and an absent list means "no items",
// which is a draft this feature must be able to express anyway — a resume with no
// education section is normal.
//
// Applied on the way IN (ReviewImport, before the draft is stored) and on the way
// OUT (ImportDocumentHandler.ReadDraft), because rows written before this existed
// can already hold nulls. One level down too: sanitising the top-level lists is
// not enough when the objects inside them carry lists of their own.
internal static class DraftSanitiser
{
    public static ImportDraft Sanitise(this ImportDraft draft) =>
        new(Sanitise(draft.Resume), Sanitise(draft.Posting));

    private static ResumeDraft? Sanitise(ResumeDraft? d) => d is null ? null : d with
    {
        Skills = d.Skills ?? [],
        SoftSkills = d.SoftSkills ?? [],
        Experience = (d.Experience ?? []).Select(x => x with { Highlights = x.Highlights ?? [] }).ToList(),
        Education = d.Education ?? []
    };

    private static PostingDraft? Sanitise(PostingDraft? d) => d is null ? null : d with
    {
        Skills = d.Skills ?? [],
        Requirements = d.Requirements ?? []
    };
}

// Turns the model's shapes into the public draft shapes, dropping the empty
// strings the schema forces the model to emit.
//
// The empty strings are not a wart to work around — they are the mechanism.
// RequireAllProperties makes every property mandatory, which is what stops a 3B
// model replying `{}`; the price is that "absent" has to be expressed as "" and
// converted back here. Paying it in one mapping function is much cheaper than
// the alternative, which Phase 4 measured: an optional schema and a bare `{}`
// back in 53ms.
internal static class DraftMapper
{
    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // PHASE 14 — the same trim/drop-empty/dedup the résumé skill list always did,
    // lifted out because there are two such lists now and they must behave
    // identically. Ordinal dedup, matching what it replaced; the catalogue does
    // the case-insensitive collapsing a few steps later and doing it twice, two
    // different ways, is how the halves come apart.
    private static List<string> Clean(List<string> values) =>
        values
            .Select(s => s?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static ResumeDraft ToDraft(ResumeExtraction e, string label) => new(
        label,
        Clean(e.FullName),
        Clean(e.Email),
        Clean(e.Phone),
        Clean(e.Location),
        Clean(e.Headline),
        Clean(e.Skills),
        // PHASE 14. Cleaned the same way and, importantly, deduped against the
        // TECHNICAL list as well as itself: a model that lists "Problem Solving"
        // in both places should not produce two entries the user has to remove
        // twice. Technical wins the tie because it was asked for first — an
        // arbitrary but stable rule, and the catalogue's own "first writer names
        // the Kind" behaviour makes the choice harmless either way.
        Clean(e.SoftSkills).Except(Clean(e.Skills), StringComparer.OrdinalIgnoreCase).ToList(),
        e.Experience
            // An entry with no employer is the model padding the array to satisfy
            // a schema that says the list must exist. Nothing useful can be
            // committed from it and it would show the user a blank card to fix.
            .Where(x => !string.IsNullOrWhiteSpace(x.Employer))
            .Select(x => new DraftExperience(
                x.Employer.Trim(),
                Clean(x.Title),
                Clean(x.Start),
                Clean(x.End),
                x.Highlights
                    .Select(h => h?.Trim())
                    .Where(h => !string.IsNullOrEmpty(h))
                    .Select(h => h!)
                    .ToList()))
            .ToList(),
        e.Education
            .Where(x => !string.IsNullOrWhiteSpace(x.Institution))
            .Select(x => new DraftEducation(
                x.Institution.Trim(),
                Clean(x.Qualification),
                Clean(x.Year)))
            .ToList());

    public static PostingDraft ToDraft(PostingExtraction e, string? description, string? sourceUrl) => new(
        Clean(e.Company) ?? "",
        Clean(e.Title) ?? "",
        Clean(e.Location),
        description,
        sourceUrl,
        e.Skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Name.Trim(), StringComparer.Ordinal)
            .Select(g => new DraftPostingSkill(g.Key, g.First().Required, g.First().Kind))
            .ToList(),
        e.Requirements
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => new DraftRequirement(
                r.Text.Trim(),
                // No TryParse and no fallback: the schema constrains this to the
                // three names, so an unmappable value cannot arrive. The tolerant
                // version that used to be here is what hid the misclassification
                // described on RequirementExtraction.Kind — it turned a wrong
                // answer into a plausible one instead of letting it show.
                r.Kind,
                r.MustHave))
            .ToList());

    // The draft is stored as jsonb and read back on review and on commit, so the
    // options used to write it and read it have to be the same object. Web
    // defaults (camelCase) match what both surfaces serialize, which means the
    // JSON in the column looks like the JSON on the wire — worth having when the
    // debugging step is "select draft_json from document_imports".
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
