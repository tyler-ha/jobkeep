using System.Text.Json.Serialization;
using Jobkeep.Contracts.Shared;
using Jobkeep.Contracts.Skills;
using Jobkeep.SharedKernel;

namespace Jobkeep.Modules.Documents.Domain;

// A resume, as its own aggregate.
//
// ---------------------------------------------------------------------------
// Why this is a table and not a column
// ---------------------------------------------------------------------------
// Until Phase 4.5 the resume was `JobApplication.ResumeText` — a `string?` on
// the application. That made a resume a property of *an application*, which is
// backwards: a resume is a property of **you**. You keep two or three variants
// and send them to thirty jobs. As a column that stored the same text thirty
// times, could not answer "which applications used the version I have since
// improved", and gave the parsed records nowhere to live that made sense —
// records attached to one application are not reusable, and reuse is the entire
// point of parsing them out.
//
// So `job_applications.ResumeText` is gone and `job_applications.ResumeId`
// points here instead. That was the first migration since InitialCreate.
//
// The payoff is a query the app could not previously express: the skills on your
// resume and the skills on the postings you apply to are now rows in the *same*
// shared `skills` table, so "what do the jobs I want ask for that my resume never
// mentions" is one join rather than a text search. That is also exactly what
// Phase 5's match check needs, and it is why the skills table was made shared in
// the first place.
public class Resume : IAuditable, ISoftDeletable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // What you call this version — "backend-focused", "generalist". Unique, so
    // re-importing under a name you already use is a rename rather than a
    // silent second copy. Required, because "Resume (1)" is not a label.
    public string Label { get; set; } = string.Empty;

    // The basics. All optional: a resume that omits a phone number is normal,
    // and a parse that misses one is not an error worth failing an import over.
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Location { get; set; }

    // The professional summary / personal statement, when the document has one.
    public string? Headline { get; set; }

    // The extracted plain text, kept verbatim.
    //
    // This is the phase's central design decision and it is worth stating plainly:
    // the *text* is stored, the uploaded *file* is not (see DocumentImport). Text
    // is what every later feature reads — the match check compares text, a re-parse
    // after a prompt change re-reads text — and keeping it means improving the
    // structuring step never requires the user to re-upload anything.
    public string SourceText { get; set; } = string.Empty;

    // Provenance only. The original bytes are discarded after extraction, so this
    // is how you tell "did I already import this exact file" without storing it.
    public string? SourceFileName { get; set; }
    public string? SourceHash { get; set; }

    // The format the bytes actually were, carried through from the import that
    // created this resume. Phase 5's formatting check reads it.
    //
    // A *detected* format, never the filename extension — DocumentTextExtractor
    // sniffs the bytes precisely because an extension is a claim, and a check
    // that warns you about PDFs would be worthless if a renamed .docx triggered
    // it. Nullable: rows created before Phase 5 have no record of their format,
    // and the format rule treats null as "unknown, say nothing", which is honest
    // rather than guessing.
    public SourceFormat? SourceFormat { get; set; }

    // Phase 7 — the case-insensitive natural key, matching companies.Name and
    // skills.Name. A STORED generated column computed by Postgres, so "Backend"
    // and "backend" are one label rather than two resumes. All three tables got
    // this together; fixing one would have made them disagree, which is exactly
    // why 4.5 left the defect in place rather than half-fixing it.
    public string LabelNormalized { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // Phase 8. This is the entity soft delete changes most, and the reason is
    // `LabelNormalized` above: the unique index on it is now FILTERED to live
    // rows, so archiving "backend" frees the name — which is correct, and is
    // also why a restore has to ask whether something took it since. Without the
    // filter, archiving a résumé would permanently burn its label.
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }

    [JsonIgnore] public List<ResumeSkill> ResumeSkills { get; set; } = new();
    [JsonIgnore] public List<ResumeExperience> Experiences { get; set; } = new();
    [JsonIgnore] public List<ResumeEducation> Educations { get; set; } = new();

    // PHASE 13.3b — the back-reference to the applications sent with this
    // version is gone, with the foreign key that backed it. `job_applications`
    // is Applications' table in its own schema; the link survives as an ordinary
    // `job_applications.ResumeId` column that nothing enforces.
    //
    // The three navigations above stay, and the contrast is the whole boundary in
    // one class: skills, experiences and educations are Documents' own tables, so
    // walking to them is a join inside one deployable. An application is not, so
    // it isn't.
}

// The join from a resume to the SHARED `skills` table — the deliberate mirror of
// PostingSkill.
//
// Sharing the skill row is the whole point. If resumes had their own skill
// vocabulary, "C# appears on 12 postings and on my resume" would be a string
// comparison across two tables; sharing it makes it a join on skill_id.
//
// There is no IsRequired here, and its absence is meaningful: "required" is a
// property of what a posting *asks for*, not of what you *have*. Modelling both
// sides with one shape would have forced a nullable flag that is always null on
// this side, which is how a schema starts lying about what it means.
public class ResumeSkill
{
    public Guid ResumeId { get; set; }
    [JsonIgnore] public Resume Resume { get; set; } = null!;

    // 13.3b CUT THE NAVIGATION AND THE FOREIGN KEY, the exact mirror of what
    // happened to PostingSkill.Skill. `skills` is the Skills module's table, so
    // this is a bare Guid now, and the guarantee that it points at a real row
    // moved to ISkillCatalog.FindOrCreateAsync — the only way a link row is ever
    // created on either side.
    public Guid SkillId { get; set; }

    // Parsed = you typed or corrected it; AiExtracted = the structuring step
    // proposed it and you confirmed it unchanged. Same enum, same meaning, as
    // the posting side.
    public SkillSource Source { get; set; } = SkillSource.Parsed;
}

// One job on the resume.
//
// Dates are TEXT, not DateOnly, and that is deliberate rather than lazy. Resumes
// write dates as "Mar 2021", "2021–present", "Summer 2019". Parsing those into a
// real date means guessing, and Phase 4 measured what a small model does when
// asked to derive rather than transcribe: it returns something plausible and
// wrong. Storing what the document literally says is honest, and nothing in this
// phase or Phase 5 does date arithmetic. If something ever does, that is a
// migration with a real requirement behind it instead of a guess made early.
public class ResumeExperience
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ResumeId { get; set; }
    [JsonIgnore] public Resume Resume { get; set; } = null!;

    public string Employer { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? StartText { get; set; }
    public string? EndText { get; set; }

    // The bullet points, as List<string> -> Postgres text[] (Npgsql maps this
    // natively; MatchResult already does the same). A child table would buy
    // ordering and querying that nothing needs — bullets are read as a block.
    public List<string> Highlights { get; set; } = new();

    // Document order. Resumes are read top-down and the top entry is the current
    // job; without this the rows come back in whatever order Postgres likes.
    public int Ordinal { get; set; }
}

// One qualification. Year is text for the same reason experience dates are.
public class ResumeEducation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ResumeId { get; set; }
    [JsonIgnore] public Resume Resume { get; set; } = null!;

    public string Institution { get; set; } = string.Empty;
    public string? Qualification { get; set; }
    public string? YearText { get; set; }

    public int Ordinal { get; set; }
}
