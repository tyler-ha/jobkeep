using Jobkeep.Modules.Ai;
using Jobkeep.Shared;
using Mediator;

namespace Jobkeep.Api.Endpoints;

// Route mapping for the Ai module, lifted out of AiModule.cs by
// Phase 13.1 so the module project can stay a plain class library with no
// ASP.NET dependency -- the handlers never knew about HTTP, only this half did.
//
// TEMPORARY SHAPE. CLAUDE.md forbids an Endpoints/ file and it is right to: this
// is the layout Phase 2.3 deleted. It is back for one step only, because 13.5
// replaces every route here with an attribute-routed controller. Do not add a
// route to this file -- add the slice, and map it in the controller.
public static class AiEndpoints
{

    public static IEndpointRouteBuilder MapAiModule(this IEndpointRouteBuilder app)
    {
        // The routes sit under /applications even though the code lives in Ai.
        // URLs follow the resource a caller is thinking about — "the analysis of
        // this application" — while the code follows whichever module owns the
        // table. Those two do not have to agree, and forcing a /ai/... prefix to
        // make them agree would leak the module layout into the public API, which
        // is the thing module boundaries are supposed to be free to change.
        var group = app.MapGroup("/applications").WithTags("Ai");

        // POST /applications/{id}/analyze — runs the model and stores the result.
        // POST rather than GET because it is not safe or idempotent in the HTTP
        // sense: it writes rows, and re-running it can add skills that were not
        // there before.
        group.MapPost("/{id:guid}/analyze", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new AnalyzePosting(id), ct)).ToHttpResult());

        // GET /applications/{id}/analysis — reads back what was stored, without
        // paying for inference again.
        group.MapGet("/{id:guid}/analysis", async (
            Guid id,
            ISender sender,
            CancellationToken ct) =>
            (await sender.Send(new GetAnalysis(id), ct)).ToHttpResult());

        return app;
    }
}
