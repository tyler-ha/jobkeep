using Jobkeep.SharedKernel;
namespace Jobkeep.Modules.Documents.Domain;

// What kind of document was uploaded. The user says which — it is not sniffed
// from the content.
//
// Guessing was considered and rejected: a resume and a job ad share most of
// their vocabulary (both name technologies, titles, companies and locations),
// so a classifier here would be wrong occasionally and silently, and the cost of
// being wrong is structuring the document against the other schema entirely. The
// user always knows which file they just picked. One radio button beats a
// probabilistic answer to a question that was never in doubt.
public enum DocumentKind
{
    Resume,
    JobPosting
}

// Where an import is in the review cycle. This enum IS the feature the user
// asked for: nothing an uploaded document produces reaches a real table until a
// human has looked at it and said yes.
public enum ImportStatus
{
    // Extracted and structured; the draft is waiting for a human to confirm or
    // correct it. This is the only state in which the draft can be edited.
    AwaitingReview,

    // Confirmed. The real rows exist and CommittedEntityId points at them.
    // Terminal — a committed import is a receipt, not a document to re-edit.
    Committed,

    // Thrown away without committing. Kept rather than deleted so a bad parse
    // stays diagnosable: the extracted text is still there to look at, which is
    // how you tell "the PDF extracted badly" from "the model structured it badly".
    Discarded,

    // Phase 13.2c. A commit was started and did not finish, and the import is
    // waiting to be re-run.
    //
    // It exists because the posting path stopped being one database transaction.
    // Committing a job ad now claims this row, calls the Applications module
    // through a contract, and writes the resulting id back; the call in the
    // middle is not something a transaction on this side can roll back once
    // Applications is its own service. So the states a commit can leave behind
    // grew by one: not "AwaitingReview, as though nothing happened", but "this
    // was attempted, here is what we know".
    //
    // What distinguishes it from AwaitingReview is what is safe to do next. An
    // AwaitingReview import has definitely created nothing. A CommitFailed one
    // may have — CommittedEntityId is the answer: null means nothing was logged
    // and re-running is a clean retry, non-null means the rows exist and
    // re-running only finishes the bookkeeping. CommitImport reads exactly that.
    //
    // Deliberately NOT terminal. A commit that failed because Postgres blinked is
    // the case this whole state exists for, and it is fixed by pressing the
    // button again.
    CommitFailed
}

// The format extraction actually found, after sniffing the bytes — not what the
// file extension claimed.
//
// Named SourceFormat rather than the obvious DocumentFormat because
// DocumentFormat.OpenXml puts a *namespace* of that name in scope, and CS0118
// ("is a namespace but is used like a type") is the result. Same class of
// collision as SliceResult vs GreenDonut's Result, and resolved the same way:
// rename ours.
public enum SourceFormat
{
    PlainText,
    Markdown,
    Pdf,
    Docx
}

// One upload, from bytes to confirmed records.
//
// ---------------------------------------------------------------------------
// The pipeline, and why it is stored in the middle
// ---------------------------------------------------------------------------
// "Parse a document" is two problems with opposite failure modes, and the design
// rests on keeping them apart:
//
//                bytes ──[extraction]──> text ──[structuring]──> draft ──[human]──> rows
//                         deterministic          a language model        confirmed
//                         fails loudly           fails plausibly
//
// Extraction is a library call: same file, same text, every time. When it fails
// it throws or returns something obviously empty. Structuring is a sampled model:
// when it fails it returns something that looks right and isn't.
//
// Persisting the text BETWEEN them buys three things. Re-structuring after a
// prompt change costs no re-upload. A bad extraction is diagnosable without a
// model in the loop — you read ExtractedText and see immediately whether the
// problem was upstream. And each half gets the test it deserves: a real fixture
// file for extraction, a canned model reply for structuring.
//
// This is the same shape Phase 4 already uses (`ai_analyses` is stored, and
// re-analyzing updates rather than re-uploads), so it is a pattern being reused
// rather than invented.
//
// ---------------------------------------------------------------------------
// What is NOT here: the file
// ---------------------------------------------------------------------------
// There is no `bytea` column and no S3 key. The original bytes are read, turned
// into text, hashed for provenance, and dropped.
//
// The cost argument: keeping them means either bytea in Postgres, which eats
// Neon's free-tier 0.5 GB — the one genuinely scarce resource in the deployed
// plan — or object storage, which is a new AWS surface in a phase that is
// otherwise entirely local.
//
// The security argument is the better one. Nothing is ever written to disk, so
// the whole class of path-traversal-via-uploaded-filename bugs cannot occur
// here: there is no path to traverse. FileName below is stored as a label and
// never used to open anything.
//
// Keeping the original file is on the backlog as "document versions", where it
// belongs — it is a storage decision with a bill attached, not part of parsing.
public class DocumentImport : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DocumentKind Kind { get; set; }
    public ImportStatus Status { get; set; } = ImportStatus.AwaitingReview;

    // The name the client sent, kept for display only. Never touches a filesystem
    // path — see above. Truncated on the way in rather than trusted for length.
    public string FileName { get; set; } = string.Empty;

    // What the bytes actually were, not what the extension said.
    public SourceFormat Format { get; set; }

    public long ByteCount { get; set; }

    // SHA-256 of the uploaded bytes, hex. The only thing that survives the file
    // itself, and enough to answer "have I imported this exact document before".
    public string ContentHash { get; set; } = string.Empty;

    // The deterministic half's output. Everything downstream reads this.
    public string ExtractedText { get; set; } = string.Empty;

    // The structuring step's proposal, and the user's corrections to it, as JSON.
    //
    // A jsonb column rather than a set of draft tables, and this is a deliberate
    // call worth defending: a draft has no query surface. Nothing ever asks "find
    // drafts whose third experience entry mentions Kubernetes" — it is written
    // whole, read whole, edited whole, and then either turned into real rows or
    // discarded. Five throwaway tables mirroring the five real ones would double
    // the schema to model something whose entire lifetime is one review screen.
    //
    // The real records get real tables, because those ARE queried (Phase 5 joins
    // resume skills to posting skills). The draft does not, because it isn't.
    // jsonb rather than text so the column is at least structurally validated by
    // Postgres and readable with -> in psql when something goes wrong.
    public string DraftJson { get; set; } = "{}";

    // Which model produced the draft, mirroring AiAnalysis.ModelUsed. Null when
    // the draft has been hand-edited past recognition or the structuring step was
    // skipped. Useful for the same reason: knowing which rows a better model
    // would improve.
    public string? ModelUsed { get; set; }

    // A human-readable note about something imperfect that did not stop the
    // import — overwhelmingly "this PDF has no text layer, it looks like a scan".
    //
    // That case is the one worth naming. A scanned PDF is an image; no managed
    // library extracts text from it and OCR is a different project. The failure
    // mode to avoid is storing an empty resume and letting the Phase 5 ATS check
    // cheerfully report that you match none of the keywords. So it is detected
    // and said out loud instead.
    public string? Warning { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CommittedAtUtc { get; set; }

    // The row the commit created — a Resume for a resume, a JobApplication for a
    // posting. Deliberately NOT a foreign key: it points into two different
    // tables depending on Kind, and a nullable FK to each would be two columns
    // that must never both be set, which is a constraint the schema cannot state
    // and the code would have to remember. As a bare Guid it is honestly what it
    // is — a receipt, not a relationship. Nothing joins on it.
    public Guid? CommittedEntityId { get; set; }
}
