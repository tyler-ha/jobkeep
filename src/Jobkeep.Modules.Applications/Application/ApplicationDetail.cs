using System.Linq.Expressions;
using Jobkeep.Models;
using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Skills;

namespace Jobkeep.Modules.Applications;

// The full response shape for a single application, plus the EF projection that
// builds it. Three slices return this — GetApplication, CreateApplication and
// UpdateApplication — so the records and the expression live together in one
// file rather than being copied three times.
//
// This is NOT a repository coming back through the side door. A repository owns
// *access*: it decides how you may query and what rules apply on the way. This
// file owns a *shape* — it holds no rules, no validation, no branching, and
// every slice still writes its own query. Sharing the projection is the same
// kind of reuse as sharing a DTO; sharing a query API is what decision 5 threw out.
//
// Collections are declared as List<T> rather than IReadOnlyList<T> on purpose:
// EF Core materialises a projected collection as List<T>, and an interface-typed
// constructor parameter puts a Convert node in the expression tree that the
// query translator does not reliably strip.

public record CompanyResponse(
    Guid Id,
    string Name,
    string? Website,
    string? Industry,
    string? HqLocation);

// PostingSkillResponse (AddSkillToPosting.cs) and RequirementResponse
// (AddRequirementToPosting.cs) are reused rather than redefined — the shape a
// caller gets for a skill should not depend on which endpoint returned it.
public record PostingResponse(
    Guid Id,
    string Title,
    string? Location,
    EmploymentType EmploymentType,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string SalaryCurrency,
    SalaryPeriod SalaryPeriod,
    string? Description,
    string? SourceUrl,
    DateOnly? PostedDate,
    CompanyResponse Company,
    List<PostingSkillResponse> Skills,
    List<RequirementResponse> Requirements);

public record ApplicationDetail(
    Guid Id,
    ApplicationStatus Status,
    DateOnly DateApplied,
    string? Notes,
    // Phase 4.5: which resume version was sent, by reference. This used to be the
    // resume TEXT inlined into every application's detail response — a whole
    // resume on the wire whenever you fetched a job you applied to, which was
    // both the over-fetch A1 is about and the PII exposure the security audit
    // records against ResumeText. The label is carried alongside the id so a
    // client can render "backend-focused" without a second round trip.
    Guid? ResumeId,
    string? ResumeLabel,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    PostingResponse Posting);

// ---------------------------------------------------------------------------
// PHASE 13.2d — the shape SQL can produce, which is no longer the shape returned
// ---------------------------------------------------------------------------
// Two of ApplicationDetail's fields live in other modules' tables: ResumeLabel is
// in `resumes` (Documents) and a skill's name and category are in `skills`
// (Skills). Both used to arrive through a navigation property — `a.Resume.Label`,
// `ps.Skill.Name` — which is a join the compiler makes invisible: no DbSet is
// named, so the boundary test passed while the query crossed.
//
// So the projection stops short and HydrateAsync finishes the job. These rows are
// internal because they never leave the module; the public record above is still
// the API contract, unchanged, and nothing on either surface moved.
internal record PostingSkillRow(Guid SkillId, bool IsRequired, SkillSource Source);

internal record PostingRow(
    Guid Id,
    string Title,
    string? Location,
    EmploymentType EmploymentType,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string SalaryCurrency,
    SalaryPeriod SalaryPeriod,
    string? Description,
    string? SourceUrl,
    DateOnly? PostedDate,
    CompanyResponse Company,
    List<PostingSkillRow> Skills,
    List<RequirementResponse> Requirements);

internal record ApplicationDetailRow(
    Guid Id,
    ApplicationStatus Status,
    DateOnly DateApplied,
    string? Notes,
    Guid? ResumeId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    PostingRow Posting);

internal static class ApplicationDetailProjection
{
    // Turns the row SQL produced into the response the API promises, by asking
    // the two modules that own the missing columns.
    //
    // Two round trips at most, and both are skipped when they would be empty: an
    // application with no résumé attached does not ask Documents anything, and a
    // posting with no skills does not ask the catalog. The batching is
    // ISkillCatalog.GetAsync's, not this method's — a page of posting_skills rows
    // is one call, never one per row.
    //
    // A skill id with no row is DROPPED rather than rendered blank. The foreign
    // key makes that impossible today; at 13.3, when the FK is gone, a missing row
    // means the taxonomy service lost something this posting points at, and an
    // empty chip on a job ad would be the worst of the three available answers.
    public static async Task<ApplicationDetail> HydrateAsync(
        ApplicationDetailRow row,
        ISkillCatalog skills,
        IResumeContract resumes,
        CancellationToken ct = default)
    {
        var resume = row.ResumeId is { } resumeId
            ? await resumes.GetAsync(resumeId, ct)
            : null;

        var names = await skills.GetAsync(row.Posting.Skills.Select(x => x.SkillId).ToList(), ct);

        var postingSkills = row.Posting.Skills
            .Where(x => names.ContainsKey(x.SkillId))
            .Select(x => new PostingSkillResponse(
                names[x.SkillId].Name, names[x.SkillId].Category, x.IsRequired, x.Source))
            .ToList();

        return new ApplicationDetail(
            row.Id,
            row.Status,
            row.DateApplied,
            row.Notes,
            row.ResumeId,
            // Null when the résumé is gone as well as when none was attached. The
            // id is still returned either way, so a client can tell the two apart
            // if it needs to — which is more than the old LEFT JOIN could say.
            resume?.Label,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            new PostingResponse(
                row.Posting.Id,
                row.Posting.Title,
                row.Posting.Location,
                row.Posting.EmploymentType,
                row.Posting.SalaryMin,
                row.Posting.SalaryMax,
                row.Posting.SalaryCurrency,
                row.Posting.SalaryPeriod,
                row.Posting.Description,
                row.Posting.SourceUrl,
                row.Posting.PostedDate,
                row.Posting.Company,
                postingSkills,
                row.Posting.Requirements));
    }

    // One flat Select instead of the old include graph. The difference is not
    // cosmetic: the retired repository's WithGraph() eager-loaded company +
    // skills + requirements + AI analysis + ATS result behind AsSplitQuery(),
    // which is five round-trips returning columns nobody asked for
    // (architecture.md A1). This loads exactly the columns below.
    //
    // Deliberately absent: AiAnalysis and AtsResult (Phase 5).
    //
    // The reason for AiAnalysis changed in Phase 4 and the exclusion did not. It
    // used to be "not written yet"; it is now a module boundary. `ai_analyses`
    // belongs to the Ai module, and projecting it here would have Applications
    // reading another module's table -- the same rule-2 crossing that Ai needed
    // IPostingContract to avoid in the other direction. It is served by the Ai
    // module at GET /applications/{id}/analysis instead (Modules/Ai/GetAnalysis.cs).
    //
    // The AI-extracted *skills* do appear below, because they are ordinary
    // posting_skills rows that happen to carry Source = AiExtracted. Those belong
    // to Applications, so there is no boundary to cross.
    //
    // AtsResult is still absent for the original reason: Phase 5 has not run.
    public static readonly Expression<Func<JobApplication, ApplicationDetailRow>> Expression =
        a => new ApplicationDetailRow(
            a.Id,
            a.Status,
            a.DateApplied,
            a.Notes,
            // The id only. The label used to be read here as
            // `a.Resume == null ? null : a.Resume.Label` — an explicit ternary
            // rather than a null-conditional, because this is an expression tree
            // and the ternary is what became the LEFT JOIN's null case. The join
            // is what 13.2d removed; the null case survives as `resume?.Label` in
            // HydrateAsync, where it is ordinary C#.
            a.ResumeId,
            a.CreatedAtUtc,
            a.UpdatedAtUtc,
            new PostingRow(
                a.Posting.Id,
                a.Posting.Title,
                a.Posting.Location,
                a.Posting.EmploymentType,
                a.Posting.SalaryMin,
                a.Posting.SalaryMax,
                a.Posting.SalaryCurrency,
                a.Posting.SalaryPeriod,
                a.Posting.Description,
                a.Posting.SourceUrl,
                a.Posting.PostedDate,
                new CompanyResponse(
                    a.Posting.Company.Id,
                    a.Posting.Company.Name,
                    a.Posting.Company.Website,
                    a.Posting.Company.Industry,
                    a.Posting.Company.HqLocation),
                a.Posting.PostingSkills
                    .Select(ps => new PostingSkillRow(ps.SkillId, ps.IsRequired, ps.Source))
                    .ToList(),
                a.Posting.Requirements
                    .Select(r => new RequirementResponse(r.Id, r.Text, r.Kind, r.IsMustHave))
                    .ToList()));
}
