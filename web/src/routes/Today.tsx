import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { ChevronRight } from 'lucide-react';

import { Failure } from '../components/Failure';
import { Screen } from '../components/Screen';
import { StatusChip } from '../components/StatusChip';
import {
  APPLICATION_STATUSES,
  ApiError,
  asApiError,
  getFunnel,
  listApplications,
  listImports,
  type ApplicationFunnel,
  type ApplicationListItem,
  type ImportSummary,
} from '../lib/api';
import { formatDateOnly, formatInstant, humanise, isoDaysAgo } from '../lib/format';

/* Today — the landing screen, and the one that has to be most honest about what
 * it can actually know.
 *
 * There are no reminders in this product. Follow-ups, nudges and "chase this on
 * Thursday" are a backlog item, not a shipped feature, so this screen does not
 * pretend to have them. What it CAN say is built from three reads the API
 * already answers, and every item on it is something the user can act on now:
 *
 *  1. Imports awaiting review — a real queue, with a real gate at the end of it.
 *  2. Applications that have sat in Applied a long while — computed from
 *     dateApplied, which the list already returns. It is not a reminder; it is
 *     an observation, and the copy says so.
 *  3. What is in flight, and what was added recently.
 *
 * When reminders land this screen gets a fourth block and the second one
 * probably becomes part of it. Until then it says only what it knows.
 */

/* Three weeks. Long enough that a Melbourne employer who was going to answer
 * usually has, short enough to still be worth a nudge — and stated on screen
 * rather than hidden, because it is a judgement call, not a fact. */
const QUIET_DAYS = 21;

type State =
  | { tag: 'loading' }
  | { tag: 'error'; error: ApiError }
  | {
      tag: 'ready';
      recent: ApplicationListItem[];
      funnel: ApplicationFunnel;
      /* The import queue failing must not take the screen down with it: it is
       * one of three blocks, and the other two are still worth showing. */
      queue: ImportSummary[] | null;
    };

export default function Today() {
  const [state, setState] = useState<State>({ tag: 'loading' });

  useEffect(() => {
    let live = true;

    Promise.all([
      listApplications('?pageSize=50&sort=DateApplied&direction=Desc'),
      getFunnel(),
      listImports('AwaitingReview').catch(() => null),
    ])
      .then(([page, funnel, queue]) => {
        if (live) setState({ tag: 'ready', recent: page.items, funnel, queue });
      })
      .catch((e) => live && setState({ tag: 'error', error: asApiError(e) }));

    return () => {
      live = false;
    };
  }, []);

  if (state.tag === 'error')
    return (
      <Screen title="Today" lede="What needs attention, and what moved since you last looked.">
        <Failure error={state.error} what="load your dashboard" />
      </Screen>
    );

  if (state.tag === 'loading')
    return (
      <Screen title="Today" lede="What needs attention, and what moved since you last looked.">
        <p className="quiet" aria-live="polite">
          Loading…
        </p>
      </Screen>
    );

  const { recent, funnel, queue } = state;

  /* An ISO date comparison, not a parsed one — "2026-08-10" sorts
   * lexicographically, so this is exact and cannot slip a day across a timezone
   * boundary the way comparing two Dates can. */
  const cutoff = isoDaysAgo(QUIET_DAYS);
  const quiet = recent.filter((a) => a.status === 'Applied' && a.dateApplied <= cutoff);
  const inFlight = recent.filter((a) => a.status === 'Interviewing' || a.status === 'Offer');
  const waiting = queue?.length ?? 0;
  const attention = waiting + quiet.length;

  if (funnel.total === 0 && waiting === 0)
    return (
      <Screen title="Today" lede="What needs attention, and what moved since you last looked.">
        <div className="state">
          <h2>Nothing here yet</h2>
          <p>
            JobKeep tracks what you have applied for, what each ad asked for, and how your CV
            reads against it. Start by <Link to="/upload">uploading an ad or a CV</Link>, or{' '}
            <Link to="/applications">add an application by hand</Link>.
          </p>
        </div>
      </Screen>
    );

  return (
    <Screen title="Today" lede="What needs attention, and what moved since you last looked.">
      {/* The held moment. One number, and it is a number you can do something
          about — not a total, which is a number you can only look at. */}
      <p className="today-figure">
        {attention > 0 ? (
          <>
            <span className="marked num">{attention}</span>
            <span className="today-figure-label">
              {attention === 1 ? 'thing' : 'things'} worth a look
            </span>
          </>
        ) : (
          <>
            <span className="marked">Nothing waiting</span>
            <span className="today-figure-label">
              <span className="num">{funnel.total}</span> applications tracked
            </span>
          </>
        )}
      </p>

      {/* The status strip. The same five counts Insights charts, but here they
          are a way in: each one is the Applications list, already filtered. */}
      <nav className="strip" aria-label="Applications by status">
        {APPLICATION_STATUSES.map((s) => {
          const count = funnel.stages.find((x) => x.status === s)?.count ?? 0;
          return (
            <Link key={s} to={`/applications?status=${s}`} className="strip-cell" data-empty={count === 0 || undefined}>
              <span className="strip-count num">{count}</span>
              <span className="strip-label">{s}</span>
            </Link>
          );
        })}
      </nav>

      <div className="today-grid">
        {waiting > 0 && (
          <section className="panel">
            <div className="panel-head">
              <h2>Waiting on you</h2>
              <span className="quiet num">{waiting}</span>
            </div>
            <p className="quiet">
              Uploaded and read, but nothing has been created from them yet. Confirming is
              what turns a draft into rows.
            </p>
            <ul className="brief">
              {queue!.map((d) => (
                <li key={d.id}>
                  <Link to={`/upload/${d.id}`} className="brief-row">
                    <span className="queue-kind" data-kind={d.kind}>
                      {d.kind === 'Resume' ? 'CV' : 'Job ad'}
                    </span>
                    <span className="brief-name">{d.fileName}</span>
                    <span className="quiet brief-when">{formatInstant(d.createdAtUtc)}</span>
                    <ChevronRight size={15} aria-hidden className="queue-go" />
                  </Link>
                </li>
              ))}
            </ul>
          </section>
        )}

        {quiet.length > 0 && (
          <section className="panel">
            <div className="panel-head">
              <h2>Quiet for a while</h2>
              <span className="quiet num">{quiet.length}</span>
            </div>
            {/* Said plainly: this is an observation about a date, not a
                reminder the product set and not advice about what to do. */}
            <p className="quiet">
              Applied more than <span className="num">{QUIET_DAYS}</span> days ago and still
              sitting in Applied. JobKeep does not chase anything for you — this is only the
              date, noticed.
            </p>
            <ul className="brief">
              {quiet.slice(0, 6).map((a) => (
                <li key={a.id}>
                  <Link to={`/applications/${a.id}`} className="brief-row">
                    <span className="brief-name">{a.title}</span>
                    <span className="quiet">{a.company}</span>
                    <time className="quiet brief-when" dateTime={a.dateApplied}>
                      {formatDateOnly(a.dateApplied)}
                    </time>
                    <ChevronRight size={15} aria-hidden className="queue-go" />
                  </Link>
                </li>
              ))}
            </ul>
            {quiet.length > 6 && (
              <p className="panel-foot">
                <Link to="/applications?status=Applied">
                  See all {quiet.length} in the list
                </Link>
              </p>
            )}
          </section>
        )}

        <section className="panel">
          <div className="panel-head">
            <h2>In flight</h2>
            <span className="quiet num">{inFlight.length}</span>
          </div>
          {inFlight.length === 0 ? (
            <p className="quiet">
              Nothing at interview or offer stage. Moving a card on the{' '}
              <Link to="/pipeline">board</Link> is how an application gets here.
            </p>
          ) : (
            <ul className="brief">
              {inFlight.map((a) => (
                <li key={a.id}>
                  <Link to={`/applications/${a.id}`} className="brief-row">
                    <span className="brief-name">{a.title}</span>
                    <span className="quiet">{a.company}</span>
                    <StatusChip status={a.status} />
                    <ChevronRight size={15} aria-hidden className="queue-go" />
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="panel">
          <div className="panel-head">
            <h2>Recently added</h2>
            <Link className="quiet-link" to="/applications">
              All applications <ChevronRight size={13} aria-hidden />
            </Link>
          </div>
          <ul className="brief">
            {recent.slice(0, 6).map((a) => (
              <li key={a.id}>
                <Link to={`/applications/${a.id}`} className="brief-row">
                  <span className="brief-name">{a.title}</span>
                  <span className="quiet">{a.company}</span>
                  <time className="quiet brief-when" dateTime={a.dateApplied}>
                    {formatDateOnly(a.dateApplied)}
                  </time>
                  <ChevronRight size={15} aria-hidden className="queue-go" />
                </Link>
              </li>
            ))}
          </ul>
          {recent[0] && (
            <p className="panel-foot">
              Newest is {humanise(recent[0].status)}, applied{' '}
              <time dateTime={recent[0].dateApplied}>{formatDateOnly(recent[0].dateApplied)}</time>.
            </p>
          )}
        </section>
      </div>
    </Screen>
  );
}
