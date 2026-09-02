namespace Jobkeep.Shared;

// PHASE 13.3c — the seam that replaces two dropped CASCADEs.
//
// ---------------------------------------------------------------------------
// Why a publisher and not a contract call
// ---------------------------------------------------------------------------
// 13.3b dropped five foreign keys because they crossed module boundaries. Two of
// them were CASCADEs — `ats_results.ApplicationId` and `ai_analyses.PostingId` —
// and a cascade is not a question, it is a consequence: when an application goes,
// its ATS result goes, and Applications does not need to know that in order to
// delete an application.
//
// The obvious alternative was a fifth contract: IAtsContract.DeleteForApplication,
// called by DeleteApplication. It works today and it is the wrong shape for where
// this is going. A contract call makes the DELETER hold the list of everyone who
// cares, so adding a sixth module means editing Applications, and when these
// become services it means N synchronous HTTP calls on the delete path, any of
// which can fail and none of which the caller can retry usefully. That is the
// thing a queue exists to avoid, and this is the seam that becomes one.
//
// So the direction is inverted: Applications announces what happened, and the
// modules that care subscribe. Applications names no module. 13.4 replaces the
// three types below with the chosen mediator's INotification and its handler
// interface, and the CALL SITES do not change — which is the point of writing
// them now rather than waiting.
//
// ---------------------------------------------------------------------------
// Why there is no IDomainEvent marker interface
// ---------------------------------------------------------------------------
// The obvious `where TEvent : IDomainEvent` cannot be written. Event types are
// module vocabulary, so they live in Jobkeep.Contracts beside the interfaces
// that share their subject — and Contracts may reference no other Jobkeep
// assembly, including this one (ModuleBoundaryTests.Foundation_projects_depend_
// on_nothing_of_ours). A marker here would force that reference.
//
// `where TEvent : class` is what is left, and it is weaker: any reference type
// can be published. Accepted rather than worked around, because the alternatives
// are worse — a marker in Contracts makes Contracts the place implementations
// live, and dropping the constraint entirely allows a struct to be published and
// silently boxed per handler.
//
// This whole file is plain BCL on purpose. Jobkeep.SharedKernel.csproj has zero
// package references and its comment says that is load-bearing, so the publisher
// resolves handlers through System.IServiceProvider rather than through
// GetServices<T>, which would drag Microsoft.Extensions.DependencyInjection.
// Abstractions into every module at once.

// One module's reaction to something that happened in another.
//
// Contravariant so a handler for a base type would receive derived events. No
// event hierarchy exists and none is planned; the modifier costs nothing and
// removing it later would be a breaking change to every implementation.
public interface IDomainEventHandler<in TEvent> where TEvent : class
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct = default);
}

// Announces that something happened. The publisher does not know, and must not
// learn, who is listening.
public interface IDomainEventPublisher
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : class;
}

// In-process, synchronous, no retry, no persistence.
//
// Every one of those is a limitation and every one of them is what 13.4 and
// Phase 14 exist to change. Stated plainly so the gap is recorded rather than
// discovered:
//
//   * A handler runs in the caller's request, on the caller's thread, AFTER the
//     caller has committed. If it throws, the exception reaches the caller — the
//     delete has already happened, so the response is a 500 describing a write
//     that succeeded. That is deliberate. The alternative is swallowing the
//     failure, which leaves exactly the silent orphan row this step exists to
//     stop, and a loud 500 is what a test can catch.
//   * There is no outbox, so a crash between the commit and the publish loses
//     the event. At single-user volume with an idempotent handler (both of the
//     two below are deletes) the residue is an invisible orphan; the real fix is
//     an outbox table written in the same transaction as the delete, which is
//     Phase 14's problem because it is only worth its cost once the subscriber
//     is a separate process.
//   * Handlers run in registration order and one failing stops the rest. With
//     two handlers on two different events, ordering is not observable today.
public sealed class DomainEventPublisher : IDomainEventPublisher
{
    private readonly IServiceProvider _services;

    public DomainEventPublisher(IServiceProvider services) => _services = services;

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : class
    {
        // GetService(typeof(IEnumerable<T>)) rather than the GetServices<T>
        // extension: same behaviour from the container, no package reference.
        // An event nobody subscribes to resolves to an empty sequence, not null,
        // but the cast is defensive because IServiceProvider only promises null
        // for an unregistered service.
        var handlers = _services.GetService(typeof(IEnumerable<IDomainEventHandler<TEvent>>))
            as IEnumerable<IDomainEventHandler<TEvent>>;

        if (handlers is null) return;

        foreach (var handler in handlers)
            await handler.HandleAsync(domainEvent, ct);
    }
}
