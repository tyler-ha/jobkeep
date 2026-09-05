using Jobkeep.Modules.Documents.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Jobkeep.SharedKernel;

namespace Jobkeep.Modules.Documents;

// The background worker that structures uploaded documents. Phase 6.5 group 6.
//
// ---------------------------------------------------------------------------
// Why this exists, and what it REVERSES
// ---------------------------------------------------------------------------
// The upload used to block on the model for up to 180 seconds. Group 6 split
// that: POST /imports extracts, saves and returns with the row in Parsing, and
// the model runs somewhere else. The first version of "somewhere else" was the
// CLIENT, driving POST /imports/{id}/reparse from the review screen.
//
// That was refused and replaced, at the user's instruction, because it does not
// actually deliver what the group is for. It moved the wait from one request to
// another rather than removing it: the parse was still owned by a browser tab,
// so closing the tab stranded the row in Parsing for ever and the accepted fix
// was "the user notices and clicks it again".
//
// The original refusal of a worker argued from AWS Lambda, which freezes the
// execution environment once a response is returned — so a background thread is
// not guaranteed to run there. That argument was CORRECT and is now MOOT: the
// AWS deploy (Phase 10) is parked, and the user has said the target may change
// to a service that runs a long-lived process, where a worker is simply the
// ordinary answer. The reversal is recorded in the phase doc rather than quietly
// applied, because the refused design is argued at length in two places.
//
// ---------------------------------------------------------------------------
// Crash recovery is the startup sweep, and it is why the queue is a COLUMN
// ---------------------------------------------------------------------------
// ImportParseQueue is a Channel<Guid> and channels do not survive a restart.
// That would matter if the channel were the queue. It is not — the queue is
// every row with Status == Parsing, which is committed database state, so the
// sweep below reconstructs the work list from scratch on every boot.
//
// The consequence worth stating: kill the process mid-parse and the row is
// picked up on the next start. The client-driven version could not do that at
// all, and it is the property the whole reversal buys.
public class ImportParseWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ImportParseQueue _queue;
    private readonly ILogger<ImportParseWorker> _log;

    public ImportParseWorker(
        IServiceScopeFactory scopes, ImportParseQueue queue, ILogger<ImportParseWorker> log)
    {
        _scopes = scopes;
        _queue = queue;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await SweepAsync(ct);

        await foreach (var work in _queue.ReadAllAsync(ct))
            await ParseAsync(work, ct);
    }

    // Everything left claiming a parse nobody is running. On a clean boot this
    // finds nothing; after a crash, a `docker compose restart api`, or a
    // deploy mid-upload, it finds exactly the rows that were in flight.
    //
    // ONE INSTANCE IS ASSUMED, AND THAT IS THE CEILING. Two processes would both
    // sweep the same rows and both parse them — wasted model calls, and a last-
    // writer-wins race on the draft. Nothing corrupts, because RestructureImport
    // is a whole-row replace, but the work is done twice. The fix is a lease
    // column plus a reaper, which is Phase 15's alongside the outbox and is
    // written down in ImportStatus.Parsing rather than half-built here. Today
    // the app runs as one container (compose.yaml), so the case cannot arise.
    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();

            // PHASE 11.2b — ACROSS EVERY USER. Named rather than bare because
            // naming the filter is the house rule, not because there is a second
            // one to keep: `document_imports` is not ISoftDeletable, and Owner is
            // the only filter on it. Written by name anyway so that adding one
            // later does not silently widen this query.
            //
            // The sweep is the one read in the app that legitimately has
            // no owner: it is recovering work for whoever left it, and this
            // scope has no principal to be. It reads the owner off each row and
            // hands it to the worker, so the parse itself runs scoped again —
            // the exemption stops at the one query that needs it.
            var stranded = await db.DocumentImports
                .IgnoreQueryFilters([QueryFilters.Owner])
                .Where(d => d.Status == ImportStatus.Parsing)
                .Select(d => new ImportParseQueue.Work(d.Id, d.OwnerUserId))
                .ToListAsync(ct);

            foreach (var work in stranded) _queue.Enqueue(work.ImportId, work.OwnerUserId);

            if (stranded.Count > 0)
                _log.LogInformation(
                    "Recovered {Count} import(s) left parsing by a previous run.", stranded.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed sweep must not take the worker down with it, because the
            // worker is also how every FUTURE upload gets parsed. The rows it
            // failed to find are still Parsing and still recoverable on the next
            // start; the uploads it would otherwise refuse to serve are not.
            _log.LogError(ex, "Could not sweep imports left parsing. Continuing.");
        }
    }

    private async Task ParseAsync(ImportParseQueue.Work work, CancellationToken ct)
    {
        var id = work.ImportId;
        try
        {
            // A scope per item, because the handler chain is scoped — the
            // mediator is registered ServiceLifetime.Scoped in Program.cs and
            // DocumentsDbContext is scoped. Resolving either into this singleton
            // would be the captive-dependency trap DocumentsModule warns about.
            using var scope = _scopes.CreateScope();

            // PHASE 11.2b — become the row's owner BEFORE anything resolves a
            // DbContext. Each context captures the current user in its
            // constructor, so this assignment has to come first or the handler
            // gets a context that can see nothing and reports the import
            // missing. Setting it here rather than exempting RestructureImport
            // is deliberate: the slice is shared with the "Read it again" button,
            // and an IgnoreQueryFilters inside it would unscope the HTTP path
            // too.
            scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId = work.OwnerUserId;

            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            // The same slice the "Read it again" button calls. It knows this
            // case: RestructureImport branches on whether it is finishing an
            // upload, and on a model failure it closes the row out into
            // AwaitingReview with a warning rather than leaving it Parsing.
            // So a bad model answer is already handled and is not an exception.
            await sender.Send(new RestructureImport(id), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything that reaches here is unexpected — the database being
            // gone, say. The row stays Parsing and the next startup sweep
            // retries it, which is the same recovery every other failure here
            // uses. Swallowed rather than rethrown for the reason above: one bad
            // document must not stop the queue.
            _log.LogError(ex, "Parsing import {ImportId} failed. It stays queued.", id);
        }
    }
}
