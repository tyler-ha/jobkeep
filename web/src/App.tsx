import { NavLink, Navigate, Route, Routes } from 'react-router-dom';

import Applications from './routes/Applications';
import AtsCheck from './routes/AtsCheck';
import Import from './routes/Import';
import Insights from './routes/Insights';
import JobPost from './routes/JobPost';
import Pipeline from './routes/Pipeline';
import Resumes from './routes/Resumes';
import Today from './routes/Today';

/* Seven destinations, not eight: "Job post" is the detail view of an
 * application and is reached by opening a row, so it has a route but no nav
 * entry. */
const NAV = [
  { to: '/today', label: 'Today' },
  { to: '/applications', label: 'Applications' },
  { to: '/pipeline', label: 'Pipeline' },
  { to: '/resumes', label: 'Résumés' },
  { to: '/import', label: 'Import' },
  { to: '/ats-check', label: 'ATS check' },
  { to: '/insights', label: 'Insights' },
];

export default function App() {
  return (
    <div className="shell">
      <nav className="nav" aria-label="Main">
        <NavLink to="/today" className="wordmark">
          JobKeep
        </NavLink>

        <ul className="nav-list">
          {NAV.map(({ to, label }) => (
            <li key={to}>
              {/* NavLink sets aria-current="page" itself, so the active style
                  keys off the accessibility state rather than a second,
                  separately-maintained class. */}
              <NavLink to={to} className="nav-link">
                <span className="nav-label">{label}</span>
              </NavLink>
            </li>
          ))}
        </ul>

        <p className="nav-foot">Phase 6.2 — scaffold</p>
      </nav>

      <main className="main">
        <Routes>
          <Route path="/" element={<Navigate to="/today" replace />} />
          <Route path="/today" element={<Today />} />
          <Route path="/applications" element={<Applications />} />
          <Route path="/applications/:id" element={<JobPost />} />
          <Route path="/pipeline" element={<Pipeline />} />
          <Route path="/resumes" element={<Resumes />} />
          <Route path="/import" element={<Import />} />
          <Route path="/ats-check" element={<AtsCheck />} />
          <Route path="/insights" element={<Insights />} />
          <Route path="*" element={<NotFound />} />
        </Routes>
      </main>
    </div>
  );
}

function NotFound() {
  return (
    <div className="state">
      <h2>No screen at this address</h2>
      <p>
        The link may be from an older version, or mistyped. Everything JobKeep
        can show is in the navigation.
      </p>
    </div>
  );
}
