using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Match;
using Mediator;
using Microsoft.AspNetCore.Mvc;

namespace Jobkeep.Api.Controllers;

// The match check, GET and POST. Phase 13.5 replaced AtsEndpoints.cs with this; the
// URLs, the responses and the slices behind them are unchanged.
//
// Under /applications, for the reason AiController gives: a URL follows the
// resource the caller is thinking about ("the match check for this application"),
// while the code follows whichever module owns the table. Forcing an /ats/...
// prefix would leak the module layout into the public API, which is the thing
// module boundaries exist to be free to change.
[ApiController]
[Route("applications")]
public class MatchController : ControllerBase
{
    // POST — computes and stores. Not idempotent in the HTTP sense: it writes an
    // match_results row, and re-running it against a different resume changes the
    // answer. `resumeId` is optional; omitted, it uses the resume the application
    // was sent with.
    //
    // [FromQuery] is explicit because it has to be: under [ApiController] a
    // simple type with no attribute binds from the route if the template names it
    // and from the query string otherwise, and relying on "otherwise" is how a
    // parameter silently moves when a route template changes.
    [HttpPost("{id:guid}/match-check")]
    public async Task<IResult> Check(
        Guid id,
        [FromQuery] Guid? resumeId,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new RunMatchCheck(id, resumeId), ct)).ToHttpResult();

    // GET — reads back what was stored, running no model. The same split
    // GetAnalysis.cs makes, and the reason the result is a table rather than a
    // computed response.
    [HttpGet("{id:guid}/match-check")]
    public async Task<IResult> Get(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new GetMatchResult(id), ct)).ToHttpResult();
}
