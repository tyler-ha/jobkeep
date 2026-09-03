import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ChevronRight, ClipboardPaste, ExternalLink, Plus, Sparkles, X } from 'lucide-react';

import { Failure } from '../components/Failure';
import {
  APPLICATION_STATUSES,
  ApiError,
  addPostingSkill,
  addRequirement,
  analyzePosting,
  asApiError,
  getAnalysis,
  getApplication,
  getAtsResult,
  removePostingSkill,
  removeRequirement,
  updateApplication,
  type AnalysisSummaryResponse,
  type ApplicationDetail,
  type ApplicationStatus,
  type AtsCheckResponse,
  type PostingSkillResponse,
  type RequirementResponse,
} from '../lib/api';
import { formatDateOnly, formatInstant, formatSalary, humanise } from '../lib/format';

/* One job, everything known about it, and the two things you do from here:
 * correct what was extracted, and check it against a résumé.
 *
 * Three requests, not one, and the split is a module boundary rather than an
 * oversight. `ai_analyses` belongs to the Ai module and `ats_results` to Ats, so
 * neither is projected into ApplicationDetail — ApplicationDetail.cs argues this
 * at length. Both extra reads answer 404 for "not run yet", which is a normal
 * state on this screen and is rendered as an invitation, never as an error.
 */

export default function JobPost() {
  const { id = '' } = useParams();

  const [app, setApp] = useState<ApplicationDetail | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [analysis, setAnalysis] = useState<AnalysisSummaryResponse | null>(null);
  const [check, setCheck] = useState<AtsCheckResponse | null>(null);
  const [analyzing, setAnalyzing] = useState(false);
  /* Two things now refuse: the status lifecycle, and the analyser with no ad to
   * read. Both are 400s and both are rules rather than faults, so the banner
   * carries its own lead rather than hard-coding the status one. */
  const [refusal, setRefusal] = useState<{ lead: string; message: string } | null>(null);
  const [editingAd, setEditingAd] = useState(false);
  const [adText, setAdText] = useState('');
  const [savingAd, setSavingAd] = useState(false);

  const reload = useCallback(
    () => getApplication(id).then(setApp).catch((e) => setError(asApiError(e))),
    [id],
  );

  useEffect(() => {
    let live = true;
    getApplication(id)
      .then((a) => live && setApp(a))
      .catch((e) => live && setError(asApiError(e)));

    /* Never analysed and never checked are both 404 here, and both are ordinary.
     * Anything else is swallowed too: a broken side panel should not take the
     * job post down with it. */
    getAnalysis(id).then((a) => live && setAnalysis(a)).catch(() => {});
    getAtsResult(id).then((c) => live && setCheck(c)).catch(() => {});

    return () => {
      live = false;
    };
  }, [id]);

  if (error) {
    return (
      <div className="screen-detail">
        <Failure error={error} what="load this application" />
        <p style={{ marginTop: 'var(--s-4)' }}>
          <Link to="/applications">Back to all applications</Link>
        </p>
      </div>
    );
  }

  if (!app) {
    return (
      <p className="quiet" aria-live="polite">
        Loading…
      </p>
    );
  }

  const posting = app.posting;
  const salary = formatSalary(
    posting.salaryMin,
    posting.salaryMax,
    posting.salaryCurrency,
    posting.salaryPeriod,
  );
  const mustHave = posting.skills.filter((s) => s.isRequired);
  const niceToHave = posting.skills.filter((s) => !s.isRequired);

  async function setStatus(next: ApplicationStatus) {
    setRefusal(null);
    try {
      setApp(await updateApplication(id, { status: next }));
    } catch (e) {
      const err = asApiError(e);
      /* A 400 here is the status lifecycle refusing the move — an Offer can
       * only be reached from an active application. That is a rule, not a
       * fault, so it is explained in place and the select snaps back. */
      if (err.isRuleRefusal)
        setRefusal({ lead: 'Not a move this application can make.', message: err.message });
      else setError(err);
    }
  }

  async function analyse() {
    setRefusal(null);
    setAnalyzing(true);
    try {
      await analyzePosting(id);
      setAnalysis(await getAnalysis(id));
      await reload();
    } catch (e) {
      /* AnalyzePosting refuses with a 400 when the posting has no description,
       * which is an ordinary state on this screen and not a fault. Before this
       * it went to setError, and setError replaces the WHOLE screen with a
       * failure card — so an application logged by hand lost its entire detail
       * view the first time anyone pressed Analyse. setStatus already routed
       * refusals correctly; this one did not. */
      const err = asApiError(e);
      if (err.isRuleRefusal)
        setRefusal({ lead: 'The analyser had nothing to read.', message: err.message });
      else setError(err);
    } finally {
      setAnalyzing(false);
    }
  }

  /* The repair path for every application logged before the form had an ad box,
   * and for every one logged without pasting it. PATCH accepts `description`;
   * it always did — the screen simply never offered it while telling the reader
   * to "paste the ad in". */
  async function saveAd() {
    setSavingAd(true);
    setRefusal(null);
    try {
      setApp(await updateApplication(id, { description: adText.trim() || null }));
      setEditingAd(false);
    } catch (e) {
      const err = asApiError(e);
      if (err.isRuleRefusal)
        setRefusal({ lead: 'That ad could not be saved.', message: err.message });
      else setError(err);
    } finally {
      setSavingAd(false);
    }
  }

  return (
    <div className="screen-detail">
      <nav className="crumbs" aria-label="Breadcrumb">
        <Link to="/applications">Applications</Link>
        <ChevronRight size={14} aria-hidden />
        <span>{posting.company.name}</span>
      </nav>

      <header className="post-head">
        <div>
          <h1>{posting.title}</h1>
          <p className="post-facts">
            <strong>{posting.company.name}</strong>
            {posting.location && <> · {posting.location}</>}
            {posting.employmentType !== 'FullTime' && <> · {humanise(posting.employmentType)}</>}
            {salary && <> · {salary}</>}
          </p>
        </div>

        <div className="post-actions">
          <Link className="btn btn-primary" to={`/applications/${id}/ats-check`}>
            Check against my CV
          </Link>
          <label className="field field-inline">
            <span className="sr-only">Status</span>
            <select
              value={app.status}
              onChange={(e) => void setStatus(e.target.value as ApplicationStatus)}
            >
              {APPLICATION_STATUSES.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
          </label>
          <span className="quiet">
            applied <time dateTime={app.dateApplied}>{formatDateOnly(app.dateApplied)}</time>
          </span>
        </div>
      </header>

      {refusal && (
        <p className="refusal" role="status">
          <strong>{refusal.lead}</strong> {refusal.message}
        </p>
      )}

      <div className="post-body">
        <div className="post-main">
          <section className="panel">
            <div className="panel-head">
              <h2>Skills the ad names</h2>
              <span className="quiet num">{posting.skills.length} extracted</span>
            </div>

            <SkillGroup
              label="Must have"
              skills={mustHave}
              applicationId={id}
              required
              onChanged={reload}
            />
            <SkillGroup
              label="Nice to have"
              skills={niceToHave}
              applicationId={id}
              required={false}
              onChanged={reload}
            />

            <p className="panel-foot">
              Pulled out of the ad when you saved it. Anything wrong, edit it — the check
              reads these rows, not the prose.
            </p>
          </section>

          <Requirements
            applicationId={id}
            requirements={posting.requirements}
            onChanged={reload}
          />

          {/* The ad, and the one place it can be corrected.
              Until Phase 6.6 this panel said "Paste the ad in and the analyser
              has something to read" and offered nothing to paste into — the
              empty state instructed an action the screen did not provide, and
              `description` was reachable only through a hand-written PATCH or
              through the upload pipeline. The edit box below is that half. */}
          <section className="panel">
            <div className="panel-head">
              <h2>The ad</h2>
              <div className="ad-head-actions">
                {posting.sourceUrl && (
                  <a href={posting.sourceUrl} target="_blank" rel="noreferrer" className="quiet-link">
                    Open the original <ExternalLink size={13} aria-hidden />
                  </a>
                )}
                {!editingAd && (
                  <button
                    type="button"
                    className="btn btn-quiet"
                    onClick={() => {
                      setAdText(posting.description ?? '');
                      setEditingAd(true);
                    }}
                  >
                    <ClipboardPaste size={15} aria-hidden />
                    {posting.description ? 'Edit the ad' : 'Paste the ad'}
                  </button>
                )}
              </div>
            </div>

            {editingAd ? (
              <>
                <label className="field">
                  <span className="sr-only">The advertisement text</span>
                  <textarea
                    rows={14}
                    value={adText}
                    onChange={(e) => setAdText(e.target.value)}
                    placeholder="Paste the advertisement here — the whole thing, boilerplate and all."
                  />
                </label>
                {/* The count is the honest signal about what the model will get.
                    A 3B model reading 5,000 characters of company history to
                    find ten technologies in the last paragraph is the case that
                    started this, so saying the size out loud is worth a line. */}
                <p className="quiet field-hint">
                  <span className="num">{adText.trim().length.toLocaleString()}</span> characters.
                  The analyser reads this and nothing else.
                </p>
                <div className="add-actions">
                  <button
                    type="button"
                    className="btn btn-primary"
                    onClick={() => void saveAd()}
                    disabled={savingAd}
                  >
                    {savingAd ? 'Saving…' : 'Save the ad'}
                  </button>
                  <button type="button" className="btn" onClick={() => setEditingAd(false)}>
                    Cancel
                  </button>
                </div>
              </>
            ) : posting.description ? (
              <div className="prose">
                {posting.description.split(/\n{2,}/).map((para, i) => (
                  <p key={i}>{para}</p>
                ))}
              </div>
            ) : (
              <p className="quiet">
                No ad text was saved with this one, so there is nothing for the analyser to
                read. Paste it in above, or <Link to="/upload">upload the ad as a file</Link>{' '}
                and let the parser fill it in.
              </p>
            )}
          </section>
        </div>

        <aside className="post-rail">
          <section className="panel">
            <h2>Last check</h2>
            {check ? (
              <>
                <p className="check-headline">
                  <span className="num check-figure">{check.matchedSkills.length}</span> of{' '}
                  <span className="num">
                    {check.matchedSkills.length + check.missingMustHaveSkills.length}
                  </span>{' '}
                  must-have skills matched
                </p>
                <p className="quiet">
                  against <strong>{check.resumeLabel ?? 'a résumé'}</strong>,{' '}
                  {formatInstant(check.checkedAtUtc)}
                </p>
                {check.missingMustHaveSkills.length > 0 && (
                  <p className="check-open">
                    Still open: {check.missingMustHaveSkills.join(', ')}.
                  </p>
                )}
                <Link className="btn" to={`/applications/${id}/ats-check`}>
                  Open the check
                </Link>
              </>
            ) : (
              <>
                <p className="quiet">
                  This job has not been checked against a résumé yet. The check is a set
                  difference over your skills, so it costs nothing to run.
                </p>
                <Link className="btn" to={`/applications/${id}/ats-check`}>
                  Run the first check
                </Link>
              </>
            )}
          </section>

          <section className="panel">
            <h2>What the model read</h2>
            {analysis ? (
              <>
                <p className="quiet">
                  Seniority <strong>{analysis.seniority}</strong> · {analysis.modelUsed ?? 'unknown model'}
                </p>
                {analysis.summary && <p className="prose-sm">{analysis.summary}</p>}
                <p className="quiet">Read {formatInstant(analysis.analyzedAtUtc)}.</p>
              </>
            ) : (
              <p className="quiet">
                The ad has not been analysed. Running it extracts skills and requirements
                from the prose and adds them to the rows above.
              </p>
            )}
            <button type="button" className="btn" onClick={() => void analyse()} disabled={analyzing}>
              <Sparkles size={15} aria-hidden />
              {analyzing ? 'Reading the ad…' : analysis ? 'Read it again' : 'Analyse the ad'}
            </button>
            {analyzing && (
              <p className="quiet" aria-live="polite">
                A local model is reading the whole ad. This takes a few seconds and does not
                leave your machine.
              </p>
            )}
          </section>

          {app.notes && (
            <section className="panel">
              <h2>Your notes</h2>
              <p className="prose-sm">{app.notes}</p>
            </section>
          )}
        </aside>
      </div>
    </div>
  );
}

function SkillGroup({
  label,
  skills,
  applicationId,
  required,
  onChanged,
}: {
  label: string;
  skills: PostingSkillResponse[];
  applicationId: string;
  required: boolean;
  onChanged: () => Promise<unknown>;
}) {
  const [adding, setAdding] = useState('');
  const [busy, setBusy] = useState(false);

  async function add(e: React.FormEvent) {
    e.preventDefault();
    if (!adding.trim()) return;
    setBusy(true);
    try {
      await addPostingSkill(applicationId, { skillName: adding.trim(), isRequired: required });
      setAdding('');
      await onChanged();
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="skill-group">
      <h3 className="skill-group-head">
        {label} <span className="quiet num">{skills.length}</span>
      </h3>
      <ul className="pills">
        {skills.map((s) => (
          <li key={s.skillName}>
            <span className="pill" data-source={s.source}>
              {s.skillName}
              {/* The user needs to know which rows a model wrote, because those
                  are the ones worth doubting. */}
              {s.source === 'AiExtracted' && (
                <Sparkles size={11} aria-label="extracted by the model" className="pill-mark" />
              )}
              <button
                type="button"
                className="pill-remove"
                aria-label={`Remove ${s.skillName}`}
                onClick={async () => {
                  await removePostingSkill(applicationId, s.skillName);
                  await onChanged();
                }}
              >
                <X size={12} aria-hidden />
              </button>
            </span>
          </li>
        ))}
        <li>
          <form className="pill-add" onSubmit={add}>
            <label className="sr-only" htmlFor={`add-${label}`}>
              Add a {label.toLowerCase()} skill
            </label>
            <input
              id={`add-${label}`}
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
    </div>
  );
}

function Requirements({
  applicationId,
  requirements,
  onChanged,
}: {
  applicationId: string;
  requirements: RequirementResponse[];
  onChanged: () => Promise<unknown>;
}) {
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);

  async function add(e: React.FormEvent) {
    e.preventDefault();
    if (!text.trim()) return;
    setBusy(true);
    try {
      await addRequirement(applicationId, {
        text: text.trim(),
        kind: 'Qualification',
        isMustHave: true,
      });
      setText('');
      await onChanged();
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel">
      <div className="panel-head">
        <h2>Written requirements</h2>
        <span className="quiet num">{requirements.length}</span>
      </div>

      {requirements.length === 0 ? (
        <p className="quiet">
          None recorded. These are the sentences a skill list cannot hold — years of
          experience, a way of working — and they are the only part of the check a model
          answers.
        </p>
      ) : (
        <ul className="reqs">
          {requirements.map((r) => (
            <li key={r.id}>
              <blockquote>{r.text}</blockquote>
              <span className="req-meta quiet">
                {r.isMustHave ? 'Must have' : 'Nice to have'} · {r.kind}
              </span>
              <button
                type="button"
                className="pill-remove"
                aria-label="Remove this requirement"
                onClick={async () => {
                  await removeRequirement(applicationId, r.id);
                  await onChanged();
                }}
              >
                <X size={12} aria-hidden />
              </button>
            </li>
          ))}
        </ul>
      )}

      <form className="req-add" onSubmit={add}>
        <label className="sr-only" htmlFor="add-req">
          Add a written requirement
        </label>
        <input
          id="add-req"
          value={text}
          placeholder="“5+ years of professional backend engineering experience”"
          onChange={(e) => setText(e.target.value)}
        />
        <button type="submit" className="btn" disabled={busy || !text.trim()}>
          Add
        </button>
      </form>
    </section>
  );
}
