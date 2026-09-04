using System.Reflection;
using Mediator;
using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Analytics;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Ats;
using Jobkeep.Modules.Documents;

namespace Jobkeep.Tests.Architecture;

/// <summary>
/// Phase 13.4. Every request has a handler, and every handler is reachable.
///
/// The hole this closes is specific to a mediator and is the standard argument
/// against one. <c>ISender.Send</c> takes an <c>IRequest&lt;T&gt;</c>, so the
/// compiler checks that the RESPONSE type matches and nothing else: a request
/// record whose handler was renamed, moved to a project the composition root does
/// not reference, or never written at all still compiles at every call site and
/// throws <c>MissingMessageHandlerException</c> at runtime, on whichever route
/// nobody clicked. That is the coupling the direct <c>XHandler handler</c>
/// parameter used to make the compiler enforce, and it is what a mediator trades
/// away for the seam.
///
/// So the trade is bought back here rather than left implicit. Pure reflection,
/// no container and no database: the question is whether the TYPES pair up, and
/// AddMediator's source generator answers it from exactly the same assembly graph
/// this test walks.
/// </summary>
public class DispatchTests
{
    // Loaded through a type from each, because a referenced assembly is not in
    // the AppDomain until something touches it — and this test would otherwise
    // pass by finding nothing.
    //
    // 13.6 CORRECTED THE LAST ENTRY, and it is the best argument the namespace
    // rename made for itself. It read ISkillCatalog, which lived in the namespace
    // Jobkeep.Modules.Skills but in the ASSEMBLY Jobkeep.Contracts — so this list
    // named Contracts twice and the Skills module not at all, and every handler in
    // that module went unchecked while the line claiming to check it compiled and
    // passed. A namespace that spans two projects is exactly how that hides.
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(Jobkeep.Modules.Applications.GetApplication).Assembly,
        typeof(Jobkeep.Modules.Analytics.StatusFunnel).Assembly,
        typeof(Jobkeep.Modules.Ai.GetAnalysis).Assembly,
        typeof(Jobkeep.Modules.Ats.CheckAts).Assembly,
        typeof(Jobkeep.Modules.Documents.GetResume).Assembly,
        typeof(Jobkeep.Modules.Skills.SkillsModule).Assembly,
    ];

    private static IEnumerable<Type> ConcreteTypes() => ModuleAssemblies
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsInterface: false });

    [Fact]
    public void Every_request_has_exactly_one_handler()
    {
        var handled = ConcreteTypes()
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
            .Select(i => i.GetGenericArguments()[0])
            .ToList();

        var problems = ConcreteTypes()
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)))
            .Select(t => (Request: t, Count: handled.Count(h => h == t)))
            .Where(x => x.Count != 1)
            .Select(x => $"{x.Request.Name}: {x.Count} handlers")
            .ToList();

        Assert.True(problems.Count == 0,
            "A request must have exactly one IRequestHandler<,>. Send() cannot check this, "
            + "so an unhandled one is a 500 on the route nobody clicked:\n  "
            + string.Join("\n  ", problems));
    }

    [Fact]
    public void Every_notification_has_at_least_one_handler()
    {
        // Weaker than the request rule on purpose, and the asymmetry is the point
        // of a notification: the publisher does not know who is listening, so
        // "many" is legal and so, in principle, is none. Zero is still asserted
        // against, because both events this project publishes exist precisely to
        // replace a dropped CASCADE — an event with no subscriber means the row
        // that cascade used to delete is now surviving in silence.
        var subscribed = ConcreteTypes()
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotificationHandler<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToHashSet();

        // Contracts, deliberately: a notification is part of a module's published
        // face, so both events live there rather than in Applications. Before 13.6
        // this line read Jobkeep.Modules.Applications.ApplicationDeleted and picked
        // the same assembly by accident rather than on purpose.
        var orphans = typeof(Jobkeep.Contracts.Applications.ApplicationDeleted).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(INotification).IsAssignableFrom(t)
                        && !subscribed.Contains(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(orphans.Count == 0,
            "A published notification has no subscriber, so whatever the dropped foreign key "
            + "used to clean up is now surviving:\n  " + string.Join("\n  ", orphans));
    }
}
