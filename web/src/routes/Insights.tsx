import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';

import { Failure } from '../components/Failure';
import { Screen } from '../components/Screen';
import {
  APPLICATION_STATUSES,
  ApiError,
  asApiError,
  getCompanies,
  getFunnel,
  getSkillDemand,
  type ApplicationFunnel,
  type CompanyRollupItem,
  type SkillDemandItem,
} from '../lib/api';
import { barScale, shares } from '../lib/chart';

/* Insights — the three read-only aggregates the Analytics module already
 * answers, and nothing else.
 *
 * Every number here is one GROUP BY that Postgres computed. Nothing on this
 * screen counts anything in JavaScript, which is the whole point of Phase 2.4
 * and the thing worth being able to say out loud about it.
 *
 * No charting library. Three charts do not earn a dependency, and each of these
 * is a list with a width on it — the value is always present as text, so the
 * chart survives colour being removed, a screen reader, and a printout. The
 * arithmetic that could silently be wrong lives in lib/chart.ts with tests.
 *
 * Three panels, three different shapes on purpose. Three bar charts stacked
 * down a page look like one chart repeated and stop being read.
 */

type State =
  | { tag: 'loading' }
  | { tag: 'error'; error: ApiError }
  | {
      tag: 'ready';
      funnel: ApplicationFunnel;
      skills: SkillDemandItem[];
      companies: CompanyRollupItem[];
    };

/* The demand chart asks a comparative question, so it needs enough rows to
 * compare and few enough to read down in one go. */
const TOP_SKILLS = 12;
const TOP_COMPANIES = 8;

export default function Insights() {
  const [state, setState] = useState<State>({ tag: 'loading' });

  useEffect(() => {
    let live = true;
    /* Three independent aggregates, fetched together. Promise.all rather than
     * three separate states: the screen has nothing useful to show with one of
     * them, and staggering the paint would make it flicker into place. */
    Promise.all([getFunnel(), getSkillDemand(TOP_SKILLS), getCompanies(TOP_COMPANIES)])
      .then(([funnel, skills, companies]) => {
        if (live) setState({ tag: 'ready', funnel, skills, companies });
      })
      .catch((e) => live && setState({ tag: 'error', error: asApiError(e) }));
    return () => {
      live = false;
    };
  }, []);

  return (
    <Screen
      title="Insights"
      lede="What the market keeps asking for, and where your applications sit."
    >
      {state.tag === 'error' && <Failure error={state.error} what="load your figures" />}
      {state.tag === 'loading' && (
        <p className="quiet" aria-live="polite">
          Loading…
        </p>
      )}

      {state.tag === 'ready' && state.funnel.total === 0 && (
        <div className="state">
          <h2>Nothing to count yet</h2>
          <p>
            These figures are aggregates over the jobs you have recorded, so they arrive
            with the first one. <Link to="/applications">Add an application</Link> or{' '}
            <Link to="/import">import an ad</Link>.
          </p>
        </div>
      )}

      {state.tag === 'ready' && state.funnel.total > 0 && (
        <div className="insights">
          <Funnel funnel={state.funnel} />
          <Demand skills={state.skills} />
          <Companies companies={state.companies} total={state.funnel.total} />
        </div>
      )}
    </Screen>
  );
}

/* ---- Where they sit ------------------------------------------------------ */

/* One stacked bar, not five. The question this answers is proportional — how
 * much of the pile is still live — and five separate bars make the reader do
 * the addition themselves.
 *
 * The segment widths come from `shares`, which rounds so they sum to exactly
 * 100. Naive rounding leaves a 1% gap at the end of the bar that looks like a
 * layout bug and gets debugged as one. */
function Funnel({ funnel }: { funnel: ApplicationFunnel }) {
  const counts = APPLICATION_STATUSES.map(
    (s) => funnel.stages.find((x) => x.status === s)?.count ?? 0,
  );
  const pct = shares(counts);
  const live = counts[0]! + counts[1]! + counts[2]!;

  return (
    <section className="panel insight insight-funnel" aria-labelledby="h-funnel">
      <div className="insight-head">
        <h2 id="h-funnel">Where they sit</h2>
        <p className="quiet">Every application you have recorded, by status.</p>
      </div>

      {/* The one held moment on this screen: the count that answers "am I
          actually in anything". Amber is the ground behind it, never the ink —
          --pop is 1.58 on white and can never carry text. */}
      <p className="insight-figure">
        <span className="marked num">{live}</span>
        <span className="insight-figure-label">
          still live, of <span className="num">{funnel.total}</span>
        </span>
      </p>

      {/* aria-hidden because the list below it says the same thing in words,
          and a bar made of divs announces as nothing useful. This is the
          decoration; the list is the data. */}
      <div className="bar-stack" aria-hidden>
        {APPLICATION_STATUSES.map((s, i) =>
          pct[i]! > 0 ? (
            <span key={s} className="bar-seg" data-status={s} style={{ width: `${pct[i]}%` }} />
          ) : null,
        )}
      </div>

      <ul className="legend">
        {APPLICATION_STATUSES.map((s, i) => (
          <li key={s} className="legend-row" data-empty={counts[i] === 0 || undefined}>
            <span className="legend-swatch" data-status={s} aria-hidden />
            <span className="legend-name">{s}</span>
            <span className="legend-count num">{counts[i]}</span>
            <span className="legend-pct num quiet">{pct[i]}%</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

/* ---- What the market asks for -------------------------------------------- */

function Demand({ skills }: { skills: SkillDemandItem[] }) {
  const widths = barScale(skills.map((s) => s.postingCount));
  const top = skills[0]?.postingCount ?? 0;

  return (
    <section className="panel insight insight-demand" aria-labelledby="h-demand">
      <div className="insight-head">
        <h2 id="h-demand">What the ads ask for</h2>
        <p className="quiet">
          Skills across every job you have recorded, most-named first. Counted over
          postings, so a skill named twice in one ad counts once.
        </p>
      </div>

      {skills.length === 0 ? (
        <p className="quiet">
          No skills recorded yet. They arrive when an imported ad is confirmed, or when
          you add them to a job by hand.
        </p>
      ) : (
        <ol className="demand">
          {skills.map((s, i) => (
            <li key={s.name} className="demand-row">
              <span className="demand-name">
                {s.name}
                {s.category && <span className="demand-cat quiet">{s.category}</span>}
              </span>
              {/* Real geometry, not a background gradient, so the bar and the
                  number beside it cannot disagree. The width is set once and
                  never transitioned — an animated width lays out every frame on
                  the main thread, which is the finding the design detector
                  already caught twice on this project. */}
              <span className="demand-track" aria-hidden>
                <span
                  className="demand-fill"
                  data-top={i === 0 || undefined}
                  style={{ width: `${widths[i]}%` }}
                />
              </span>
              <span className="demand-count num">
                {s.postingCount}
                <span className="sr-only"> {s.postingCount === 1 ? 'ad' : 'ads'}</span>
              </span>
            </li>
          ))}
        </ol>
      )}

      {/* The recorded gap, said out loud rather than papered over. Merging the
          two rows client-side would hide a defect the backend has a test
          pinning, and the fix is a migration, not a chart. */}
      {top > 0 && (
        <p className="insight-foot quiet">
          Skills are matched exactly, so <code>C#</code> and <code>c#</code> count as two.
          A known gap — the fix is a case-insensitive key on the skills table.
        </p>
      )}
    </section>
  );
}

/* ---- Who you have applied to --------------------------------------------- */

/* A ranked table, not a third bar chart. The question is "who am I actually
 * spending my applications on", which is read as a list of names — and by this
 * point in the page a third row of bars would be scrolled past. */
function Companies({ companies, total }: { companies: CompanyRollupItem[]; total: number }) {
  return (
    <section className="panel insight insight-companies" aria-labelledby="h-companies">
      <div className="insight-head">
        <h2 id="h-companies">Where they went</h2>
        <p className="quiet">Applications per company, most first.</p>
      </div>

      {companies.length === 0 ? (
        <p className="quiet">No companies yet.</p>
      ) : (
        <table className="rank">
          <caption className="sr-only">Companies by number of applications</caption>
          <thead>
            <tr>
              <th scope="col" className="sr-only">
                Rank
              </th>
              <th scope="col">Company</th>
              <th scope="col" className="rank-num">
                Applications
              </th>
              <th scope="col" className="rank-num">
                Share
              </th>
            </tr>
          </thead>
          <tbody>
            {companies.map((c, i) => (
              <tr key={c.name}>
                <td className="rank-i num" aria-hidden>
                  {i + 1}
                </td>
                <td>
                  {/* The filter this row describes, one click away. The company
                      filter is ILIKE server-side, so an exact name matches. */}
                  <Link to={`/applications?company=${encodeURIComponent(c.name)}`}>{c.name}</Link>
                </td>
                <td className="rank-num num">{c.applicationCount}</td>
                <td className="rank-num num quiet">
                  {Math.round((c.applicationCount / total) * 100)}%
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
