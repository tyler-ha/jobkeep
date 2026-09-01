using Jobkeep.Modules.Analytics;
using Jobkeep.Shared;

namespace Jobkeep.Api.Endpoints;

// Route mapping for the Analytics module, lifted out of AnalyticsModule.cs by
// Phase 13.1 so the module project can stay a plain class library with no
// ASP.NET dependency -- the handlers never knew about HTTP, only this half did.
//
// TEMPORARY SHAPE. CLAUDE.md forbids an Endpoints/ file and it is right to: this
// is the layout Phase 2.3 deleted. It is back for one step only, because 13.5
// replaces every route here with an attribute-routed controller. Do not add a
// route to this file -- add the slice, and map it in the controller.
public static class AnalyticsEndpoints
{

    public static IEndpointRouteBuilder MapAnalyticsModule(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stats").WithTags("Analytics");

        // GET /stats/skill-demand?top=
        //
        // A bare `int? top` rather than an [AsParameters] query record. The list
        // slice needs the record because it binds ten filters that both surfaces
        // and Swagger have to agree on; one optional scalar does not earn an
        // input type, and in GraphQL it reads better as skillDemand(top: 5) than
        // as skillDemand(query: { top: 5 }).
        group.MapGet("/skill-demand", async (
            int? top,
            SkillDemandHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(top, ct)).ToHttpResult());

        // GET /stats/funnel — no parameters; the funnel is the whole table.
        group.MapGet("/funnel", async (
            StatusFunnelHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(ct)).ToHttpResult());

        // GET /stats/companies?top=
        group.MapGet("/companies", async (
            int? top,
            CompanyRollupHandler handler,
            CancellationToken ct) =>
            (await handler.HandleAsync(top, ct)).ToHttpResult());

        return app;
    }
}
