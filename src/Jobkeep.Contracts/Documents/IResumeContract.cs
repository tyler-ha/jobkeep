using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Skills;
namespace Jobkeep.Contracts.Documents;

// PHASE 13.2d: the interface and its DTO live in Jobkeep.Contracts; the
// implementation lives with the module that owns the table. The namespace is
// deliberately Jobkeep.Modules.Documents -- 13.6 renames namespaces to match
// projects, in one pass, once nothing else is moving.

// What another module is allowed to know about a résumé when all it needs is to
// point at one. Two fields, and the omissions are the point: no SourceText, no
// email, no phone, no location.
//
// A résumé is the most personal thing in this database and the security audit
// records it as such. Applications needs to answer exactly two questions about
// one — "does this id exist" and "what is it called" — and a contract that
// handed over the entity would have handed over a person's CV to satisfy a
// foreign-key check. Deliberately not the Resume entity, for the same reason
// SkillInfo is not the Skill entity, plus this one.
public record ResumeRef(Guid Id, string Label);

// The résumé as a *document*: its extracted text, and what the import managed to
// pull out of it. This is genuinely a CV crossing a module boundary, so it is
// worth being explicit about why that is correct here and was refused above.
//
// The rule is not "a CV never crosses". It is "each caller gets what its own
// question needs". Applications asks whether an id exists, so it gets two
// fields. The ATS check's entire feature is reading the CV — judging free-text
// requirement coverage against it and warning when the extraction looks
// truncated — so a contract that withheld the text would not be protecting
// anything, it would be refusing the feature. Two DTOs rather than one fat one
// is what keeps that distinction visible: nobody gets SourceText by accident.
//
// Still omitted, because no caller has asked: Phone, Headline, SourceFileName,
// SourceHash, and the education and experience rows themselves.
//
// AT 13.3 THIS BECOMES A NETWORK PAYLOAD. That is a real change in exposure —
// today it is an in-process record, tomorrow it is a CV on a wire — and it is
// the security audit's business at that point, not something to discover then.
// Recorded here so the question arrives with the change that causes it.
public record ResumeContent(
    Guid Id,
    string Label,
    string? FullName,
    string? Email,
    string? Location,
    ResumeSourceFormat? SourceFormat,
    string SourceText,
    // How many roles the import found. A count, not the rows: the one caller
    // uses it as the denominator of a "was text lost?" heuristic, and the rows
    // themselves would be a second CV's worth of personal history.
    int ExperienceCount);

// A copy of the SourceFormat enum, for the same reason PostingRequirementKind is
// a copy: Jobkeep.Contracts may reference no other Jobkeep assembly, so it cannot
// name the entity enum that lives beside the entity. The mapping is an explicit
// switch in ResumeContract rather than a cast, so adding a value to one without
// the other fails to compile instead of silently renumbering.
public enum ResumeSourceFormat { PlainText, Markdown, Pdf, Docx }

// What Documents exposes about a résumé.
//
// ---------------------------------------------------------------------------
// Three methods, and the test that keeps it at three
// ---------------------------------------------------------------------------
// This interface shipped at 13.2d with one method and a comment saying a second
// "would be worth stopping over", because its only caller then was Applications
// and Applications has no business knowing more about a résumé than its name.
// That was the right rule for that caller and the wrong rule to state as a cap:
// it counted METHODS when what it meant was that a contract must not grow one
// method per caller's question. 13.2e is the stop the comment asked for, and the
// count is not what the answer turned on.
//
// The test that replaces it is the one ISkillCatalog already carries: does a
// proposed method name a fact about a RÉSUMÉ, or a question the caller has about
// its own feature? All three below are the first kind — what this résumé is
// called, what it says, which skills it lists. "Which skills does posting X ask
// for that this résumé lacks" is the second kind, and it stays in Ats, which is
// the module that has that question.
//
// The distinction has teeth. The first kind is bounded by what a résumé IS, so
// the list closes on its own. The second kind is bounded by nothing, which is
// how IJobApplicationRepository reached the size that killed it (decision 5).
public interface IResumeContract
{
    // The reference: existence and a name, for a caller checking an id or
    // rendering a chip. Null means no such résumé, so existence is `is not null`
    // at the call site and the caller writes its own message — the convention
    // every contract in this project uses.
    //
    // Kept separate from GetContentAsync rather than merged into it, and the
    // asymmetry is deliberate: merging would mean every foreign-key check pulled
    // a CV across the boundary, which is the over-fetch A1 is about with a
    // privacy cost stacked on top.
    Task<ResumeRef?> GetAsync(Guid resumeId, CancellationToken ct = default);

    // The document: extracted text plus the fields the import found in it. Null
    // when no such résumé. See ResumeContent for why this one is allowed to carry
    // a CV when GetAsync deliberately does not.
    Task<ResumeContent?> GetContentAsync(Guid resumeId, CancellationToken ct = default);

    // The skill ids this résumé lists — ids, not names.
    //
    // Stopping at the id is the same call v_posting_skill_demand made in 13.2b:
    // resolving names in here would mean joining `skills`, which does not remove
    // the cross-module read, it only moves it from C# where the compiler sees it
    // into SQL where nothing does. The caller resolves ids through ISkillCatalog,
    // which is the module that owns the vocabulary.
    //
    // Empty rather than null for a résumé that does not exist. The one caller has
    // already resolved the résumé through GetContentAsync by the time it asks, so
    // a second "does it exist" answer would be a distinction with no reader.
    Task<IReadOnlyList<Guid>> GetSkillIdsAsync(Guid resumeId, CancellationToken ct = default);
}
