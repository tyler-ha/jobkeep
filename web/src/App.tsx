import { useEffect, useState } from 'react';
import { NavLink, Navigate, Route, Routes } from 'react-router-dom';
import {
  BarChart3,
  Briefcase,
  Columns3,
  FileText,
  LayoutDashboard,
  Menu,
  Target,
  Upload as UploadIcon,
  X,
} from 'lucide-react';

import Applications from './routes/Applications';
import MatchCheck from './routes/MatchCheck';
import Insights from './routes/Insights';
import JobPost from './routes/JobPost';
import Pipeline from './routes/Pipeline';
import Resumes from './routes/Resumes';
import SignIn from './routes/SignIn';
import Today from './routes/Today';
import Upload from './routes/Upload';
import { onUnauthenticated, signOut, whoAmI, type Account } from './lib/api';

/* Seven destinations, not eight: "Job post" is the detail view of an
 * application and is reached by opening a row, so it has a route but no nav
 * entry. */
const NAV = [
  { to: '/today', label: 'Today', icon: LayoutDashboard },
  { to: '/applications', label: 'Applications', icon: Briefcase },
  { to: '/pipeline', label: 'Pipeline', icon: Columns3 },
  { to: '/resumes', label: 'Résumés', icon: FileText },
  { to: '/upload', label: 'Upload', icon: UploadIcon },
  { to: '/match-check', label: 'Match check', icon: Target },
  { to: '/insights', label: 'Insights', icon: BarChart3 },
];

export default function App() {
  const [navOpen, setNavOpen] = useState(false);

  /* PHASE 11.1c — the route guard, and it is one piece of state rather than a
   * <ProtectedRoute> per route.
   *
   * THREE values, not two, and the third is the one that matters: `undefined` is
   * "not asked yet". There is no token in the client to inspect — the cookie is
   * HttpOnly by design — so the only honest way to know whether a session is
   * live is to ask the server, and that is a round trip. Rendering the shell
   * during it would flash the app at someone who is about to be shown a sign-in
   * form; rendering the form would flash a sign-in at someone already signed in,
   * which is worse. So the first paint waits.
   */
  const [account, setAccount] = useState<Account | null | undefined>(undefined);

  useEffect(() => {
    /* Registered before the first call, so a 401 from the check itself takes the
     * same path as a 401 from anywhere else. Both land on setAccount(null),
     * which is why this is safe to fire more than once. */
    onUnauthenticated(() => setAccount(null));
    whoAmI().then(setAccount, () => setAccount(null));
  }, []);

  /* Deliberately blank rather than a spinner. This is one request against
   * localhost, and a spinner that appears for 20ms is a flicker, not feedback. */
  if (account === undefined) return null;

  /* Not a route. The address is left untouched, so signing in lands the user on
   * whatever they opened — see the header comment in routes/SignIn.tsx. */
  if (account === null) return <SignIn onSignedIn={setAccount} />;

  return (
    <div className="shell">
      <nav className="nav" aria-label="Main" data-open={navOpen || undefined}>
        <div className="nav-bar">
          <NavLink to="/today" className="wordmark" onClick={() => setNavOpen(false)}>
            JobKeep
          </NavLink>
          {/* Hidden by CSS above 900px — a real toggle button rather than a
              CSS-only checkbox hack, so aria-expanded stays accurate and the
              icon swap needs no extra markup. */}
          <button
            type="button"
            className="nav-toggle"
            aria-expanded={navOpen}
            aria-controls="nav-list"
            aria-label={navOpen ? 'Close menu' : 'Open menu'}
            onClick={() => setNavOpen((open) => !open)}
          >
            {navOpen ? <X size={20} aria-hidden /> : <Menu size={20} aria-hidden />}
          </button>
        </div>

        <ul className="nav-list" id="nav-list">
          {NAV.map(({ to, label, icon: Icon }) => (
            <li key={to}>
              {/* NavLink sets aria-current="page" itself, so the active style
                  keys off the accessibility state rather than a second,
                  separately-maintained class. The icon has no label of its
                  own — it's decorative alongside the text, not a second way
                  to identify the destination. Closing the mobile menu here,
                  in the click that causes the navigation, is the state
                  update the change actually belongs to — watching pathname
                  in an effect would fire the same close a render later, and
                  do it on desktop too, where there is no menu to close. */}
              <NavLink to={to} className="nav-link" onClick={() => setNavOpen(false)}>
                <Icon size={16} aria-hidden />
                <span className="nav-label">{label}</span>
              </NavLink>
            </li>
          ))}
        </ul>

        <div className="nav-foot">
          <p>Local build · API on :5080</p>
          <p>{account.email}</p>
          {/* Clears the state whatever the server said. A sign-out that failed
              because the API is down should still sign you out of this tab —
              the alternative is a user who cannot leave. */}
          <button
            type="button"
            className="nav-signout"
            onClick={() => void signOut().finally(() => setAccount(null))}
          >
            Sign out
          </button>
        </div>
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
