using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Analytics;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Modules.Match;
using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Documents.Domain;
using Mediator;

// PHASE 13.4 — namespace aliases, and they are not cosmetic. Every resolver
// below is named for the field it publishes (`GetApplication`, `RunMatchCheck`), and
// 13.4 gave the request record the same name — so a bare `new RunMatchCheck(...)`
// inside this class binds to the METHOD and does not compile. Aliasing the five
// module namespaces keeps the call sites one line each; the alternative is
// fully-qualified type names on every field, or renaming resolvers and changing
// the published schema to suit a C# lookup rule.
using Apps = Jobkeep.Modules.Applications;
using Stats = Jobkeep.Modules.Analytics;
using Ai = Jobkeep.Modules.Ai;
using Docs = Jobkeep.Modules.Documents;
using Match = Jobkeep.Modules.Match;

namespace Jobkeep.Api.GraphQL;

// GraphQL read side. Both resolvers are thin adapters over the same slice
// handlers the REST routes call, so a filter or a validation rule cannot mean
// one thing here and another there.
//
// Phase 2.3 changed what these return. They used to hand back the EF entity
// straight from IJobApplicationRepository, which had two consequences:
//
//   * A1 — the repository's include graph eager-loaded company, skills,
//     requirements, AI analysis and match result on every call, whatever the
//     client asked for. A query selecting one field cost five round-trips.
//   * A7 — because HotChocolate builds the schema from resolver return types,
//     publishing JobApplication published its navigation properties too. A
//     client could walk application -> posting -> company -> postings ->
//     applications -> resumeText and read every résumé in the database. The
//     [JsonIgnore] attributes that hide those back-references from REST mean
//     nothing to HotChocolate.
//
// Returning DTOs closes A7 outright: no EF entity is reachable from any root
// field, so the entity types are not in the published schema at all. A1 is
// narrowed rather than closed — the handlers project to exactly the DTO's
// columns, so nothing loads AI analyses or résumé text unasked, but a query
// selecting only `title` still loads the whole DTO. True per-field projection
// needs HotChocolate.Data, and buying it would mean resolvers returning
// IQueryable<JobApplication> again — fixing A1 by reinstating A7.
public class Query
{
    public async Task<ApplicationPage> GetApplications(
        ApplicationQuery? query,
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Apps.ListApplications(query ?? new ApplicationQuery()), ct)).ValueOrThrow();

    public async Task<ApplicationDetail> GetApplication(
        Guid id,
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Apps.GetApplication(id), ct)).ValueOrThrow();

    // Phase 2.4 — the analytics fields. Same pattern: adapters over the slice
    // handlers the /stats routes call, so the cap on `top` and the shape of the
    // funnel are decided once rather than once per surface.
    //
    // Worth noting what these are NOT. They return finished aggregates, not an
    // IQueryable a client can further filter or page. That is on purpose: an
    // aggregate over an unbounded client-supplied query is how a read-only
    // reporting endpoint turns into a way to make the database do arbitrary
    // work, and it is also how EF entities get back into the published schema
    // (architecture.md A7, closed in Phase 2.3 and worth keeping closed).
    public async Task<List<SkillDemandItem>> GetSkillDemand(
        int? top,
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Stats.SkillDemand(top), ct)).ValueOrThrow();

    public async Task<ApplicationFunnel> GetStatusFunnel(
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Stats.StatusFunnel(), ct)).ValueOrThrow();

    public async Task<List<CompanyRollupItem>> GetCompanyRollup(
        int? top,
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Stats.CompanyRollup(top), ct)).ValueOrThrow();

    // Phase 4 — reads back the stored analysis without re-running the model.
    // The counterpart to the analyzePosting mutation, and the reason the analysis
    // is not simply a field on ApplicationDetail: `ai_analyses` belongs to the Ai
    // module, and ApplicationDetail's projection belongs to Applications. See
    // Modules/Ai/GetAnalysis.cs.
    public async Task<AnalysisSummaryResponse> GetAnalysis(
        Guid applicationId,
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Ai.GetAnalysis(applicationId), ct)).ValueOrThrow();

    // Phase 4.5 — the document import review cycle.
    //
    // The upload itself is REST-only (DocumentsModule.cs explains why a file
    // does not belong in this schema), but everything after the bytes arrive is
    // on both surfaces: the draft you review, correct and confirm is the same
    // draft either way, decided by the same handlers.
    public async Task<List<ImportSummary>> GetImports(
        ImportStatus? status,
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Docs.ListImports(status), ct)).ValueOrThrow();

    // Returns the extracted text as well as the draft — the review screen needs
    // the document to check the draft against. GetImport.cs notes why that is a
    // deliberate exception to this codebase's habit of never over-fetching.
    public async Task<ImportResponse> GetImport(
        Guid id,
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Docs.GetImport(id), ct)).ValueOrThrow();

    // Phase 6 step 6.1 — the résumé read surface, which did not exist until the
    // front end needed a picker. Same adapter pattern; the list/detail split (no
    // résumé text in the list, text in the detail) is decided in the handlers, so
    // a GraphQL client cannot select its way past it.
    public async Task<List<ResumeSummary>> GetResumes(
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Docs.ListResumes(), ct)).ValueOrThrow();

    public async Task<ResumeDetail> GetResume(
        Guid id,
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Docs.GetResume(id), ct)).ValueOrThrow();

    // Phase 5 — the stored match check. A query, not a mutation, because it reads
    // and nothing else; the runMatchCheck mutation is what computes it. Same split as
    // analysis/analyzePosting above, and for the same reason: the answer is
    // stored so that reading it back cannot quietly become a different answer.
    public async Task<MatchCheckResponse> GetMatchResult(
        Guid applicationId,
        [Service] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new Match.GetMatchResult(applicationId), ct)).ValueOrThrow();
}
