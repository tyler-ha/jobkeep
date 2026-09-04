import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { Archive, ArrowDown, ArrowUp, Plus, RotateCcw, Search, X } from 'lucide-react';

import { Failure } from '../components/Failure';
import { Screen } from '../components/Screen';
import { StatusChip } from '../components/StatusChip';
import {
  APPLICATION_STATUSES,
  ApiError,
  asApiError,
  archiveApplication,
  createApplication,
  getFunnel,
  listApplications,
  restoreApplication,
  type ApplicationFunnel,
  type ApplicationListItem,
  type ApplicationPage,
  type ApplicationSort,
  type ApplicationStatus,
} from '../lib/api';
import { formatDateOnly } from '../lib/format';

/* The list you live in during a search.
 *
 * The tiebreak in PRODUCT.md says the tool wins, so this is a dense table
 * rather than a feed of cards: columns you can scan down, sorting on the
 * headers instead of a toolbar control, and no row taller than it needs to be.
 *
 * The "CV match" column was dropped when this screen shipped, because filling it
 * meant a request per row. PHASE 9, GAP 1 BROUGHT IT BACK: ApplicationListItem
 * now carries a `match` summary, batched server-side over the page's ids through
 * IMatchContract, so the column costs one query for the whole page rather than
 * one per row. The skills column stayed — it was never a substitute, and both fit.
 *
 * One thing the approved artboard shows that the API still cannot serve, recorded
 * as a deviation in docs/phases/phase-6-frontend.md:
 *
 *  - A single "Search company or role" box. The API's company and title filters
 *    are ANDed, so one box across both is not one request. The box carries a
 *    field selector instead, which is one request and says what it is doing.
 */

const PAGE_SIZE = 25;

type Sort = { field: ApplicationSort; dir: 'Asc' | 'Desc' };

/* Phase 9. The tab row filters by one status, by "closed", or by nothing. It is a
 * union rather than a status plus a flag so that the pair the API refuses cannot
 * be constructed. */
type StatusFilter = ApplicationStatus | 'Closed' | null;

type State =
  | { tag: 'loading' }
  | { tag: 'error'; error: ApiError }
  | { tag: 'ready'; page: ApplicationPage };

/* Which column header maps to which server sort. Skills has no entry because
 * there is nothing sensible to sort a multi-valued column by. */
const COLUMNS: { label: string; field?: ApplicationSort; className?: string }[] = [
  { label: 'Company', field: 'Company' },
  { label: 'Role', field: 'Title' },
  { label: 'Skills the ad names', className: 'col-skills' },
  /* Phase 9, gap 1. No sort field: ApplicationSort has no match option, and
     adding one would mean ordering the list by another module's table — which is
     a join this module deliberately does not have. Sorting by a column the server
     fills from a contract call is a different feature, not a free one. */
  { label: 'CV match', className: 'col-match' },
  { label: 'Status', field: 'Status' },
  { label: 'Applied', field: 'DateApplied', className: 'col-date' },
  /* Phase 8. No sort field: there is nothing to order by, it is a button. The
   * header is named rather than blank, because a column of controls with no
   * heading is a column with no name — and "Archive" describes what the column
   * is for even on the rows whose button currently says Restore. */
  { label: 'Archive', className: 'col-action' },
];

export default function Applications() {
  const [state, setState] = useState<State>({ tag: 'loading' });
  const [funnel, setFunnel] = useState<ApplicationFunnel | null>(null);

  /* Insights links here with ?company=REA%20Group, which is the one deep link
   * the app makes into this screen. Read once, as the initial value of the
   * existing state, rather than kept in sync with the URL: the filters are a
   * working position, not an address, and mirroring every keystroke into the
   * history would put a hundred entries behind the back button. */
  const [params] = useSearchParams();
  const initial = params.get('company') ?? params.get('title');

  const [field, setField] = useState<'company' | 'title'>(
    params.get('title') ? 'title' : 'company',
  );
  const [term, setTerm] = useState(initial ?? '');
  /* Today's status strip links here already filtered. Anything that is not one
   * of the five real statuses is ignored rather than passed on to the API,
   * which would answer 400 for a typo in a URL. */
  const linked = params.get('status');
  /* PHASE 9 — one state, not two, and that is deliberate.
   *
   * The API REFUSES `status` and `isClosed` in the same request: they are two
   * ways of saying which stages you want, and merging them answers a question
   * nobody asked. Modelling this screen's filter as a single value that is
   * either a status, the string 'Closed', or null means the refused combination
   * is not expressible here — the UI cannot send the request the API would
   * reject. Two booleans would have invited exactly that bug. */
  const [status, setStatus] = useState<StatusFilter>(
    APPLICATION_STATUSES.includes(linked as ApplicationStatus)
      ? (linked as ApplicationStatus)
      : null,
  );
  const [sort, setSort] = useState<Sort>({ field: 'DateApplied', dir: 'Desc' });
  const [page, setPage] = useState(1);

  const [adding, setAdding] = useState(false);
  const [landed, setLanded] = useState<string | null>(null);

  /* Phase 8. Three pieces of state, and the third is the one worth explaining.
   *
   *  - `archived` drives ?includeArchived, which INCLUDES archived rows rather
   *    than showing only them. The label says "Include archived" for that reason.
   *  - `undone` is the undo banner: what was just archived, kept only until the
   *    next action. It holds the title as well as the id because the banner has
   *    to name the thing after the row has left the table.
   *  - `reload` exists because the fetch effect keys on the FILTERS, and an
   *    archive changes none of them. Bumping a counter is how a screen that owns
   *    its own use case re-runs a query without a cache layer to invalidate; the
   *    alternative is mutating the page in place, which drifts from the server's
   *    idea of totalCount and paging the moment anything else changes. */
  const [archived, setArchived] = useState(false);
  const [undone, setUndone] = useState<{ id: string; title: string } | null>(null);
  const [reload, setReload] = useState(0);

  /* Debounced, because the filter is ILIKE against Postgres and firing one
   * request per keystroke would be rude to a database that has no index on the
   * columns being filtered (F14 — deliberate, and parked). */
  const [debounced, setDebounced] = useState(initial ?? '');
  useEffect(() => {
    const t = setTimeout(() => setDebounced(term.trim()), 250);
    return () => clearTimeout(t);
  }, [term]);

  /* Every filter change goes through here rather than through an effect that
   * watches for one. It resets the page — page 3 of an unfiltered list is
   * rarely page 3 of a filtered one — and puts the screen into its loading
   * state at the moment the user caused it, which is where that belongs. */
  function change(apply: () => void) {
    apply();
    setPage(1);
    setState({ tag: 'loading' });
  }

  useEffect(() => {
    let live = true;
    const q = new URLSearchParams({
      page: String(page),
      pageSize: String(PAGE_SIZE),
      sort: sort.field,
      direction: sort.dir,
    });
    if (debounced) q.set(field, debounced);
    /* 'Closed' is not a status, so it goes out as the API's own shorthand rather
     * than as `?status=Rejected&status=Withdrawn`. Which stages count as closed is
     * decided in ApplicationStatusTransitions.Closed, server-side, and spelling
     * them out here would put a second copy of that answer in TypeScript. */
    if (status === 'Closed') q.set('isClosed', 'true');
    else if (status) q.set('status', status);
    if (archived) q.set('includeArchived', 'true');

    listApplications(`?${q}`)
      .then((p) => live && setState({ tag: 'ready', page: p }))
      .catch((e) => live && setState({ tag: 'error', error: asApiError(e) }));

    return () => {
      live = false;
    };
  }, [debounced, field, status, sort, page, archived, reload]);

  /* The per-status counts come from the funnel rather than from five more list
   * requests: it is one GROUP BY, already built in Phase 2.4, and the list
   * endpoint can only filter one status at a time. Its failure is silent — a
   * missing count degrades the tab labels, it does not break the screen. */
  const refreshCounts = () => getFunnel().then(setFunnel).catch(() => setFunnel(null));
  useEffect(() => {
    void refreshCounts();
  }, []);

  const countFor = (s: ApplicationStatus | null) =>
    !funnel ? null : s === null ? funnel.total : (funnel.stages.find((x) => x.status === s)?.count ?? 0);

  /* Archive and undo, both of which reload the list and the counts: the funnel
   * is a GROUP BY over live applications, so it moves too, and a stale tab count
   * beside a shortened table is the kind of small lie that costs trust in the
   * whole screen.
   *
   * The failure path is deliberately quiet. An archive that fails leaves the row
   * where it is and clears the banner, so the screen still shows the truth —
   * there is nothing for the user to reconcile, which is why this does not raise
   * the full <Failure> treatment a load failure gets. */
  async function archive(a: ApplicationListItem) {
    try {
      await archiveApplication(a.id);
      setUndone({ id: a.id, title: a.title });
    } finally {
      setReload((n) => n + 1);
      void refreshCounts();
    }
  }

  async function undo(id: string) {
    try {
      await restoreApplication(id);
    } finally {
      setUndone(null);
      setReload((n) => n + 1);
      void refreshCounts();
    }
  }

  function toggleSort(f: ApplicationSort) {
    change(() =>
      setSort((prev) =>
        prev.field === f
          ? { field: f, dir: prev.dir === 'Asc' ? 'Desc' : 'Asc' }
          : /* A new column starts in the direction that column is usually read:
               most recent first for a date, A–Z for a name. */
            { field: f, dir: f === 'DateApplied' || f === 'UpdatedAt' ? 'Desc' : 'Asc' },
      ),
    );
  }

  function onCreated(created: { id: string }) {
    setAdding(false);
    setLanded(created.id);
    void refreshCounts();
    /* Refetch rather than splice the new row in: the list is sorted and paged
     * by the server, and guessing where the row belongs is how a client and a
     * server start disagreeing about the same data. */
    setPage(1);
    setSort({ field: 'DateApplied', dir: 'Desc' });
    setStatus(null);
    setTerm('');
    listApplications(`?page=1&pageSize=${PAGE_SIZE}&sort=DateApplied&direction=Desc`)
      .then((p) => setState({ tag: 'ready', page: p }))
      .catch((e) => setState({ tag: 'error', error: asApiError(e) }));
  }

  return (
    <Screen
      title="Applications"
      lede="Every role you have gone for, newest first."
      actions={
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => setAdding((v) => !v)}
          aria-expanded={adding}
        >
          {adding ? <X size={16} aria-hidden /> : <Plus size={16} aria-hidden />}
          {adding ? 'Cancel' : 'Add application'}
        </button>
      }
    >
      {adding && <AddForm onCreated={onCreated} onCancel={() => setAdding(false)} />}

      <div className="toolbar">
        <div className="field-search">
          <Search size={15} aria-hidden className="field-search-icon" />
          <label className="sr-only" htmlFor="search-field">
            Search which column
          </label>
          <select
            id="search-field"
            className="field-search-select"
            value={field}
            onChange={(e) => change(() => setField(e.target.value as 'company' | 'title'))}
          >
            <option value="company">Company</option>
            <option value="title">Role</option>
          </select>
          <label className="sr-only" htmlFor="search-term">
            Filter by {field === 'company' ? 'company' : 'role'}
          </label>
          <input
            id="search-term"
            className="field-search-input"
            type="search"
            value={term}
            placeholder={field === 'company' ? 'REA Group…' : 'Backend Engineer…'}
            onChange={(e) => change(() => setTerm(e.target.value))}
          />
        </div>

        <div className="tabs" role="group" aria-label="Filter by status">
          <FilterTab active={status === null} count={countFor(null)} onClick={() => change(() => setStatus(null))}>
            All
          </FilterTab>
          {APPLICATION_STATUSES.map((s) => (
            <FilterTab
              key={s}
              active={status === s}
              count={countFor(s)}
              onClick={() => change(() => setStatus(status === s ? null : s))}
            >
              {s}
            </FilterTab>
          ))}

          {/* PHASE 9 — the tab a single-value filter could not serve. The union of
              two requests cannot be paged honestly: page 2 of Rejected and page 2
              of Withdrawn are not page 2 of anything the user asked for.

              NO COUNT, deliberately, and it is the same argument as the query
              above. The funnel returns per-stage counts, so summing Rejected and
              Withdrawn here would be easy — and it would put a second definition
              of "closed" in this file, free to drift from the server's. A tab
              reading "3" above four rows is precisely the failure that costs
              trust. If this count is wanted, /stats/funnel should publish it. */}
          <FilterTab
            active={status === 'Closed'}
            count={null}
            onClick={() => change(() => setStatus(status === 'Closed' ? null : 'Closed'))}
          >
            Closed
          </FilterTab>
        </div>

        {/* Phase 8. A tab, not a checkbox, because it belongs to the same row of
            filters and behaves the same way — one press, one refetch. It sits
            after the status tabs rather than among them because it crosses them:
            you can include archived rows while filtered to Interviewing. */}
        <div className="tabs" role="group" aria-label="Archive">
          <FilterTab active={archived} count={null} onClick={() => change(() => setArchived(!archived))}>
            Include archived
          </FilterTab>
        </div>
      </div>

      {/* The undo. Inline above the list rather than a floating toast: it has no
          timeout, so nothing is lost by looking away, and it does not cover a row.
          The archive is reversible on the server for as long as the row exists —
          this banner is the convenient path back, not the only one. */}
      {undone && (
        <div className="undo" role="status">
          <span>
            Archived <strong>{undone.title}</strong>.
          </span>
          <button type="button" className="btn btn-quiet" onClick={() => void undo(undone.id)}>
            <RotateCcw size={14} aria-hidden />
            Undo
          </button>
          <button type="button" className="btn btn-quiet" onClick={() => setUndone(null)} aria-label="Dismiss">
            <X size={14} aria-hidden />
          </button>
        </div>
      )}

      {state.tag === 'error' && <Failure error={state.error} what="load your applications" />}

      {state.tag === 'loading' && (
        <p className="quiet" aria-live="polite">
          Loading…
        </p>
      )}

      {state.tag === 'ready' && state.page.items.length === 0 && (
        /* Phase 8 added the third case, and it is the one the phase doc singles
           out: "no applications" and "no ACTIVE applications" are different
           sentences, and only one of them was written. An empty list with
           everything archived used to read as an empty database — which is
           alarming, wrong, and one click from being fixed. */
        <div className="state">
          <h2>
            {debounced || status
              ? 'Nothing matches that'
              : archived
                ? 'Nothing recorded yet'
                : 'Nothing active'}
          </h2>
          <p>
            {debounced || status
              ? 'Clear the filters to see everything you have recorded.'
              : archived
                ? 'The first job you add shows up here. Upload an ad, or add one by hand.'
                : 'Everything you have recorded is archived. Include archived to see it, or add a new one.'}
          </p>
        </div>
      )}

      {state.tag === 'ready' && state.page.items.length > 0 && (
        <>
          <table className="table">
            <caption className="sr-only">
              Applications, sorted by {sort.field} {sort.dir === 'Asc' ? 'ascending' : 'descending'}
            </caption>
            <thead>
              <tr>
                {COLUMNS.map((c) => (
                  <th
                    key={c.label}
                    scope="col"
                    className={c.className}
                    aria-sort={
                      c.field && sort.field === c.field
                        ? sort.dir === 'Asc'
                          ? 'ascending'
                          : 'descending'
                        : undefined
                    }
                  >
                    {c.field ? (
                      <button type="button" className="th-sort" onClick={() => toggleSort(c.field!)}>
                        {c.label}
                        {sort.field === c.field &&
                          (sort.dir === 'Asc' ? (
                            <ArrowUp size={13} aria-hidden />
                          ) : (
                            <ArrowDown size={13} aria-hidden />
                          ))}
                      </button>
                    ) : (
                      c.label
                    )}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {state.page.items.map((a) => (
                <Row
                  key={a.id}
                  a={a}
                  landed={a.id === landed}
                  onLanded={() => setLanded(null)}
                  onArchive={() => void archive(a)}
                  onRestore={() => void undo(a.id)}
                />
              ))}
            </tbody>
          </table>

          <div className="pager">
            <p className="quiet">
              <span className="num">{(state.page.page - 1) * state.page.pageSize + 1}</span>–
              <span className="num">
                {(state.page.page - 1) * state.page.pageSize + state.page.items.length}
              </span>{' '}
              of <span className="num">{state.page.totalCount}</span>
            </p>
            {state.page.totalPages > 1 && (
              <div className="pager-controls">
                <button
                  type="button"
                  className="btn"
                  disabled={state.page.page <= 1}
                  onClick={() => {
                    setState({ tag: 'loading' });
                    setPage((p) => p - 1);
                  }}
                >
                  Previous
                </button>
                <span className="quiet">
                  Page <span className="num">{state.page.page}</span> of{' '}
                  <span className="num">{state.page.totalPages}</span>
                </span>
                <button
                  type="button"
                  className="btn"
                  disabled={state.page.page >= state.page.totalPages}
                  onClick={() => {
                    setState({ tag: 'loading' });
                    setPage((p) => p + 1);
                  }}
                >
                  Next
                </button>
              </div>
            )}
          </div>
        </>
      )}
    </Screen>
  );
}

function FilterTab({
  active,
  count,
  onClick,
  children,
}: {
  active: boolean;
  count: number | null;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button type="button" className="tab" aria-pressed={active} onClick={onClick}>
      {children}
      {count !== null && <span className="tab-count num">{count}</span>}
    </button>
  );
}

function Row({
  a,
  landed,
  onLanded,
  onArchive,
  onRestore,
}: {
  a: ApplicationListItem;
  landed: boolean;
  onLanded: () => void;
  onArchive: () => void;
  onRestore: () => void;
}) {
  const navigate = useNavigate();

  /* The marker swipe fades after a beat. This is one of the two functional uses
   * of amber the brief allows — the tile that just landed — and it is the only
   * authored motion on this screen. */
  useEffect(() => {
    if (!landed) return;
    const t = setTimeout(onLanded, 2400);
    return () => clearTimeout(t);
  }, [landed, onLanded]);

  /* The link in the Role cell is the real affordance: it is what keyboard and
   * screen-reader users get, and what a middle-click opens in a tab. The row
   * click is a mouse convenience layered on top, and it stands aside whenever
   * the click landed on something else interactive or on a text selection. */
  function onRowClick(e: React.MouseEvent<HTMLTableRowElement>) {
    if (e.defaultPrevented || e.metaKey || e.ctrlKey || e.button !== 0) return;
    if ((e.target as HTMLElement).closest('a, button, input, select')) return;
    if (window.getSelection()?.toString()) return;
    navigate(`/applications/${a.id}`);
  }

  return (
    <tr
      className="table-row"
      data-landed={landed || undefined}
      data-archived={a.isArchived || undefined}
      onClick={onRowClick}
    >
      <td className="cell-company">{a.company}</td>
      <td className="cell-role">
        <Link to={`/applications/${a.id}`}>{a.title}</Link>
        {a.location && <span className="cell-sub">{a.location}</span>}
      </td>
      <td className="col-skills">
        {a.skills.length === 0 ? (
          <span className="quiet">—</span>
        ) : (
          <span className="skill-run">
            {a.skills.slice(0, 4).join(' · ')}
            {a.skills.length > 4 && <span className="quiet"> +{a.skills.length - 4}</span>}
          </span>
        )}
      </td>
      <td className="col-match">
        {/* Phase 9, gap 1. `match` is null for every application nobody has run
            the check on, which is most of them — so the absence is rendered as a
            sentence rather than left to fall through as an empty cell. "not
            checked" says the check has not happened; an empty cell would read as
            "checked, and it found nothing". */}
        {a.match ? (
          `${a.match.matched}/${a.match.total}`
        ) : (
          <span className="quiet">not checked</span>
        )}
      </td>
      <td>
        <StatusChip status={a.status} />
      </td>
      <td className="col-date">
        {/* DateOnly arrives as "2026-08-29". formatDateOnly does string surgery
            rather than parsing — see lib/format.ts for why. */}
        <time dateTime={a.dateApplied}>{formatDateOnly(a.dateApplied)}</time>
      </td>
      <td className="col-action">
        {/* NOT .btn-danger, deliberately. PRODUCT.md reserves the alert red for
            genuine failures and for destruction, and an archive is neither — it
            is reversible, and it is the tidy-up a user does on purpose. Dressing
            it in red would make the safest action on the screen look like the
            most dangerous one.

            The label is on the button rather than only in a tooltip, because an
            icon-only control in a table is unlabelled for everyone using a
            screen reader and ambiguous for everyone else. */}
        <button
          type="button"
          className="btn btn-quiet"
          onClick={a.isArchived ? onRestore : onArchive}
          aria-label={`${a.isArchived ? 'Restore' : 'Archive'} ${a.title} at ${a.company}`}
        >
          {a.isArchived ? <RotateCcw size={14} aria-hidden /> : <Archive size={14} aria-hidden />}
          {a.isArchived ? 'Restore' : 'Archive'}
        </button>
      </td>
    </tr>
  );
}

/* Inline, not a modal. Adding a job is neither an interruption nor something
 * that needs protected focus, and keeping the list visible behind it is the
 * point — you are usually copying from an ad in the next tab. */
function AddForm({
  onCreated,
  onCancel,
}: {
  onCreated: (created: { id: string }) => void;
  onCancel: () => void;
}) {
  const [company, setCompany] = useState('');
  const [title, setTitle] = useState('');
  const [location, setLocation] = useState('');
  const [sourceUrl, setSourceUrl] = useState('');
  const [description, setDescription] = useState('');
  const [notes, setNotes] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const first = useRef<HTMLInputElement>(null);

  useEffect(() => first.current?.focus(), []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const created = await createApplication({
        company: company.trim(),
        title: title.trim(),
        location: location.trim() || null,
        sourceUrl: sourceUrl.trim() || null,
        description: description.trim() || null,
        notes: notes.trim() || null,
      });
      onCreated(created);
    } catch (e) {
      setError(asApiError(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="panel add-form" onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onCancel()}>
      <h2>Add an application</h2>
      <p className="quiet">
        Company and role are all that is required. The company is matched by name — an
        existing one is reused rather than duplicated. Paste the ad in as well and the
        analyser can read it; or{' '}
        <Link to="/upload">upload the ad as a file</Link> and let the parser fill this in
        for you.
      </p>

      <div className="add-grid">
        <label className="field">
          <span>Company</span>
          <input ref={first} value={company} onChange={(e) => setCompany(e.target.value)} required />
        </label>
        <label className="field">
          <span>Role</span>
          <input value={title} onChange={(e) => setTitle(e.target.value)} required />
        </label>
        <label className="field">
          <span>Location</span>
          <input
            value={location}
            onChange={(e) => setLocation(e.target.value)}
            placeholder="Richmond, Melbourne VIC"
          />
        </label>
        <label className="field">
          <span>Link to the ad</span>
          <input type="url" value={sourceUrl} onChange={(e) => setSourceUrl(e.target.value)} />
        </label>
        {/* The ad, and it is deliberately the largest control on the form.
            Phase 6.3 shipped this form with Notes and no description, which put
            the only textarea labelled for prose on job_applications.Notes — a
            field nothing reads. Someone pasting an advertisement found the box
            that looked right and got no skills, because the analyser reads
            job_postings.Description. Two fields, two owners, one of them
            invisible. See docs/phases/phase-6.6-the-ad-goes-somewhere.md. */}
        <label className="field field-wide">
          <span>The ad</span>
          <textarea
            rows={8}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Paste the advertisement here — the whole thing, boilerplate and all."
          />
          <span className="quiet field-hint">
            This is what the analyser reads to pull out skills and requirements. Leave it
            empty and &ldquo;Analyse the ad&rdquo; has nothing to work from. You can add it
            later from the job&rsquo;s own page.
          </span>
        </label>
        <label className="field field-wide">
          <span>Your notes</span>
          <textarea
            rows={2}
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            placeholder="Who referred you, what to ask, why you want it."
          />
          <span className="quiet field-hint">
            Yours, not the employer&rsquo;s. Nothing reads these — they are for you.
          </span>
        </label>
      </div>

      {error && <Failure error={error} what="save this application" />}

      <div className="add-actions">
        <button type="submit" className="btn btn-primary" disabled={busy || !company.trim() || !title.trim()}>
          {busy ? 'Saving…' : 'Save application'}
        </button>
        <button type="button" className="btn" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  );
}
