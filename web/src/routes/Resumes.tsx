import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { FileText, Plus, Sparkles, X } from 'lucide-react';

import { Failure } from '../components/Failure';
import { Screen } from '../components/Screen';
import {
  ApiError,
  addResumeSkill,
  asApiError,
  getResume,
  listResumes,
  removeResumeSkill,
  type ResumeDetail,
  type ResumeSummary,
} from '../lib/api';
import { formatInstant, humanise } from '../lib/format';

/* The shelf: every version of your CV, and what each one actually says.
 *
 * The skill list here is not decoration — it is the input to the ATS check. The
 * check matches skill ROWS, not the words in the document, which is the
 * recorded limitation from Phase 5: a CV whose prose says "PostgreSQL" but
 * whose skill list says "SQL" gets reported as missing PostgreSQL. So this
 * screen carries the same add/remove controls the check's board does, and says
 * why they matter. It is the correction path, not the synonym fix.
 *
 * `sourceText` is the verbatim extracted text and it is real personal data. It
 * stays on this screen: it is rendered because "is this what the parser read?"
 * is the only way to explain a surprising check, and it is never sent anywhere.
 */

export default function Resumes() {
  const { id } = useParams();
  const navigate = useNavigate();

  const [shelf, setShelf] = useState<ResumeSummary[] | null>(null);
  const [error, setError] = useState<ApiError | null>(null);

  const reloadShelf = useCallback(
    () => listResumes().then(setShelf).catch((e) => setError(asApiError(e))),
    [],
  );

  useEffect(() => {
    void reloadShelf();
  }, [reloadShelf]);

  /* Land on something. A shelf with one CV on it and nothing selected is a
   * screen asking a question with one possible answer, so it answers it —
   * `replace`, so the back button leaves the screen rather than bouncing
   * between the empty state and the first résumé. */
  useEffect(() => {
    if (!id && shelf && shelf.length > 0) navigate(`/resumes/${shelf[0]!.id}`, { replace: true });
  }, [id, shelf, navigate]);

  return (
    <Screen
      title="Résumés"
      lede="Every version you have imported, and the skills each one claims."
    >
      {error && <Failure error={error} what="load your résumés" />}
      {!shelf && !error && (
        <p className="quiet" aria-live="polite">
          Loading…
        </p>
      )}

      {shelf?.length === 0 && (
        <div className="state">
          <h2>No résumé uploaded yet</h2>
          <p>
            The ATS check compares a job ad against a résumé's skills, so it needs one first.{' '}
            <Link to="/upload">Upload a CV</Link> — a PDF, a Word file or plain text — and
            confirm what the parser read.
          </p>
        </div>
      )}

      {shelf && shelf.length > 0 && (
        <div className="shelf-layout">
          <nav className="shelf" aria-label="Résumé versions">
            <ul>
              {shelf.map((r) => (
                <li key={r.id}>
                  <Link
                    to={`/resumes/${r.id}`}
                    className="shelf-item"
                    aria-current={r.id === id ? 'true' : undefined}
                  >
                    <span className="shelf-label">{r.label}</span>
                    <span className="shelf-meta quiet">
                      <span className="num">{r.skillCount}</span>{' '}
                      {r.skillCount === 1 ? 'skill' : 'skills'}
                      {r.sourceFormat && <> · {humanise(r.sourceFormat)}</>}
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          </nav>

          {/* Keyed on the id so switching résumé REMOUNTS the pane. That is
              what clears the previous one's data; doing it by hand meant a
              synchronous setState inside the effect, which is both a cascading
              render and a lint warning worth not having. */}
          {id ? <Detail key={id} id={id} onSkillsChanged={reloadShelf} /> : null}
        </div>
      )}
    </Screen>
  );
}

/* ---- One résumé ---------------------------------------------------------- */

function Detail({ id, onSkillsChanged }: { id: string; onSkillsChanged: () => void }) {
  const [resume, setResume] = useState<ResumeDetail | null>(null);
  const [error, setError] = useState<ApiError | null>(null);

  const load = useCallback(
    () =>
      getResume(id)
        .then(setResume)
        .catch((e) => setError(asApiError(e))),
    [id],
  );

  useEffect(() => {
    void load();
  }, [load]);

  if (error) return <Failure error={error} what="load that résumé" />;
  if (!resume)
    return (
      <p className="quiet" aria-live="polite">
        Loading…
      </p>
    );

  return (
    <div className="resume">
      <header className="resume-head">
        <div>
          <h2>{resume.label}</h2>
          <p className="resume-who">
            {resume.fullName ?? <span className="quiet">no name found</span>}
            {resume.headline && <> · {resume.headline}</>}
          </p>
          <p className="quiet">
            {resume.sourceFileName ? (
              <>
                <FileText size={13} aria-hidden /> {resume.sourceFileName}
                {resume.sourceFormat && <> · {humanise(resume.sourceFormat)}</>}
              </>
            ) : (
              'Entered by hand'
            )}{' '}
            · updated {formatInstant(resume.updatedAtUtc)}
          </p>
        </div>
        <Link className="btn btn-primary" to="/ats-check">
          Check a job against this
        </Link>
      </header>

      {/* The parser's contact-detail read, which is the single highest-value
          thing on an ATS check: Phase 5's real-CV run found the biggest risk was
          the parser losing the candidate's name. If it is missing here, it was
          missing to the check too. */}
      <section className="panel">
        <div className="panel-head">
          <h3>What the parser read as contact details</h3>
        </div>
        <dl className="facts">
          <Fact label="Name" value={resume.fullName} />
          <Fact label="Email" value={resume.email} />
          <Fact label="Phone" value={resume.phone} />
          <Fact label="Location" value={resume.location} />
        </dl>
        {(!resume.fullName || !resume.email) && (
          <p className="refusal" role="status">
            <strong>Something the parser could not find.</strong> A real ATS reads this the
            same way. If it is in your document, it is probably in a header, a text box or a
            column — the layouts parsers most often drop.
          </p>
        )}
      </section>

      <Skills resume={resume} onChanged={() => Promise.all([load(), onSkillsChanged()])} />

      {resume.experiences.length > 0 && (
        <section className="panel">
          <div className="panel-head">
            <h3>Experience</h3>
            <span className="quiet num">{resume.experiences.length}</span>
          </div>
          <ol className="history">
            {resume.experiences.map((x) => (
              <li key={x.id}>
                <p className="history-head">
                  <strong>{x.title ?? 'Role not named'}</strong>
                  <span className="quiet"> · {x.employer}</span>
                </p>
                {/* Dates stay as the document wrote them. Transcribing "Mar 2021"
                    beats guessing a DateOnly — Models/Resume.cs makes the case,
                    and this screen does not undo it by reformatting. */}
                {(x.startText || x.endText) && (
                  <p className="quiet history-when">
                    {x.startText ?? '?'} – {x.endText ?? 'present'}
                  </p>
                )}
                {x.highlights.length > 0 && (
                  <ul className="history-points">
                    {x.highlights.map((h, i) => (
                      <li key={i}>{h}</li>
                    ))}
                  </ul>
                )}
              </li>
            ))}
          </ol>
        </section>
      )}

      {resume.educations.length > 0 && (
        <section className="panel">
          <div className="panel-head">
            <h3>Education</h3>
          </div>
          <ul className="history">
            {resume.educations.map((e) => (
              <li key={e.id}>
                <p className="history-head">
                  <strong>{e.qualification ?? 'Qualification not named'}</strong>
                  {e.institution && <span className="quiet"> · {e.institution}</span>}
                </p>
                {e.yearText && <p className="quiet history-when">{e.yearText}</p>}
              </li>
            ))}
          </ul>
        </section>
      )}

      {/* Closed by default. It is long, it is the least structured thing here,
          and it is the most personal — but it is also the only way to answer
          "why did the check say that", so it is one click away rather than on
          another screen. */}
      <details className="panel source-text">
        <summary>
          <span>What the parser actually extracted</span>
          <span className="quiet num">{resume.sourceText.length} characters</span>
        </summary>
        <p className="quiet">
          Verbatim, before any structuring. The check's "already in your text" hint reads
          this, not the skill list — which is why a word can be here and still be reported
          as missing. Nothing on this screen leaves your machine.
        </p>
        <pre className="source-body">{resume.sourceText}</pre>
      </details>
    </div>
  );
}

function Fact({ label, value }: { label: string; value: string | null }) {
  return (
    <>
      <dt>{label}</dt>
      <dd>{value ?? <span className="quiet">not found</span>}</dd>
    </>
  );
}

/* ---- Skills -------------------------------------------------------------- */

/* The same two writes the ATS board makes, on the screen that owns the résumé.
 * `POST /resumes/{id}/skills` and its inverse are the whole correction path for
 * the skill-row limitation, so they belong somewhere you can reach without
 * first picking a job to check against. */
function Skills({
  resume,
  onChanged,
}: {
  resume: ResumeDetail;
  onChanged: () => Promise<unknown>;
}) {
  const [adding, setAdding] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  async function add(e: React.FormEvent) {
    e.preventDefault();
    const name = adding.trim();
    if (!name) return;
    setBusy(true);
    setError(null);
    try {
      await addResumeSkill(resume.id, name);
      setAdding('');
      await onChanged();
    } catch (err) {
      setError(asApiError(err));
    } finally {
      setBusy(false);
    }
  }

  async function remove(skillName: string) {
    setError(null);
    try {
      await removeResumeSkill(resume.id, skillName);
      await onChanged();
    } catch (err) {
      setError(asApiError(err));
    }
  }

  return (
    <section className="panel">
      <div className="panel-head">
        <h3>Skills this CV claims</h3>
        <span className="quiet num">{resume.skills.length}</span>
      </div>

      <ul className="pills">
        {resume.skills.map((s) => (
          <li key={s.skillName}>
            <span className="pill" data-source={s.source}>
              {s.skillName}
              {/* Which rows a model wrote — those are the ones worth doubting. */}
              {s.source === 'AiExtracted' && (
                <Sparkles size={11} aria-label="extracted by the model" className="pill-mark" />
              )}
              <button
                type="button"
                className="pill-remove"
                aria-label={`Remove ${s.skillName}`}
                onClick={() => void remove(s.skillName)}
              >
                <X size={12} aria-hidden />
              </button>
            </span>
          </li>
        ))}
        <li>
          <form className="pill-add" onSubmit={add}>
            <label className="sr-only" htmlFor="add-resume-skill">
              Add a skill to this résumé
            </label>
            <input
              id="add-resume-skill"
              value={adding}
              placeholder="Add a skill"
              onChange={(e) => setAdding(e.target.value)}
            />
            <button type="submit" disabled={busy || !adding.trim()} aria-label="Add skill">
              <Plus size={13} aria-hidden />
            </button>
          </form>
        </li>
      </ul>

      {error && <Failure error={error} what="change this résumé's skills" />}

      <p className="panel-foot">
        These rows are what the ATS check compares against — not the prose below. Matching is
        exact, so if an ad says <code>.NET</code> and this list says <code>C#</code>, the
        check reports a gap. Adding the ad's word here is the fix, and it takes one click
        from the check itself.
      </p>
    </section>
  );
}
