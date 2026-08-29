import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';

import { Screen } from '../components/Screen';
import { ApiError, listApplications, type ApplicationPage } from '../lib/api';

/* Deliberately plain. Step 6.3 builds the designed Applications screen —
 * filters, sort, paging, the create flow. What this does is prove the wiring:
 * a real cross-origin request against the running API, with the four states a
 * screen has to have before it is honest about anything. */

type State =
  | { tag: 'loading' }
  | { tag: 'error'; error: ApiError }
  | { tag: 'ready'; page: ApplicationPage };

export default function Applications() {
  const [state, setState] = useState<State>({ tag: 'loading' });

  useEffect(() => {
    let live = true;
    listApplications('?pageSize=25')
      .then((page) => live && setState({ tag: 'ready', page }))
      .catch((error) => {
        if (!live) return;
        setState({
          tag: 'error',
          error: error instanceof ApiError ? error : new ApiError(0, String(error)),
        });
      });
    return () => {
      live = false;
    };
  }, []);

  return (
    <Screen
      title="Applications"
      lede="Every job you have recorded, newest first."
    >
      {state.tag === 'loading' && (
        <p className="row-company" aria-live="polite">
          Loading…
        </p>
      )}

      {state.tag === 'error' && (
        <div className="error" role="alert">
          <strong>{state.error.status === 0 ? 'No answer from the API' : `The API returned ${state.error.status}`}</strong>
          <p>
            {state.error.message} Start it with <code>cd src &amp;&amp; dotnet run</code>,
            and check Postgres is up.
          </p>
        </div>
      )}

      {state.tag === 'ready' && state.page.items.length === 0 && (
        <div className="state">
          <h2>Nothing recorded yet</h2>
          <p>
            The first job you add shows up here. Import an ad, or add one by hand.
          </p>
        </div>
      )}

      {state.tag === 'ready' && state.page.items.length > 0 && (
        <>
          <ul className="rows">
            {state.page.items.map((a) => (
              <li key={a.id} className="row">
                <div>
                  <div className="row-title">
                    <Link to={`/applications/${a.id}`}>{a.title}</Link>
                  </div>
                  <div className="row-company">
                    {a.company}
                    {a.location ? ` · ${a.location}` : ''}
                  </div>
                </div>
                <div className="row-company">{a.skills.slice(0, 3).join(', ')}</div>
                <span className="chip" data-status={a.status}>
                  {a.status}
                </span>
                {/* DateOnly arrives as "2026-08-29". Rendered as-is rather than
                    parsed to a Date and localised — that shifts dates near
                    midnight by a day, and this is a calendar day, not an
                    instant. */}
                <time className="row-date" dateTime={a.dateApplied}>
                  {a.dateApplied}
                </time>
              </li>
            ))}
          </ul>
          <p className="row-company" style={{ marginTop: 'var(--s-4)' }}>
            <span className="num">{state.page.items.length}</span> of{' '}
            <span className="num">{state.page.totalCount}</span>
          </p>
        </>
      )}
    </Screen>
  );
}
