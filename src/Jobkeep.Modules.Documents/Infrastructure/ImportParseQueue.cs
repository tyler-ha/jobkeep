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
public class ImportParseQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        // One reader (the worker) and, in principle, many writers (concurrent
        // uploads). Telling the channel so lets it use the cheaper single-reader
        // implementation.
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // Fire and forget, deliberately. TryWrite on an unbounded channel only fails
    // if the channel has been completed, which happens on shutdown — and a
    // dropped id at shutdown is exactly the case the startup sweep exists for,
    // so there is nothing useful to do with the false.
    public void Enqueue(Guid importId) => _channel.Writer.TryWrite(importId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
