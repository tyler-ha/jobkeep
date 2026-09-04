using Jobkeep.Modules.Ai;
using Mediator;
using Microsoft.AspNetCore.Mvc;

namespace Jobkeep.Api.Controllers;

// The analyzer's two routes. Phase 13.5 replaced AiEndpoints.cs with this; the
// URLs, the responses and the slices behind them are unchanged.
//
// The routes sit under /applications even though the code lives in Ai. URLs
// follow the resource a caller is thinking about — "the analysis of this
// application" — while the code follows whichever module owns the table. Those
// two do not have to agree, and forcing a /ai/... prefix to make them agree would
// leak the module layout into the public API, which is the thing module
// boundaries are supposed to be free to change.
//
// Three controllers therefore share the /applications prefix (this one, Ats and
// Applications itself). Attribute routing is fine with that as long as the
// templates differ, and the controller NAME is what Swagger tags by — so the
// grouping in the UI still follows the module, exactly as WithTags("Ai") did.
[ApiController]
[Route("applications")]
public class AiController : ControllerBase
{
    // POST /applications/{id}/analyze — runs the model and stores the result.
    // POST rather than GET because it is not safe or idempotent in the HTTP
    // sense: it writes rows, and re-running it can add skills that were not
    // there before.
    [HttpPost("{id:guid}/analyze")]
    public async Task<IResult> Analyze(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new AnalyzePosting(id), ct)).ToHttpResult();

    // GET /applications/{id}/analysis — reads back what was stored, without
    // paying for inference again.
    [HttpGet("{id:guid}/analysis")]
    public async Task<IResult> Analysis(
        Guid id,
        [FromServices] ISender sender,
        CancellationToken ct)
        => (await sender.Send(new GetAnalysis(id), ct)).ToHttpResult();
}
