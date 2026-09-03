using Jobkeep.Modules.Ats;
using Jobkeep.Shared;

namespace Jobkeep.Api.Endpoints;

// Route mapping for the Ats module, lifted out of AtsModule.cs by
// Phase 13.1 so the module project can stay a plain class library with no
// ASP.NET dependency -- the handlers never knew about HTTP, only this half did.
//
// TEMPORARY SHAPE. CLAUDE.md forbids an Endpoints/ file and it is right to: this
// is the layout Phase 2.3 deleted. It is back for one step only, because 13.5
// replaces every route here with an attribute-routed controller. Do not add a
// route to this file -- add the slice, and map it in the controller.
public static class AtsEndpoints
{

    public static IEndpointRouteBuilder MapAtsModule(this IEndpointRouteBuilder app)
    {
        // Under /applications, for the reason AiModule.cs gives: a URL follows the
        // resource the caller is thinking about ("the ATS check for this
        // application"), while the code follows whichever module owns the table.
        // Forcing an /ats/... prefix would leak the module layout into the public
        // API, which is the thing module boundaries exist to be free to change.
        var group = app.MapGroup("/applications").WithTags("Ats");

        // POST — computes and stores. Not idempotent in the HTTP sense: it writes
        // an ats_results row, and re-running it against a different resume changes
        // the answer. `resumeId` is optional; omitted, it uses the resume the
        // application was sent with.
        group.MapPost("/{id:guid}/ats-check", async (
            Guid id,
            Guid? resumeId,
            CheckAtsHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, resumeId, ct)).ToHttpResult());

        // GET — reads back what was stored, running no model. The same split
        // GetAnalysis.cs makes, and the reason the result is a table rather than
        // a computed response.
        group.MapGet("/{id:guid}/ats-check", async (
            Guid id,
            GetAtsResultHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(id, ct)).ToHttpResult());

        return app;
    }
}
