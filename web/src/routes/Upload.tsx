import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Briefcase,
  Check,
  ChevronRight,
  FileCheck2,
  FileDown,
  FileText,
  FileUp,
  RefreshCw,
  Save,
  Trash2,
  Upload as UploadIcon,
  X,
} from 'lucide-react';

import { Failure } from '../components/Failure';
import { Screen } from '../components/Screen';
import {
  ApiError,
  asApiError,
  confirmImport,
  discardImport,
  getImport,
  listImports,
  reparseImport,
  reviewImport,
  uploadImport,
  type DocumentKind,
  type ImportDraft,
  type ImportResponse,
  type ImportStatus,
  type ImportSummary,
  type PostingDraft,
  type RequirementKind,
  type ResumeDraft,
} from '../lib/api';
import { formatBytes, formatInstant, humanise } from '../lib/format';
import { estimateProgress } from '../lib/progress';

/* Upload & confirm — the gate.
 *
 * THE NAME IS SPLIT ON PURPOSE. The UI says upload; the wire still says import
 * — `/imports`, `ImportStatus`, `ImportDraft`, `confirmImport`. Renaming the
 * screen was a Phase 6.5 ask (the screen already called itself three different
 * things); renaming the API to follow a word would have been a breaking change
 * bought for nothing. `lib/api.ts` carries the same note.
 *
 * This is the one feature in the app where a model's output does not become
 * data until a human has looked at it. Uploading writes one row in one table
 * nothing else reads; `POST /imports/{id}/confirm` is where a résumé, an
 * application, its skills and its requirements actually come into existence.
 * The screen is shaped around that: the draft and the text it came from sit
 * side by side, because the user's job here is to answer "does this match the
 * document" and they cannot do it with the document on another screen.
 *
 * The draft is PUT, not PATCHed — a full replace. ReviewImport.cs makes the
 * argument and it shows up directly in this UI: the correction a bad parse most
 * often needs is "delete the third role", which a partial update of a nested
 * draft cannot express.
 *
 * Uploading is the only thing in the app with no GraphQL equivalent, on purpose
 * (DocumentsModule.cs argues it at length), so this screen is REST-only and
 * that is not an oversight.
 */

export default function Upload() {
  const { id } = useParams();
  return id ? <Review id={id} /> : <Queue />;
}

/* ---- The queue ----------------------------------------------------------- */

/* "Still reading" is where a document lives between upload and draft. The server
 * owns that work now — ImportParseWorker picks these rows up, including ones left
 * behind by a crash or a restart — so this tab is a WINDOW ON THE QUEUE rather
 * than the recovery mechanism it was in the first version of group 6.
 *
 * It still earns its place: a parse is the one thing in this app that takes real
 * time, and a status a user cannot see is a status that reads as "nothing
 * happened". */
const VIEWS: { status: ImportStatus; label: string }[] = [
  { status: 'AwaitingReview', label: 'Waiting on you' },
  { status: 'Parsing', label: 'Still reading' },
  { status: 'Committed', label: 'Confirmed' },
  { status: 'Discarded', label: 'Discarded' },
  { status: 'CommitFailed', label: 'Needs another go' },
];

function Queue() {
  const [view, setView] = useState<ImportStatus>('AwaitingReview');
  /* Bumped after an upload to force the list to refetch. A counter rather than
   * a callback into the child: the child owns its own fetch, and this only has
   * to say "what you have is stale". */
  const [generation, setGeneration] = useState(0);

  return (
    <Screen
      title="Upload"
      lede="Upload a CV or a job ad. Nothing it produces becomes data until you say so."
    >
      <Uploader
        onUploaded={() => setGeneration((g) => g + 1)}
        onSwitchView={() => setView('AwaitingReview')}
      />

      <div className="tabs" role="group" aria-label="Which imports to show">
        {VIEWS.map((v) => (
          <button
            key={v.status}
            type="button"
            className="tab"
            aria-pressed={view === v.status}
            onClick={() => setView(v.status)}
          >
            {v.label}
          </button>
        ))}
      </div>

      {/* Keyed on the view AND the generation, so switching tab or finishing an
          upload remounts the list. The remount is what clears the previous
          view's rows — clearing them by hand meant a synchronous setState
          inside an effect, which is a cascading render for no gain. */}
      <QueueList key={`${view}-${generation}`} view={view} />
    </Screen>
  );
}

function QueueList({ view }: { view: ImportStatus }) {
  const [items, setItems] = useState<ImportSummary[] | null>(null);
  const [error, setError] = useState<ApiError | null>(null);

  useEffect(() => {
    let live = true;
    listImports(view)
      .then((r) => live && setItems(r))
      .catch((e) => live && setError(asApiError(e)));
    return () => {
      live = false;
    };
  }, [view]);

  return (
    <>
      {error && <Failure error={error} what="load your imports" />}
      {!items && !error && (
        <p className="quiet" aria-live="polite">
          Loading…
        </p>
      )}

      {items?.length === 0 && (
        <div className="state">
          <h2>
            {view === 'AwaitingReview'
              ? 'Nothing waiting'
              : view === 'Committed'
                ? 'Nothing confirmed yet'
                : view === 'Discarded'
                  ? 'Nothing discarded'
                  : 'Nothing stuck'}
          </h2>
          <p>
            {view === 'AwaitingReview'
              ? 'Upload a document above and it lands here for review.'
              : view === 'Committed'
                ? 'Confirmed imports are receipts — the interesting view of one is the row it created.'
                : view === 'Discarded'
                  ? 'A discarded import keeps its extracted text, so a bad parse stays diagnosable.'
                  : 'This is where a confirm that stopped half-way would show up. Empty is the good outcome.'}
          </p>
        </div>
      )}

      {items && items.length > 0 && (
        <ul className="queue">
          {items.map((d) => (
            <li key={d.id}>
              <Link to={`/upload/${d.id}`} className="queue-item">
                <span className="queue-kind" data-kind={d.kind}>
                  {d.kind === 'Resume' ? 'CV' : 'Job ad'}
                </span>
                <span className="queue-name">{d.fileName}</span>
                <span className="queue-meta quiet">
                  {humanise(d.format)} · <span className="num">{Math.round(d.byteCount / 1024)}</span> kB
                  · <span className="num">{d.textLength}</span> characters read
                </span>
                {/* A stored warning means a stage did not run. It is amber, not
                    red: a degraded parse still produced a draft worth reviewing. */}
                {d.warning && <span className="queue-warn">{d.warning}</span>}
                <time className="queue-when quiet" dateTime={d.createdAtUtc}>
                  {formatInstant(d.createdAtUtc)}
                </time>
                <ChevronRight size={16} aria-hidden className="queue-go" />
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

/* ---- The uploader -------------------------------------------------------- */

/* The extension is stripped because that is what the server does too:
 * ImportDocument.cs falls back to Path.GetFileNameWithoutExtension(...) clipped
 * to 100. Showing anything else would put a different default on screen from
 * the one that actually gets stored. */
function labelFromFile(name: string): string {
  return name.replace(/\.[^./\\]+$/, '').slice(0, 100);
}

function Uploader({
  onUploaded,
  onSwitchView,
}: {
  onUploaded: () => void;
  onSwitchView: () => void;
}) {
  const navigate = useNavigate();
  const [kind, setKind] = useState<DocumentKind>('Resume');
  const [file, setFile] = useState<File | null>(null);
  const [label, setLabel] = useState('');
  /* Whether the user has touched the label box. Without it, choosing a second
   * file would silently overwrite a name they had already typed. */
  const [labelTouched, setLabelTouched] = useState(false);
  const [sourceUrl, setSourceUrl] = useState('');
  const [dragging, setDragging] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const input = useRef<HTMLInputElement>(null);

  /* Whether this form is still mounted. Set in the effect body rather than only
   * at declaration because StrictMode mounts, unmounts and remounts — the
   * cleanup from the first pass would otherwise leave it false for good. */
  const live = useRef(true);
  useEffect(() => {
    live.current = true;
    return () => {
      live.current = false;
    };
  }, []);

  /* The value the box SHOWS. The value it SENDS is still `label`, so an
   * untouched box sends nothing and the server's own fallback stays the single
   * source of truth. This makes an existing default visible; it does not move
   * the decision into the client. */
  const shownLabel = labelTouched ? label : file ? labelFromFile(file.name) : '';

  function clearFile() {
    setFile(null);
    if (input.current) input.current.value = '';
  }

  function onDrop(e: React.DragEvent) {
    e.preventDefault();
    setDragging(false);
    const dropped = e.dataTransfer.files?.[0];
    if (!dropped) return;
    setFile(dropped);
    setError(null);
    /* The native input is the one the keyboard uses, so it is kept in step with
     * what was dropped rather than left holding a stale filename. `DataTransfer`
     * is the only way to build a FileList, and Safari < 14.1 does not have it —
     * hence the guard, not a cast. */
    if (input.current && typeof DataTransfer === 'function') {
      const dt = new DataTransfer();
      dt.items.add(dropped);
      input.current.files = dt.files;
    }
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      const created = await uploadImport(
        file,
        kind,
        label.trim() || undefined,
        sourceUrl.trim() || undefined,
      );
      clearFile();
      setLabel('');
      setLabelTouched(false);
      setSourceUrl('');
      onSwitchView();
      onUploaded();
      /* Straight to the review. The upload is not the thing the user came to
       * do — confirming what was read is — and a queue with one new row on it
       * is a screen asking them to click the row they just created.
       *
       * Honest since group 6: this used to happen up to 180 seconds after the
       * click, because the POST blocked on the model. It now happens in about a
       * second, and the review screen drives the model itself.
       *
       * GUARDED ANYWAY. react-router 7's useNavigate sets activeRef.current in
       * a layout effect with NO cleanup, so the ref stays true after unmount and
       * this fires from a resolved promise even when the user has gone
       * elsewhere. The old symptom was memorable — upload a CV, click another
       * nav link, and three minutes later the app yanked you onto the review
       * screen. Shrinking the window to a second makes it unreachable in
       * practice, not impossible; a latent bug that is merely hard to hit is
       * still a bug. */
      if (live.current) navigate(`/upload/${created.id}`);
    } catch (err) {
      setError(asApiError(err));
    } finally {
      setBusy(false);
    }
  }

  /* Three cues, not one. --pop is 1.45 on the ground, under WCAG's 3.0 non-text
   * threshold, so the amber ground CANNOT carry this state by itself: the zone
   * also changes its outline and its wording, and every cue survives colour
   * being removed. Same construction as .board-cv on the match check. */
  const Icon = dragging ? FileDown : file ? FileCheck2 : FileUp;
  const headline = dragging
    ? 'Drop it here'
    : file
      ? file.name
      : 'Drop a file here, or choose one';

  return (
    <form className="panel uploader" onSubmit={submit}>
      <div className="panel-head">
        <h2>Upload a document</h2>
        <span className="quiet">PDF, Word or plain text · up to 5 MB</span>
      </div>

      <div className="upload-grid">
        <div className="dropzone-wrap">
          {/* A label wrapping the real input, not a div with a click handler.
              The native control keeps its own keyboard path, its file picker and
              its place in the accessibility tree; only its appearance is
              replaced, and :focus-within puts the ring on the zone. */}
          <label
            className={`dropzone${dragging ? ' is-hot' : ''}${file ? ' is-chosen' : ''}`}
            onDragOver={(e) => {
              e.preventDefault();
              setDragging(true);
            }}
            onDragLeave={(e) => {
              /* Dragging across a child fires dragleave on the parent, so the
                 state has to survive it or the zone flickers on every internal
                 boundary. */
              if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDragging(false);
            }}
            onDrop={onDrop}
          >
            {/* No `accept` beyond the formats the extractor actually handles —
                offering a filter wider than DocumentTextExtractor supports moves
                the failure from the file picker to a 400.

                And NO `required`. It is redundant (the submit button is disabled
                without a file) and, on a control this rule hides, actively
                harmful: Chrome refuses to submit a form containing an invalid
                control it cannot scroll to, with "An invalid form control with
                name='' is not focusable" — and says it only in the console. */}
            <input
              ref={input}
              type="file"
              className="sr-only"
              accept=".pdf,.docx,.txt,.md,text/plain,text/markdown,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document"
              onChange={(e) => {
                setFile(e.target.files?.[0] ?? null);
                setError(null);
              }}
            />
            <Icon className="dropzone-icon" size={22} aria-hidden />
            <span className="dropzone-line">{headline}</span>
            <span className="dropzone-sub quiet">
              {file ? formatBytes(file.size) : 'PDF, DOCX, TXT or Markdown'}
            </span>
          </label>

          {/* Outside the label, or clicking it would reopen the file picker. */}
          {file && !busy && (
            <button
              type="button"
              className="dropzone-clear"
              aria-label="Remove the chosen file"
              onClick={clearFile}
            >
              <X size={14} aria-hidden />
            </button>
          )}
        </div>

        <fieldset className="field">
          <legend>What is it?</legend>
          <div className="segmented">
            {(['Resume', 'JobPosting'] as const).map((k) => (
              <label key={k} className="segment">
                <input
                  type="radio"
                  name="kind"
                  value={k}
                  checked={kind === k}
                  onChange={() => setKind(k)}
                />
                <span>
                  {k === 'Resume' ? <FileText size={14} aria-hidden /> : <Briefcase size={14} aria-hidden />}
                  {k === 'Resume' ? 'A CV' : 'A job ad'}
                </span>
              </label>
            ))}
          </div>
        </fieldset>

        {kind === 'Resume' ? (
          <label className="field">
            <span>Call this version</span>
            <input
              value={shownLabel}
              onChange={(e) => {
                setLabelTouched(true);
                setLabel(e.target.value);
              }}
              placeholder="backend-focused"
            />
            {/* The model is not asked for this: the document contains no
                evidence about how YOU organise your résumés. */}
          </label>
        ) : (
          <label className="field">
            <span>Link to the ad</span>
            <input type="url" value={sourceUrl} onChange={(e) => setSourceUrl(e.target.value)} />
          </label>
        )}
      </div>

      {error && <Failure error={error} what="upload that document" />}

      <div className="add-actions">
        <button type="submit" className="btn btn-primary" disabled={busy || !file}>
          <UploadIcon size={15} aria-hidden />
          {busy ? 'Uploading…' : 'Upload and read'}
        </button>
        {/* The progress bar used to live here, because the wait did. Since group
            6 this button is a file upload and nothing else — a bar modelling a
            three-minute model call has no business under a one-second POST. It
            moved to the review screen, which is where the model now runs. */}
      </div>
    </form>
  );
}

/* The wait, drawn. It stays in this file rather than components/: the house rule
 * is that a component moves out once a SECOND screen needs it, and nothing else
 * in the app waits on the model from the browser. If the paste path or a future
 * re-parse grows its own bar, that is the moment to promote it.
 *
 * The curve, and the argument for showing an estimate at all, are in
 * lib/progress.ts. What matters here is the accessibility shape:
 *
 * - The bar is a `progressbar`, and it is NOT inside a live region. Politely
 *   announcing a percentage that changes eight times a second is a screen-reader
 *   denial of service. `aria-valuetext` says "estimated" so the number is not
 *   mistaken for a measurement.
 * - The sentence beside it IS live, and it is static, so it is announced exactly
 *   once when the wait begins.
 * - Reduced motion is already honoured globally (tokens.css zeroes every
 *   duration), which makes the width change instant — so the tick rate drops to
 *   a second as well, or the element twitches instead of gliding.
 */
function Parsing() {
  const [elapsed, setElapsed] = useState(0);

  useEffect(() => {
    const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
    const started = Date.now();
    const id = window.setInterval(() => setElapsed(Date.now() - started), reduced ? 1000 : 120);
    return () => window.clearInterval(id);
  }, []);

  const pct = Math.round(estimateProgress(elapsed) * 100);

  return (
    <div className="parsing">
      <div
        className="progress"
        role="progressbar"
        aria-label="Reading the document"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={pct}
        aria-valuetext={`About ${pct} per cent, estimated`}
      >
        <div className="progress-bar" style={{ '--fill': pct / 100 } as React.CSSProperties} />
      </div>
      {/* One honest line. Not a staged "Extracting… / Structuring…" transition
          on a timer: the client cannot observe either stage, and a fake one is
          the part a reviewer would catch. */}
      <p className="quiet" aria-live="polite">
        Extracting the text, then a local model structures it. Usually 5–30 seconds — longer
        for a long ad. Nothing is written to your data yet.
      </p>
    </div>
  );
}

/* ---- The review ---------------------------------------------------------- */

function Review({ id }: { id: string }) {
  const navigate = useNavigate();
  const [imported, setImported] = useState<ImportResponse | null>(null);
  const [draft, setDraft] = useState<ImportDraft | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState<null | 'saving' | 'reparsing' | 'confirming'>(null);
  const [saved, setSaved] = useState(false);
  const [confirming, setConfirming] = useState(false);

  /* Load, then WATCH while the server parses. Phase 6.5 group 6.
   *
   * This screen used to DRIVE the parse — it fired POST /imports/{id}/reparse
   * when it saw a Parsing row, and the model ran inside that request. That was
   * replaced: it meant a browser tab owned the work, so closing the tab stranded
   * the row for ever. ImportParseWorker owns it now, and the client's only job
   * is to notice when the row stops saying Parsing.
   *
   * So the poll is not a fallback for something better — it is the honest read
   * of a state the server changes on its own. It costs one indexed lookup by
   * primary key every 1.5 seconds, for the seconds a parse takes.
   *
   * No liveness ref is needed for it: the interval is cleared on unmount and
   * `live` gates every setState, so navigating away stops the polling and the
   * worker carries on regardless. That is the whole point — nothing about
   * finishing the parse depends on this component still being mounted. */
  useEffect(() => {
    let live = true;
    let timer: number | undefined;

    const apply = (r: ImportResponse) => {
      if (!live) return;
      setImported(r);
      setDraft(r.draft);
      if (r.status === 'Parsing') timer = window.setTimeout(poll, 1500);
    };

    const poll = () => {
      getImport(id)
        .then(apply)
        /* A failed poll is not a failed import. The row is still being parsed by
         * the server, and a transient blip here should not paint an error over a
         * screen that is about to succeed — so it retries rather than reporting.
         * The initial load below does report, because that one failing means
         * there is nothing to show at all. */
        .catch(() => {
          if (live) timer = window.setTimeout(poll, 1500);
        });
    };

    getImport(id)
      .then(apply)
      .catch((e) => live && setError(asApiError(e)));

    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [id]);

  if (error && !imported) return <Failure error={error} what="load that import" />;
  if (!imported || !draft)
    return (
      <p className="quiet" aria-live="polite">
        Loading…
      </p>
    );

  /* CommitFailed is editable too — it means the confirm did not finish, and the
   * user's next move is to fix something and press the button again. Locking the
   * draft would leave them a screen with nothing to do on it. */
  const editable = imported.status === 'AwaitingReview' || imported.status === 'CommitFailed';

  async function save() {
    if (!draft) return;
    setBusy('saving');
    setError(null);
    try {
      const next = await reviewImport(id, draft);
      setImported(next);
      setDraft(next.draft);
      setSaved(true);
    } catch (e) {
      setError(asApiError(e));
    } finally {
      setBusy(null);
    }
  }

  async function reparse() {
    setBusy('reparsing');
    setError(null);
    try {
      const next = await reparseImport(id);
      setImported(next);
      setDraft(next.draft);
      setSaved(false);
    } catch (e) {
      setError(asApiError(e));
    } finally {
      setBusy(null);
    }
  }

  async function confirm() {
    setBusy('confirming');
    setError(null);
    try {
      /* Save first, always. The confirm reads the STORED draft, so confirming
       * without saving would commit the parse and silently throw away every
       * correction on screen — the one bug on this screen that would look like
       * the review feature not working at all. */
      const stored = await reviewImport(id, draft!);
      const receipt = await confirmImport(id);
      navigate(
        stored.kind === 'Resume'
          ? `/resumes/${receipt.committedEntityId}`
          : `/applications/${receipt.committedEntityId}`,
      );
    } catch (e) {
      setError(asApiError(e));
      setBusy(null);
    }
  }

  async function discard() {
    setError(null);
    try {
      await discardImport(id);
      navigate('/upload');
    } catch (e) {
      setError(asApiError(e));
    }
  }

  return (
    <div className="screen-detail">
      <nav className="crumbs" aria-label="Breadcrumb">
        <Link to="/upload">Upload</Link>
        <ChevronRight size={14} aria-hidden />
        <span>{imported.fileName}</span>
      </nav>

      <header className="post-head">
        <div>
          <h1>{imported.kind === 'Resume' ? 'Is this your CV?' : 'Is this the ad?'}</h1>
          <p className="post-facts">
            <strong>{imported.fileName}</strong> · {humanise(imported.format)} ·{' '}
            <span className="num">{Math.round(imported.byteCount / 1024)}</span> kB
            {imported.modelUsed && <> · read by {imported.modelUsed}</>}
          </p>
        </div>
      </header>

      {/* Stored, not computed — an unstored warning would let a later read of an
          empty draft claim the document simply had nothing in it. Amber, because
          a degraded parse is a task, not a failure. */}
      {imported.warning && (
        <p className="refusal" role="status">
          <strong>The reading was degraded.</strong> {imported.warning} Anything below that is
          blank may be in the text on the right — you can type it in.
        </p>
      )}

      {imported.status === 'Committed' && (
        <p className="refusal" role="status">
          <strong>Already confirmed.</strong> This is a receipt now, not a draft.{' '}
          {imported.committedEntityId && (
            <Link
              to={
                imported.kind === 'Resume'
                  ? `/resumes/${imported.committedEntityId}`
                  : `/applications/${imported.committedEntityId}`
              }
            >
              Open what it created
            </Link>
          )}
        </p>
      )}

      {imported.status === 'CommitFailed' && (
        <p className="refusal" role="status">
          <strong>That confirm did not finish.</strong> Confirming a job ad creates the
          application in a separate step, and this one stopped part-way through. Press
          confirm again — the server knows what already exists, so it will finish the job
          rather than logging it twice.
        </p>
      )}

      {imported.status === 'Discarded' && (
        <p className="refusal" role="status">
          <strong>Discarded.</strong> The extracted text is kept so a bad parse stays
          diagnosable — it is how you tell a document that extracted badly from one the
          model structured badly.
        </p>
      )}

      {imported.status === 'Parsing' && (
        <p className="refusal" role="status">
          <strong>Still reading it.</strong> The text is saved — that part is safe. A local
          model is turning it into a draft now. You can leave this screen; it carries on
          without you, and the queue keeps the row under “Still reading”.
        </p>
      )}

      {error && <Failure error={error} what="save this import" />}

      <div className="review">
        <div className="review-draft">
          {/* The progress bar moved here from the upload form, which is where
              the wait used to be. The extracted text stays visible beside it —
              reading what came out of the document is the one useful thing to
              do while the model works, and it is the same question the screen
              asks afterwards. */}
          {imported.status === 'Parsing' && <Parsing />}
          {imported.status !== 'Parsing' && draft.resume && (
            <ResumeForm
              draft={draft.resume}
              editable={editable}
              onChange={(resume) => {
                setDraft({ resume, posting: null });
                setSaved(false);
              }}
            />
          )}
          {imported.status !== 'Parsing' && draft.posting && (
            <PostingForm
              draft={draft.posting}
              editable={editable}
              onChange={(posting) => {
                setDraft({ resume: null, posting });
                setSaved(false);
              }}
            />
          )}
        </div>

        {/* The document, beside the draft rather than behind a tab. "Does this
            match?" is not a question you can answer from memory. */}
        <aside className="review-text">
          <div className="panel">
            <div className="panel-head">
              <h2>What was extracted</h2>
            </div>
            {/* This screen's one held moment, and the only amber it spends.
                Every other screen has one — Today's backlog count, Insights'
                top skill, the match check percentage — and the rule from
                PRODUCT.md is one per screen, in the display face, under the
                marker stroke.

                It is this number because it is the question the screen is
                actually asking: did the machine read your document at all? A
                CV that extracts to 40 characters is a scanned picture, and
                seeing that here is the difference between "the model is bad"
                and "there was nothing to read". */}
            <p className="upload-figure">
              <span className="marked">
                {imported.extractedText.length.toLocaleString('en-AU')}
              </span>
              <span className="upload-figure-label">characters read</span>
            </p>
            <pre className="source-body">{imported.extractedText}</pre>
          </div>
        </aside>
      </div>

      {editable && (
        <div className="review-actions">
          <button
            type="button"
            className="btn btn-primary"
            onClick={() => void confirm()}
            disabled={busy !== null}
          >
            <Check size={15} aria-hidden />
            {busy === 'confirming' ? 'Confirming…' : 'Confirm — create it'}
          </button>
          <button type="button" className="btn" onClick={() => void save()} disabled={busy !== null}>
            <Save size={15} aria-hidden />
            {busy === 'saving' ? 'Saving…' : 'Save corrections'}
          </button>
          <button
            type="button"
            className="btn"
            onClick={() => void reparse()}
            disabled={busy !== null}
          >
            <RefreshCw size={15} aria-hidden />
            {busy === 'reparsing' ? 'Reading again…' : 'Read it again'}
          </button>

          {/* Two-step rather than a browser confirm dialog: the same protection,
              in the page, and it does not seize focus from the screen.

              And pushed to the far end of the row (the margin is in the CSS),
              because "Discard" sitting shoulder to shoulder with "Confirm —
              create it" put the destructive action one pointer-slip from the
              primary one. Distance is the cheapest guard there is. */}
          {confirming ? (
            <span className="discard-confirm">
              <button type="button" className="btn btn-danger" onClick={() => void discard()}>
                Yes, discard it
              </button>
              <button type="button" className="btn" onClick={() => setConfirming(false)}>
                Keep it
              </button>
            </span>
          ) : (
            <button type="button" className="btn" onClick={() => setConfirming(true)}>
              <Trash2 size={15} aria-hidden />
              Discard
            </button>
          )}

          <span className="quiet review-note" role="status">
            {saved
              ? 'Corrections saved. Nothing has been created yet.'
              : 'Confirming saves your corrections first, then creates the rows.'}
          </span>
        </div>
      )}

      {/* "Read it again" overwrites the draft, corrections included. Saying so
          next to the button is cheaper than an undo. */}
      {editable && (
        <p className="panel-foot">
          Reading it again runs the model over the same extracted text and replaces the draft
          — including anything you have typed here. The document itself is never re-uploaded.
        </p>
      )}
    </div>
  );
}

/* ---- Draft editors ------------------------------------------------------- */

/* One field component rather than a label/input pair repeated fourteen times.
 * It stays in this file because nothing else needs it: the other screens edit
 * one thing at a time, and a draft is the only form in the app with this many
 * fields on it. */
function Text({
  label,
  value,
  editable,
  onChange,
  placeholder,
  wide,
}: {
  label: string;
  value: string | null;
  editable: boolean;
  onChange: (v: string | null) => void;
  placeholder?: string;
  wide?: boolean;
}) {
  return (
    <label className={wide ? 'field field-wide' : 'field'}>
      <span>{label}</span>
      <input
        value={value ?? ''}
        placeholder={placeholder}
        disabled={!editable}
        /* Empty means "the document did not say", which is null on the wire —
         * not an empty string. The commit treats them the same, but the draft
         * round-trips through PUT and a "" would come back as a value the
         * parser claimed to have found. */
        onChange={(e) => onChange(e.target.value.trim() === '' ? null : e.target.value)}
      />
    </label>
  );
}

/* A list of short strings, edited as lines. A row of inputs per item is the
 * obvious build and the worse one: reordering, deleting and adding all become
 * buttons, when a textarea already does all three with the keyboard. */
function Lines({
  label,
  hint,
  values,
  editable,
  onChange,
}: {
  label: string;
  hint?: string;
  values: string[];
  editable: boolean;
  onChange: (v: string[]) => void;
}) {
  return (
    <label className="field field-wide">
      <span>
        {label} <span className="quiet num">{values.length}</span>
      </span>
      <textarea
        rows={Math.min(10, Math.max(3, values.length + 1))}
        value={values.join('\n')}
        disabled={!editable}
        onChange={(e) =>
          onChange(
            e.target.value
              .split('\n')
              .map((s) => s.trim())
              .filter(Boolean),
          )
        }
      />
      {hint && <span className="quiet field-hint">{hint}</span>}
    </label>
  );
}

function ResumeForm({
  draft,
  editable,
  onChange,
}: {
  draft: ResumeDraft;
  editable: boolean;
  onChange: (d: ResumeDraft) => void;
}) {
  const set = <K extends keyof ResumeDraft>(key: K, value: ResumeDraft[K]) =>
    onChange({ ...draft, [key]: value });

  return (
    <>
      <section className="panel">
        <div className="panel-head">
          <h2>Who this CV says you are</h2>
        </div>
        <div className="add-grid">
          {/* The one field the model is never asked for: the document contains
              no evidence about how you organise your own résumés. */}
          <label className="field">
            <span>Call this version</span>
            <input
              value={draft.label}
              disabled={!editable}
              onChange={(e) => set('label', e.target.value)}
              required
            />
          </label>
          <Text
            label="Name"
            value={draft.fullName}
            editable={editable}
            onChange={(v) => set('fullName', v)}
          />
          <Text
            label="Email"
            value={draft.email}
            editable={editable}
            onChange={(v) => set('email', v)}
          />
          <Text
            label="Phone"
            value={draft.phone}
            editable={editable}
            onChange={(v) => set('phone', v)}
          />
          <Text
            label="Location"
            value={draft.location}
            editable={editable}
            onChange={(v) => set('location', v)}
          />
          <Text
            label="Headline"
            value={draft.headline}
            editable={editable}
            onChange={(v) => set('headline', v)}
            wide
          />
        </div>
        <p className="panel-foot">
          A parser that loses your name is the single biggest ATS risk there is — Phase 5's
          run against a real CV found exactly that. If a field is blank here, it was blank to
          the machine reading your application.
        </p>
      </section>

      <section className="panel">
        <div className="panel-head">
          <h2>Skills</h2>
        </div>
        <Lines
          label="One per line"
          hint="These rows are what the match check compares against, and matching is exact. Write the words the ads use."
          values={draft.skills}
          editable={editable}
          onChange={(v) => set('skills', v)}
        />
      </section>

      <section className="panel">
        <div className="panel-head">
          <h2>Experience</h2>
          <span className="quiet num">{draft.experience.length}</span>
        </div>
        {draft.experience.length === 0 && (
          <p className="quiet">
            The model found no roles. If the text on the right has them, the layout probably
            hid them — a table or a column. Add one below.
          </p>
        )}
        {draft.experience.map((x, i) => (
          <div className="draft-entry" key={i}>
            <div className="add-grid">
              <Text
                label="Employer"
                value={x.employer}
                editable={editable}
                onChange={(v) =>
                  set(
                    'experience',
                    draft.experience.map((e, j) => (i === j ? { ...e, employer: v ?? '' } : e)),
                  )
                }
              />
              <Text
                label="Title"
                value={x.title}
                editable={editable}
                onChange={(v) =>
                  set(
                    'experience',
                    draft.experience.map((e, j) => (i === j ? { ...e, title: v } : e)),
                  )
                }
              />
              {/* Dates stay strings, carried through unparsed. "Mar 2021" is
                  what the document said; transcribing beats guessing a date. */}
              <Text
                label="From"
                value={x.start}
                placeholder="Mar 2021"
                editable={editable}
                onChange={(v) =>
                  set(
                    'experience',
                    draft.experience.map((e, j) => (i === j ? { ...e, start: v } : e)),
                  )
                }
              />
              <Text
                label="To"
                value={x.end}
                placeholder="present"
                editable={editable}
                onChange={(v) =>
                  set(
                    'experience',
                    draft.experience.map((e, j) => (i === j ? { ...e, end: v } : e)),
                  )
                }
              />
            </div>
            <Lines
              label="Highlights"
              values={x.highlights}
              editable={editable}
              onChange={(v) =>
                set(
                  'experience',
                  draft.experience.map((e, j) => (i === j ? { ...e, highlights: v } : e)),
                )
              }
            />
            {editable && (
              <button
                type="button"
                className="btn btn-quiet"
                onClick={() =>
                  set(
                    'experience',
                    draft.experience.filter((_, j) => j !== i),
                  )
                }
              >
                <Trash2 size={14} aria-hidden />
                Remove this role
              </button>
            )}
          </div>
        ))}
        {editable && (
          <button
            type="button"
            className="btn"
            onClick={() =>
              set('experience', [
                ...draft.experience,
                { employer: '', title: null, start: null, end: null, highlights: [] },
              ])
            }
          >
            Add a role
          </button>
        )}
      </section>

      <section className="panel">
        <div className="panel-head">
          <h2>Education</h2>
          <span className="quiet num">{draft.education.length}</span>
        </div>
        {draft.education.map((e, i) => (
          <div className="draft-entry" key={i}>
            <div className="add-grid">
              <Text
                label="Institution"
                value={e.institution}
                editable={editable}
                onChange={(v) =>
                  set(
                    'education',
                    draft.education.map((x, j) => (i === j ? { ...x, institution: v ?? '' } : x)),
                  )
                }
              />
              <Text
                label="Qualification"
                value={e.qualification}
                editable={editable}
                onChange={(v) =>
                  set(
                    'education',
                    draft.education.map((x, j) => (i === j ? { ...x, qualification: v } : x)),
                  )
                }
              />
              <Text
                label="Year"
                value={e.year}
                editable={editable}
                onChange={(v) =>
                  set(
                    'education',
                    draft.education.map((x, j) => (i === j ? { ...x, year: v } : x)),
                  )
                }
              />
            </div>
            {editable && (
              <button
                type="button"
                className="btn btn-quiet"
                onClick={() =>
                  set(
                    'education',
                    draft.education.filter((_, j) => j !== i),
                  )
                }
              >
                <Trash2 size={14} aria-hidden />
                Remove
              </button>
            )}
          </div>
        ))}
        {editable && (
          <button
            type="button"
            className="btn"
            onClick={() =>
              set('education', [
                ...draft.education,
                { institution: '', qualification: null, year: null },
              ])
            }
          >
            Add a qualification
          </button>
        )}
      </section>
    </>
  );
}

const REQUIREMENT_KINDS: RequirementKind[] = ['Qualification', 'Responsibility', 'Benefit'];

function PostingForm({
  draft,
  editable,
  onChange,
}: {
  draft: PostingDraft;
  editable: boolean;
  onChange: (d: PostingDraft) => void;
}) {
  const set = <K extends keyof PostingDraft>(key: K, value: PostingDraft[K]) =>
    onChange({ ...draft, [key]: value });

  return (
    <>
      <section className="panel">
        <div className="panel-head">
          <h2>The job</h2>
        </div>
        <div className="add-grid">
          <label className="field">
            <span>Company</span>
            <input
              value={draft.company}
              disabled={!editable}
              onChange={(e) => set('company', e.target.value)}
              required
            />
          </label>
          <label className="field">
            <span>Role</span>
            <input
              value={draft.title}
              disabled={!editable}
              onChange={(e) => set('title', e.target.value)}
              required
            />
          </label>
          <Text
            label="Location"
            value={draft.location}
            editable={editable}
            onChange={(v) => set('location', v)}
          />
          <Text
            label="Link to the ad"
            value={draft.sourceUrl}
            editable={editable}
            onChange={(v) => set('sourceUrl', v)}
          />
          <label className="field field-wide">
            <span>Description</span>
            <textarea
              rows={5}
              value={draft.description ?? ''}
              disabled={!editable}
              onChange={(e) => set('description', e.target.value.trim() === '' ? null : e.target.value)}
            />
          </label>
        </div>
        <p className="panel-foot">
          The company is matched by name on confirm — an existing one is reused rather than
          duplicated, so spelling it the way you did last time matters.
        </p>
      </section>

      <section className="panel">
        <div className="panel-head">
          <h2>Skills the ad names</h2>
          <span className="quiet num">{draft.skills.length}</span>
        </div>
        <ul className="draft-skills">
          {draft.skills.map((s, i) => (
            <li key={i}>
              <input
                aria-label={`Skill ${i + 1}`}
                value={s.name}
                disabled={!editable}
                onChange={(e) =>
                  set(
                    'skills',
                    draft.skills.map((x, j) => (i === j ? { ...x, name: e.target.value } : x)),
                  )
                }
              />
              <label className="check">
                <input
                  type="checkbox"
                  checked={s.required}
                  disabled={!editable}
                  onChange={(e) =>
                    set(
                      'skills',
                      draft.skills.map((x, j) =>
                        i === j ? { ...x, required: e.target.checked } : x,
                      ),
                    )
                  }
                />
                <span>Must have</span>
              </label>
              {editable && (
                <button
                  type="button"
                  className="pill-remove"
                  aria-label={`Remove ${s.name || `skill ${i + 1}`}`}
                  onClick={() =>
                    set(
                      'skills',
                      draft.skills.filter((_, j) => j !== i),
                    )
                  }
                >
                  <Trash2 size={13} aria-hidden />
                </button>
              )}
            </li>
          ))}
        </ul>
        {editable && (
          <button
            type="button"
            className="btn"
            onClick={() =>
              // Phase 14: a hand-added skill has no kind. 'Unknown' rather than a
              // guess — the catalogue fills it from the seeded vocabulary if it
              // recognises the name, and inventing 'Technical' here would beat that
              // to it, since kind is set on create and never overwritten.
              set('skills', [...draft.skills, { name: '', required: true, kind: 'Unknown' }])
            }
          >
            Add a skill
          </button>
        )}
        <p className="panel-foot">
          The must-have flag is what splits the match check's two stages. A nice-to-have you
          have not got is a note; a must-have you have not got is the gap.
        </p>
      </section>

      <section className="panel">
        <div className="panel-head">
          <h2>Requirements, in the ad's words</h2>
          <span className="quiet num">{draft.requirements.length}</span>
        </div>
        <ul className="draft-reqs">
          {draft.requirements.map((r, i) => (
            <li key={i}>
              <textarea
                aria-label={`Requirement ${i + 1}`}
                rows={2}
                value={r.text}
                disabled={!editable}
                onChange={(e) =>
                  set(
                    'requirements',
                    draft.requirements.map((x, j) => (i === j ? { ...x, text: e.target.value } : x)),
                  )
                }
              />
              <div className="draft-req-meta">
                <label className="field field-inline">
                  <span className="sr-only">Kind of requirement {i + 1}</span>
                  <select
                    value={r.kind}
                    disabled={!editable}
                    onChange={(e) =>
                      set(
                        'requirements',
                        draft.requirements.map((x, j) =>
                          i === j ? { ...x, kind: e.target.value as RequirementKind } : x,
                        ),
                      )
                    }
                  >
                    {REQUIREMENT_KINDS.map((k) => (
                      <option key={k} value={k}>
                        {k}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="check">
                  <input
                    type="checkbox"
                    checked={r.isMustHave}
                    disabled={!editable}
                    onChange={(e) =>
                      set(
                        'requirements',
                        draft.requirements.map((x, j) =>
                          i === j ? { ...x, isMustHave: e.target.checked } : x,
                        ),
                      )
                    }
                  />
                  <span>Must have</span>
                </label>
                {editable && (
                  <button
                    type="button"
                    className="btn btn-quiet"
                    onClick={() =>
                      set(
                        'requirements',
                        draft.requirements.filter((_, j) => j !== i),
                      )
                    }
                  >
                    <Trash2 size={13} aria-hidden />
                    Remove
                  </button>
                )}
              </div>
            </li>
          ))}
        </ul>
        {editable && (
          <button
            type="button"
            className="btn"
            onClick={() =>
              set('requirements', [
                ...draft.requirements,
                { text: '', kind: 'Qualification', isMustHave: true },
              ])
            }
          >
            Add a requirement
          </button>
        )}
        <p className="panel-foot">
          These are the free-text lines the check's model stage reads. Everything else it does
          is SQL, so a model outage degrades this stage and only this one.
        </p>
      </section>
    </>
  );
}
