using Jobkeep.Modules.Analytics;
using Jobkeep.Modules.Applications;

namespace Jobkeep.GraphQL;

// GraphQL read side. Both resolvers are thin adapters over the same slice
// handlers the REST routes call, so a filter or a validation rule cannot mean
// one thing here and another there.
//
// Phase 2.3 changed what these return. They used to hand back the EF entity
// straight from IJobApplicationRepository, which had two consequences:
//
//   * A1 — the repository's include graph eager-loaded company, skills,
//     requirements, AI analysis and ATS result on every call, whatever the
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
        [Service] ListApplicationsHandler handler,
        CancellationToken ct)
        => (await handler.HandleAsync(query ?? new ApplicationQuery(), ct)).ValueOrThrow();

    public async Task<ApplicationDetail> GetApplication(
        Guid id,
        [Service] GetApplicationHandler handler,
        CancellationToken ct)
        => (await handler.HandleAsync(id, ct)).ValueOrThrow();

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
        [Service] SkillDemandHandler handler,
        CancellationToken ct)
        => (await handler.HandleAsync(top, ct)).ValueOrThrow();

    public async Task<ApplicationFunnel> GetStatusFunnel(
        [Service] StatusFunnelHandler handler,
        CancellationToken ct)
        => (await handler.HandleAsync(ct)).ValueOrThrow();

    public async Task<List<CompanyRollupItem>> GetCompanyRollup(
        int? top,
        [Service] CompanyRollupHandler handler,
        CancellationToken ct)
        => (await handler.HandleAsync(top, ct)).ValueOrThrow();
}
