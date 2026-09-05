using System.Threading.Channels;

namespace Jobkeep.Modules.Documents;

// The in-memory half of the import parse queue. Phase 6.5 group 6.
//
// THE DURABLE QUEUE IS `document_imports.Status == Parsing`, not this. That is
// the whole design, and it is why there is no new table and no migration: the
// state a worker needs to find its work is already a column, already committed,
// already visible in the review queue's "Still reading" tab.
//
// This channel is a LATENCY OPTIMISATION on top of that column — it saves the
// worker from polling the database to notice a row that the request thread
// already knew about. Everything still works if a message is lost: the row stays
// Parsing, and ImportParseWorker's startup sweep picks it up. That is the
// property worth protecting, so do not let this become the source of truth by
// enqueuing an id that has not been saved first.
//
// Unbounded because the producer is a human uploading a file. The only way to
// grow this queue faster than it drains is to upload documents faster than a
// language model can read them, by hand, which is not a load profile. A bounded
// channel would buy backpressure nobody can generate at the cost of deciding
// what to do when it is full.
// PHASE 11.2b — the message carries the OWNER as well as the id.
//
// The worker runs outside any request, so its scope has no principal and the
// owner query filter would hide every row it was sent to work on. It has to
// state whose work it is doing, and the cheapest place to learn that is from
// whoever queued it: the upload knows (it just saved the row) and the sweep
// reads the column. The alternative — an extra scope and an extra query to look
// the owner up — buys nothing, since the enqueuer had the value in hand.
public class ImportParseQueue
{
    public readonly record struct Work(Guid ImportId, Guid OwnerUserId);

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(
        // One reader (the worker) and, in principle, many writers (concurrent
        // uploads). Telling the channel so lets it use the cheaper single-reader
        // implementation.
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // Fire and forget, deliberately. TryWrite on an unbounded channel only fails
    // if the channel has been completed, which happens on shutdown — and a
    // dropped id at shutdown is exactly the case the startup sweep exists for,
    // so there is nothing useful to do with the false.
    public void Enqueue(Guid importId, Guid ownerUserId) =>
        _channel.Writer.TryWrite(new Work(importId, ownerUserId));

    public IAsyncEnumerable<Work> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
