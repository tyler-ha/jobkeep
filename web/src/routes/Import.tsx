import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ChevronRight, RefreshCw, Trash2, Upload } from 'lucide-react';

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
import { formatInstant, humanise } from '../lib/format';

/* Import & confirm — the gate.
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

export default function Import() {
  const { id } = useParams();
  return id ? <Review id={id} /> : <Queue />;
}

/* ---- The queue ----------------------------------------------------------- */

const VIEWS: { status: ImportStatus; label: string }[] = [
  { status: 'AwaitingReview', label: 'Waiting on you' },
  { status: 'Committed', label: 'Confirmed' },
  { status: 'Discarded', label: 'Discarded' },
];

function Queue() {
  const [view, setView] = useState<ImportStatus>('AwaitingReview');
  /* Bumped after an upload to force the list to refetch. A counter rather than
   * a callback into the child: the child owns its own fetch, and this only has
   * to say "what you have is stale". */
  const [generation, setGeneration] = useState(0);

  return (
    <Screen
      title="Import"
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
                : 'Nothing discarded'}
          </h2>
          <p>
            {view === 'AwaitingReview'
              ? 'Upload a document above and it lands here for review.'
              : view === 'Committed'
                ? 'Confirmed imports are receipts — the interesting view of one is the row it created.'
                : 'A discarded import keeps its extracted text, so a bad parse stays diagnosable.'}
          </p>
        </div>
      )}

      {items && items.length > 0 && (
        <ul className="queue">
          {items.map((d) => (
            <li key={d.id}>
              <Link to={`/import/${d.id}`} className="queue-item">
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
  const [sourceUrl, setSourceUrl] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const input = useRef<HTMLInputElement>(null);

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
      setFile(null);
      setLabel('');
      setSourceUrl('');
      if (input.current) input.current.value = '';
      onSwitchView();
      onUploaded();
      /* Straight to the review. The upload is not the thing the user came to
       * do — confirming what was read is — and a queue with one new row on it
       * is a screen asking them to click the row they just created. */
      navigate(`/import/${created.id}`);
    } catch (err) {
      setError(asApiError(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="panel uploader" onSubmit={submit}>
      <div className="panel-head">
        <h2>Upload a document</h2>
        <span className="quiet">PDF, Word or plain text</span>
      </div>

      <div className="upload-grid">
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
                <span>{k === 'Resume' ? 'A CV' : 'A job ad'}</span>
              </label>
            ))}
          </div>
        </fieldset>

        <label className="field">
          <span>File</span>
          {/* No `accept` beyond the formats the extractor actually handles —
              offering a filter wider than DocumentTextExtractor supports moves
              the failure from the file picker to a 400. */}
          <input
            ref={input}
            type="file"
            accept=".pdf,.docx,.txt,.md,text/plain,text/markdown,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            required
          />
        </label>

        {kind === 'Resume' ? (
          <label className="field">
            <span>Call this version</span>
            <input
              value={label}
              onChange={(e) => setLabel(e.target.value)}
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
          <Upload size={15} aria-hidden />
          {busy ? 'Reading the document…' : 'Upload and read'}
        </button>
        {busy && (
          <span className="quiet" aria-live="polite">
            The text is extracted here, then a local model structures it. Nothing is written
            to your data yet.
          </span>
        )}
      </div>
    </form>
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

  useEffect(() => {
    let live = true;
    getImport(id)
      .then((r) => {
        if (!live) return;
        setImported(r);
        setDraft(r.draft);
      })
      .catch((e) => live && setError(asApiError(e)));
    return () => {
      live = false;
    };
  }, [id]);

  if (error && !imported) return <Failure error={error} what="load that import" />;
  if (!imported || !draft)
    return (
      <p className="quiet" aria-live="polite">
        Loading…
      </p>
    );

  const editable = imported.status === 'AwaitingReview';

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
      navigate('/import');
    } catch (e) {
      setError(asApiError(e));
    }
  }

  return (
    <div className="screen-detail">
      <nav className="crumbs" aria-label="Breadcrumb">
        <Link to="/import">Import</Link>
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

      {imported.status === 'Discarded' && (
        <p className="refusal" role="status">
          <strong>Discarded.</strong> The extracted text is kept so a bad parse stays
          diagnosable — it is how you tell a document that extracted badly from one the
          model structured badly.
        </p>
      )}

      {error && <Failure error={error} what="save this import" />}

      <div className="review">
        <div className="review-draft">
          {draft.resume && (
            <ResumeForm
              draft={draft.resume}
              editable={editable}
              onChange={(resume) => {
                setDraft({ resume, posting: null });
                setSaved(false);
              }}
            />
          )}
          {draft.posting && (
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
              <span className="quiet num">{imported.extractedText.length}</span>
            </div>
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
            {busy === 'confirming' ? 'Confirming…' : 'Confirm — create it'}
          </button>
          <button type="button" className="btn" onClick={() => void save()} disabled={busy !== null}>
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
              in the page, and it does not seize focus from the screen. */}
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

          <span className="quiet" role="status">
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
          hint="These rows are what the ATS check compares against, and matching is exact. Write the words the ads use."
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
            onClick={() => set('skills', [...draft.skills, { name: '', required: true }])}
          >
            Add a skill
          </button>
        )}
        <p className="panel-foot">
          The must-have flag is what splits the ATS check's two stages. A nice-to-have you
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
