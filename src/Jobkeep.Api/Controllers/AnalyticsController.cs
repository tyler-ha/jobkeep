using Jobkeep.Modules.Analytics;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jobkeep.Api.Controllers;

// The three read-only aggregates. Phase 13.5 replaced AnalyticsEndpoints.cs with
// this; the URLs, the responses and the slices behind them are unchanged.
//
// Named for the module rather than for the route prefix so Swagger's tag stays
// "Analytics" — Swashbuckle tags by controller name, and the endpoint file it
// replaces said WithTags("Analytics") over a /stats group.
// PHASE 11.2a - every action here requires an authenticated caller. The
// attribute is on the class rather than a fallback policy in Program.cs,
// because a fallback would also catch /identity/login and /identity/register
// and those have to stay open. AuthorizationTests asserts that no action on
// any controller is reachable without it, so a sixth controller that forgets
// this line fails the build rather than shipping an open route.
[Authorize]
[ApiController]
[Route("stats")]
public class AnalyticsController : ControllerBase
{
    // GET /stats/skill-demand?top=
    //
    // A bare `int? top` rather than a query record. The list slice needs a record
    // because it binds ten filters that both surfaces and Swagger have to agree
    // on; one optional scalar does not earn an input type, and in GraphQL it
    // reads better as skillDemand(top: 5) than as skillDemand(query: { top: 5 }).
    [HttpGet("skill-demand")]
    public async Task<IResult> SkillDemand(
        [FromQuery] int? top,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new SkillDemand(top), ct)).ToHttpResult();

    // GET /stats/funnel — no parameters; the funnel is the whole table.
    [HttpGet("funnel")]
    public async Task<IResult> Funnel(
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new StatusFunnel(), ct)).ToHttpResult();

    // GET /stats/companies?top=
    [HttpGet("companies")]
    public async Task<IResult> Companies(
        [FromQuery] int? top,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new CompanyRollup(top), ct)).ToHttpResult();
}
