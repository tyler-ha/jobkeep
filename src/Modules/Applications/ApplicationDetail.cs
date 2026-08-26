using System.Linq.Expressions;
using Jobkeep.Models;

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
    string? ResumeText,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    PostingResponse Posting);

public static class ApplicationDetailProjection
{
    // One flat Select instead of the old include graph. The difference is not
    // cosmetic: the retired repository's WithGraph() eager-loaded company +
    // skills + requirements + AI analysis + ATS result behind AsSplitQuery(),
    // which is five round-trips returning columns nobody asked for
    // (architecture.md A1). This loads exactly the columns below.
    //
    // Deliberately absent: AiAnalysis (Phase 4) and AtsResult (Phase 5). Neither
    // is written yet, and adding them to the contract before they exist would
    // publish a field that is always null.
    public static readonly Expression<Func<JobApplication, ApplicationDetail>> Expression =
        a => new ApplicationDetail(
            a.Id,
            a.Status,
            a.DateApplied,
            a.Notes,
            a.ResumeText,
            a.CreatedAtUtc,
            a.UpdatedAtUtc,
            new PostingResponse(
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
                    .Select(ps => new PostingSkillResponse(
                        ps.Skill.Name, ps.Skill.Category, ps.IsRequired, ps.Source))
                    .ToList(),
                a.Posting.Requirements
                    .Select(r => new RequirementResponse(r.Id, r.Text, r.Kind, r.IsMustHave))
                    .ToList()));
}
