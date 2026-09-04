import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  DndContext,
  DragOverlay,
  PointerSensor,
  useDraggable,
  useDroppable,
  useSensor,
  useSensors,
  type DragEndEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import { ChevronRight, GripVertical, Plus, RefreshCw, X } from 'lucide-react';

import { Failure } from '../components/Failure';
import { Screen } from '../components/Screen';
import { StatusChip } from '../components/StatusChip';
import {
  ApiError,
  addResumeSkill,
  asApiError,
  getApplication,
  getMatchResult,
  getResume,
  listApplications,
  listResumes,
  removeResumeSkill,
  runMatchCheck,
  updateApplication,
  type ApplicationDetail,
  type ApplicationListItem,
  type MatchCheckResponse,
  type ResumeDetail,
  type ResumeSummary,
} from '../lib/api';
import { formatDateOnly, formatInstant } from '../lib/format';

/* The match check.
 *
 * Two routes, one screen, because a screen owns its use case end to end:
 * /match-check with no application is the picker, /applications/:id/match-check is
 * the board. The approved artboard only draws the board — it is reached from an
 * application — but the navigation has an entry for it, and an entry that
 * cannot answer "which job?" would be a dead end.
 *
 * The whole point of the board is one round trip: the ad asks for a skill your
 * résumé's skill list does not name, you drag it across, and the gap closes.
 * That is the shipped correction path for the known limitation — the check
 * matches skill ROWS, not skill TEXT, so a CV that says "C#" reports ".NET" as
 * missing. It is not the synonym fix, and the copy here does not pretend it is.
 */

export default function MatchCheck() {
  const { id } = useParams();
  return id ? <Board applicationId={id} /> : <Picker />;
}

/* ---- The picker ---------------------------------------------------------- */

function Picker() {
  const [items, setItems] = useState<ApplicationListItem[] | null>(null);
  const [error, setError] = useState<ApiError | null>(null);

  useEffect(() => {
    listApplications('?pageSize=50&sort=DateApplied&direction=Desc')
      .then((p) => setItems(p.items))
      .catch((e) => setError(asApiError(e)));
  }, []);

  return (
    <Screen
      title="Match check"
      lede="Pick the job you want to check a résumé against."
    >
      {error && <Failure error={error} what="load your applications" />}
      {!items && !error && <p className="quiet" aria-live="polite">Loading…</p>}
      {items?.length === 0 && (
        <div className="state">
          <h2>Nothing to check yet</h2>
          <p>Record a job first — the check compares a résumé against what an ad asks for.</p>
        </div>
      )}
      {items && items.length > 0 && (
        <ul className="pick-list">
          {items.map((a) => (
            <li key={a.id}>
              <Link to={`/applications/${a.id}/match-check`} className="pick">
                <span className="pick-role">{a.title}</span>
                <span className="quiet">{a.company}</span>
                <StatusChip status={a.status} />
                <time className="col-date" dateTime={a.dateApplied}>
                  {formatDateOnly(a.dateApplied)}
                </time>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </Screen>
  );
}

/* ---- The board ----------------------------------------------------------- */

const CV_DROP_ID = 'cv';

function Board({ applicationId }: { applicationId: string }) {
  const [app, setApp] = useState<ApplicationDetail | null>(null);
  const [resumes, setResumes] = useState<ResumeSummary[]>([]);
  const [resumeId, setResumeId] = useState<string | null>(null);
  const [resume, setResume] = useState<ResumeDetail | null>(null);
  const [check, setCheck] = useState<MatchCheckResponse | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [running, setRunning] = useState(false);
  const [dragging, setDragging] = useState<string | null>(null);
  const [announce, setAnnounce] = useState('');

  /* Pointer only, deliberately. dnd-kit ships a KeyboardSensor, but a keyboard
   * drag is press-space, arrow, arrow, press-space to do what one button does in
   * one keystroke — and mounting it would put a focusable handle on every row.
   * The "Add" button on each gap IS the keyboard path, and it is the better one;
   * the grip stays out of the accessibility tree entirely. */
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }));

  useEffect(() => {
    let live = true;
    Promise.all([getApplication(applicationId), listResumes()])
      .then(([a, rs]) => {
        if (!live) return;
        setApp(a);
        setResumes(rs);
        /* The linked résumé is the default because it is the one you actually
         * sent. Falling back to the first is better than an empty board. */
        setResumeId(a.resumeId ?? rs[0]?.id ?? null);
      })
      .catch((e) => live && setError(asApiError(e)));
    return () => {
      live = false;
    };
  }, [applicationId]);

  /* Read the stored result before running anything. Re-checking overwrites the
   * row and costs a model call; showing what was already decided costs one
   * SELECT. Only run automatically when there is nothing stored. */
  const refresh = useCallback(
    async (opts: { rerun: boolean }) => {
      if (!resumeId) return;
      setRunning(true);
      setError(null);
      /* The résumé is fetched alongside the check rather than after it — the
       * two are independent reads and the board needs both before it can
       * paint. */
      const detail = getResume(resumeId);
      try {
        let next: MatchCheckResponse;
        try {
          next = opts.rerun
            ? await runMatchCheck(applicationId, resumeId)
            : await getMatchResult(applicationId);
        } catch (e) {
          const err = asApiError(e);
          /* Nothing stored yet — 404 is what "never checked" looks like here.
           * Run it: three of its four stages are SQL, so this is cheap, and the
           * user came to this screen to see an answer rather than a prompt. */
          if (!err.isMissing || opts.rerun) throw err;
          next = await runMatchCheck(applicationId, resumeId);
        }
        /* match_results is 1:1 with the application, so the stored row may have
         * judged a different résumé than the one now selected. Showing it under
         * this résumé's name would be a lie; re-run instead. */
        if (!opts.rerun && next.resumeId !== resumeId) {
          next = await runMatchCheck(applicationId, resumeId);
        }
        setCheck(next);
        setResume(await detail);
      } catch (e) {
        setError(asApiError(e));
      } finally {
        setRunning(false);
      }
    },
    [applicationId, resumeId],
  );

  /* The one effect on this screen that really is synchronising with an external
   * system: it starts the network work the board exists to show. oxlint flags
   * the setState inside `refresh` and cannot see past the async boundary; the
   * warning is left standing rather than restructured into something less
   * honest, because moving the request out of the effect would mean firing it
   * from render. */
  useEffect(() => {
    void refresh({ rerun: false });
  }, [refresh]);

  if (error && !check) {
    return (
      <div className="screen-detail">
        <Failure error={error} what="run the check" />
        <p style={{ marginTop: 'var(--s-4)' }}>
          <Link to={`/applications/${applicationId}`}>Back to the job post</Link>
        </p>
      </div>
    );
  }

  if (app && resumes.length === 0) {
    return (
      <div className="state">
        <h2>No résumé to check against</h2>
        <p>
          The check compares a résumé's skills against what the ad asks for, so it needs
          one first. Upload a CV and it becomes available here.
        </p>
      </div>
    );
  }

  if (!app || !check || !resume) {
    return (
      <p className="quiet" aria-live="polite">
        {running ? 'Running the check…' : 'Loading…'}
      </p>
    );
  }

  const posting = app.posting;
  const mustTotal = posting.skills.filter((s) => s.isRequired).length;
  const niceTotal = posting.skills.length - mustTotal;
  const mustOpen = check.missingMustHaveSkills.length;
  const niceOpen = check.missingNiceToHaveSkills.length;
  const reqTotal = posting.requirements.length;
  const reqOpen = check.unmetRequirements.length;

  const contactMissing = check.formattingRiskNotes.some((n) => n.includes('could not find your'));
  /* The near-miss. The gap matches skill ROWS; this looks at the résumé's own
   * extracted TEXT, which GET /resumes/{id} returns, and says so when the word
   * is right there in the document. It is the difference between "you don't
   * have this" and "your skill list doesn't say you do", and the drag below is
   * how you fix the second one. */
  const haystack = resume.sourceText.toLowerCase();
  const inText = (skill: string) => haystack.includes(skill.toLowerCase());

  async function addToCv(skill: string) {
    if (!resumeId) return;
    setAnnounce(`Adding ${skill} to ${resume!.label}…`);
    try {
      await addResumeSkill(resumeId, skill);
      await refresh({ rerun: true });
      setAnnounce(`${skill} added to ${resume!.label}. The check has been run again.`);
    } catch (e) {
      setError(asApiError(e));
      setAnnounce(`${skill} could not be added.`);
    }
  }

  async function removeFromCv(skill: string) {
    if (!resumeId) return;
    setAnnounce(`Removing ${skill}…`);
    try {
      await removeResumeSkill(resumeId, skill);
      await refresh({ rerun: true });
      setAnnounce(`${skill} removed from ${resume!.label}. The check has been run again.`);
    } catch (e) {
      setError(asApiError(e));
    }
  }

  function onDragEnd(e: DragEndEvent) {
    setDragging(null);
    if (e.over?.id === CV_DROP_ID && typeof e.active.id === 'string') {
      void addToCv(e.active.id);
    }
  }

  return (
    <DndContext
      sensors={sensors}
      onDragStart={(e: DragStartEvent) => setDragging(String(e.active.id))}
      onDragCancel={() => setDragging(null)}
      onDragEnd={onDragEnd}
    >
      <div className="screen-detail">
        <nav className="crumbs" aria-label="Breadcrumb">
          <Link to="/applications">Applications</Link>
          <ChevronRight size={14} aria-hidden />
          <Link to={`/applications/${applicationId}`}>{posting.company.name}</Link>
          <ChevronRight size={14} aria-hidden />
          <span>Match check</span>
        </nav>

        <header className="post-head">
          <div>
            <h1>Match check</h1>
            <p className="post-facts">
              {posting.company.name} · {posting.title}
              {check.checkedAtUtc && <> · checked {formatInstant(check.checkedAtUtc)}</>}
            </p>
          </div>
          <div className="post-actions">
            <label className="field field-inline">
              <span className="sr-only">Résumé to check against</span>
              <select value={resumeId ?? ''} onChange={(e) => setResumeId(e.target.value)}>
                {resumes.map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.label}
                  </option>
                ))}
              </select>
            </label>
            {resumeId !== app.resumeId && (
              <button
                type="button"
                className="btn"
                onClick={async () => setApp(await updateApplication(applicationId, { resumeId }))}
              >
                Set as the one you sent
              </button>
            )}
            <button
              type="button"
              className="btn btn-primary"
              onClick={() => void refresh({ rerun: true })}
              disabled={running}
            >
              <RefreshCw size={15} aria-hidden />
              {running ? 'Checking…' : 'Run check again'}
            </button>
          </div>
        </header>

        <p className="lede-wide">
          Four of the five stages below are a database join, not a guess. Drag a skill into
          your CV to close one.
        </p>

        {check.warning && (
          <p className="degraded" role="status">
            <strong>The written requirements were not assessed.</strong> {check.warning}
          </p>
        )}

        {error && <Failure error={error} what="update your résumé" />}

        <ol className="stages">
          <Stage label="Contact details" done={contactMissing ? 0 : 1} total={1} unit="found" />
          <Stage label="Must-have skills" done={mustTotal - mustOpen} total={mustTotal} />
          <Stage label="Nice to have" done={niceTotal - niceOpen} total={niceTotal} />
          <Stage
            label="Written requirements"
            done={reqTotal - reqOpen}
            total={reqTotal}
            unassessed={Boolean(check.warning)}
          />
          <Stage
            label="Format risks"
            done={check.formattingRiskNotes.length === 0 ? 1 : 0}
            total={1}
            unit="clear"
          />
        </ol>

        <p className="sr-only" role="status" aria-live="polite">
          {announce}
        </p>

        <div className="board">
          <section className="panel board-ad">
            <div className="panel-head">
              <h2>The ad asks for</h2>
              <span className="quiet num">
                {mustOpen + niceOpen} still open of {posting.skills.length}
              </span>
            </div>

            {mustOpen + niceOpen === 0 ? (
              <p className="quiet">
                Every skill this ad names is on <strong>{resume.label}</strong>. Nothing to
                close here.
              </p>
            ) : (
              <ul className="gap-list">
                {check.missingMustHaveSkills.map((s) => (
                  <GapSkill
                    key={s}
                    name={s}
                    mustHave
                    nearMiss={inText(s)}
                    onAdd={() => void addToCv(s)}
                  />
                ))}
                {check.missingNiceToHaveSkills.map((s) => (
                  <GapSkill
                    key={s}
                    name={s}
                    mustHave={false}
                    nearMiss={inText(s)}
                    onAdd={() => void addToCv(s)}
                  />
                ))}
              </ul>
            )}

            <p className="panel-foot">
              A skill here is not a failure — it is a line your résumé's skill list does not
              have yet. The ones marked as already in your text are the near misses: the
              words are in the document, the list just does not say so.
            </p>
          </section>

          <CvPanel
            resume={resume}
            matched={check.matchedSkills}
            onRemove={removeFromCv}
            dragging={dragging}
          />
        </div>

        <div className="board-foot">
          <section className="panel">
            <div className="panel-head">
              <h2>Still open</h2>
              <span className="quiet num">{reqOpen} written</span>
            </div>
            {check.warning ? (
              <p className="quiet">
                Not assessed this run — see the note above. The skill gap and the formatting
                notes are complete; they do not use a model.
              </p>
            ) : reqOpen === 0 ? (
              <p className="quiet">
                Every written requirement has evidence in <strong>{resume.label}</strong>.
              </p>
            ) : (
              <ul className="quotes">
                {check.unmetRequirements.map((r) => (
                  <li key={r}>
                    <blockquote>{r}</blockquote>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="panel">
            <h2>How this was decided</h2>
            <p className="prose-sm">
              The skill gap is a set difference over the shared skills table — exact,
              instant, and free. Only the written requirements above are answered by a
              model, which is why the check still runs when the model is down and says
              so instead of failing.
            </p>
            {check.formattingRiskNotes.length > 0 && (
              <ul className="notes">
                {check.formattingRiskNotes.map((n) => (
                  <li key={n}>{n}</li>
                ))}
              </ul>
            )}
            <p className="quiet">
              There is deliberately no score. Tested on this project's own CV, the same
              document as a designed PDF lost the candidate's name, location and every
              listed skill — a number out of 100 would have averaged that away into a digit.
            </p>
          </section>
        </div>
      </div>

      {/* The dragged chip follows the pointer as itself rather than as a ghost
          of the row, so what you are carrying is unambiguous. */}
      <DragOverlay dropAnimation={null}>
        {dragging ? <span className="pill pill-dragging">{dragging}</span> : null}
      </DragOverlay>
    </DndContext>
  );
}

function Stage({
  label,
  done,
  total,
  unit,
  unassessed,
}: {
  label: string;
  done: number;
  total: number;
  unit?: string;
  unassessed?: boolean;
}) {
  const pct = total === 0 ? null : Math.round((done / total) * 100);
  const complete = pct === 100;

  return (
    <li className="stage" data-complete={complete || undefined} data-unassessed={unassessed || undefined}>
      <span className="stage-label">{label}</span>
      <span className="stage-value mono">
        {unassessed ? 'not run' : total === 0 ? 'none' : unit ? (done ? unit : `not ${unit}`) : `${pct}%`}
      </span>
      {/* The rule under each stage is the same number again, not a decoration
          standing in for it — the figure above is what you read. A 0–1 ratio,
          because the CSS scales it rather than resizing it. */}
      <span
        className="stage-rule"
        role="presentation"
        style={{ ['--fill' as string]: unassessed || pct === null ? 0 : pct / 100 }}
      />
      {!unit && total > 0 && (
        <span className="stage-sub quiet num">
          {done} of {total}
        </span>
      )}
    </li>
  );
}

function GapSkill({
  name,
  mustHave,
  nearMiss,
  onAdd,
}: {
  name: string;
  mustHave: boolean;
  nearMiss: boolean;
  onAdd: () => void;
}) {
  const { listeners, setNodeRef, isDragging } = useDraggable({ id: name });

  return (
    <li className="gap" data-dragging={isDragging || undefined} data-near-miss={nearMiss || undefined}>
      <span className="gap-grip" ref={setNodeRef} {...listeners} aria-hidden>
        <GripVertical size={14} />
      </span>
      <span className="gap-name">{name}</span>
      <span className="gap-kind quiet">{mustHave ? 'must have' : 'nice to have'}</span>
      {nearMiss && <span className="gap-hint">in your text</span>}
      {/* The click path is not a fallback for the drag — it is the keyboard
          path, and it is the faster one on a trackpad. Both do the same thing. */}
      <button type="button" className="gap-add" onClick={onAdd} aria-label={`Add ${name} to your CV`}>
        <Plus size={13} aria-hidden />
        Add
      </button>
    </li>
  );
}

function CvPanel({
  resume,
  matched,
  onRemove,
  dragging,
}: {
  resume: ResumeDetail;
  matched: string[];
  onRemove: (skill: string) => Promise<void>;
  dragging: string | null;
}) {
  const { setNodeRef, isOver } = useDroppable({ id: CV_DROP_ID });
  const matchedSet = new Set(matched.map((m) => m.toLowerCase()));

  return (
    <section
      ref={setNodeRef}
      className="panel board-cv"
      /* Amber alone cannot carry this state — #FFC53D is 1.45 on the ground,
         under the 3.0 non-text threshold. The zone gets a solid outline and a
         changed label alongside the amber, so the cue survives without colour. */
      data-armed={dragging ? '' : undefined}
      data-over={isOver || undefined}
    >
      <div className="panel-head">
        <h2>{resume.label}</h2>
        <span className="quiet mono">
          {resume.sourceFormat?.toLowerCase() ?? 'text'} · {resume.sourceText.length.toLocaleString()} chars
        </span>
      </div>

      <p className="quiet">
        {[
          resume.fullName ? 'name' : null,
          resume.email ? 'email' : null,
          resume.location ? 'location' : null,
        ]
          .filter(Boolean)
          .join(', ') || 'no contact details'}{' '}
        found by the parser.
      </p>

      <div className="cv-head">
        <h3>Skills on your CV</h3>
        <span className="num cv-count">{resume.skills.length}</span>
      </div>

      <ul className="pills">
        {resume.skills.map((s) => (
          <li key={s.skillName}>
            <span className="pill" data-matched={matchedSet.has(s.skillName.toLowerCase()) || undefined}>
              {s.skillName}
              <button
                type="button"
                className="pill-remove"
                aria-label={`Remove ${s.skillName} from ${resume.label}`}
                onClick={() => void onRemove(s.skillName)}
              >
                <X size={12} aria-hidden />
              </button>
            </span>
          </li>
        ))}
      </ul>

      <p className="drop-hint" aria-hidden>
        {isOver ? 'Let go to add it' : dragging ? 'Drop it anywhere in here' : ''}
      </p>
    </section>
  );
}
