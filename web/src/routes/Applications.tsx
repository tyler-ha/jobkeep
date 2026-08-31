import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { ArrowDown, ArrowUp, Plus, Search, X } from 'lucide-react';

import { Failure } from '../components/Failure';
import { Screen } from '../components/Screen';
import { StatusChip } from '../components/StatusChip';
import {
  APPLICATION_STATUSES,
  ApiError,
  asApiError,
  createApplication,
  getFunnel,
  listApplications,
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
 * rather than a feed of cards: five columns you can scan down, sorting on the
 * headers instead of a toolbar control, and no row taller than it needs to be.
 *
 * Two things the approved artboard shows that the frozen API cannot serve, both
 * recorded as deviations in docs/phases/phase-6-frontend.md:
 *
 *  - The "CV match" column (0/9, 5/7, "not checked"). ApplicationListItem has
 *    no ATS data and the GraphQL surface exposes flat root fields, so the only
 *    ways to fill it are a per-row request — an N+1 on a list — or a backend
 *    change. It is dropped here and logged as Phase 7 work. The skills the ad
 *    names take the column instead, which the list endpoint does return.
 *  - A single "Search company or role" box. The API's company and title filters
 *    are ANDed, so one box across both is not one request. The box carries a
 *    field selector instead, which is one request and says what it is doing.
 */

const PAGE_SIZE = 25;

type Sort = { field: ApplicationSort; dir: 'Asc' | 'Desc' };

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
  { label: 'Status', field: 'Status' },
  { label: 'Applied', field: 'DateApplied', className: 'col-date' },
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
  const [status, setStatus] = useState<ApplicationStatus | null>(
    APPLICATION_STATUSES.includes(linked as ApplicationStatus)
      ? (linked as ApplicationStatus)
      : null,
  );
  const [sort, setSort] = useState<Sort>({ field: 'DateApplied', dir: 'Desc' });
  const [page, setPage] = useState(1);

  const [adding, setAdding] = useState(false);
  const [landed, setLanded] = useState<string | null>(null);

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
    if (status) q.set('status', status);

    listApplications(`?${q}`)
      .then((p) => live && setState({ tag: 'ready', page: p }))
      .catch((e) => live && setState({ tag: 'error', error: asApiError(e) }));

    return () => {
      live = false;
    };
  }, [debounced, field, status, sort, page]);

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
        </div>
      </div>

      {state.tag === 'error' && <Failure error={state.error} what="load your applications" />}

      {state.tag === 'loading' && (
        <p className="quiet" aria-live="polite">
          Loading…
        </p>
      )}

      {state.tag === 'ready' && state.page.items.length === 0 && (
        <div className="state">
          <h2>{debounced || status ? 'Nothing matches that' : 'Nothing recorded yet'}</h2>
          <p>
            {debounced || status
              ? 'Clear the filters to see everything you have recorded.'
              : 'The first job you add shows up here. Import an ad, or add one by hand.'}
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
                <Row key={a.id} a={a} landed={a.id === landed} onLanded={() => setLanded(null)} />
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
}: {
  a: ApplicationListItem;
  landed: boolean;
  onLanded: () => void;
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
    <tr className="table-row" data-landed={landed || undefined} onClick={onRowClick}>
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
      <td>
        <StatusChip status={a.status} />
      </td>
      <td className="col-date">
        {/* DateOnly arrives as "2026-08-29". formatDateOnly does string surgery
            rather than parsing — see lib/format.ts for why. */}
        <time dateTime={a.dateApplied}>{formatDateOnly(a.dateApplied)}</time>
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
        existing one is reused rather than duplicated.
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
        <label className="field field-wide">
          <span>Notes</span>
          <textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
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
