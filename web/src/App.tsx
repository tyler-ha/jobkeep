import { NavLink, Navigate, Route, Routes } from 'react-router-dom';

import Applications from './routes/Applications';
import MatchCheck from './routes/MatchCheck';
import Insights from './routes/Insights';
import JobPost from './routes/JobPost';
import Pipeline from './routes/Pipeline';
import Resumes from './routes/Resumes';
import Today from './routes/Today';
import Upload from './routes/Upload';

/* Seven destinations, not eight: "Job post" is the detail view of an
 * application and is reached by opening a row, so it has a route but no nav
 * entry. */
const NAV = [
  { to: '/today', label: 'Today' },
  { to: '/applications', label: 'Applications' },
  { to: '/pipeline', label: 'Pipeline' },
  { to: '/resumes', label: 'Résumés' },
  { to: '/upload', label: 'Upload' },
  { to: '/match-check', label: 'Match check' },
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

        <p className="nav-foot">Local build · API on :5080</p>
      </nav>

      <main className="main">
        <Routes>
          <Route path="/" element={<Navigate to="/today" replace />} />
          <Route path="/today" element={<Today />} />
          <Route path="/applications" element={<Applications />} />
          <Route path="/applications/:id" element={<JobPost />} />
          <Route path="/pipeline" element={<Pipeline />} />
          <Route path="/resumes" element={<Resumes />} />
          {/* Same screen, master-detail. The shelf selects the first résumé
              when the bare path is opened, so this is the address the app
              actually links to — and it makes one CV shareable as a link. */}
          <Route path="/resumes/:id" element={<Resumes />} />
          <Route path="/upload" element={<Upload />} />
          {/* The queue and the review are one screen: /upload is the queue plus
              the uploader, /upload/:id is the draft beside the document. The
              wire is unmoved — the API is still /imports. See lib/api.ts. */}
          <Route path="/upload/:id" element={<Upload />} />
          <Route path="/match-check" element={<MatchCheck />} />
          {/* The board needs a job. The bare /match-check above is the picker;
              this is what every link on the app actually points at. */}
          <Route path="/applications/:id/match-check" element={<MatchCheck />} />
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
