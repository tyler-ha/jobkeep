import { useState, type FormEvent } from 'react';

import { ApiError, asApiError, register, signIn, type Account } from '../lib/api';

/* The sign-in screen. Phase 11.1c.
 *
 * NOT A ROUTE, and that is a deliberate deviation from the plan, which said "a
 * login route and a route guard in App.tsx". It is rendered by App INSTEAD of
 * the shell when nobody is signed in, whatever the address. Two things fall out
 * of that, both of them better than the /login version:
 *
 *   * The address survives. Open a bookmarked /applications/{id} with an expired
 *     session, sign in, and you are on that job post — no redirect parameter to
 *     carry, and no code to carry it.
 *   * There is no signed-out URL to get stranded on. A /login route is reachable
 *     while signed in, which is a state that then needs its own rule.
 *
 * It is also less code, which is the tiebreak rather than the argument.
 *
 * IT CAN ALSO CREATE THE ACCOUNT. A sign-in form with no way to make an account
 * is a dead end when the only alternative is Swagger — and registration is one
 * boolean and a different URL, since 11.1b mapped both. See the ponytail note on
 * `register` in lib/api.ts: the route is open, which is right for localhost and
 * is the first thing to close when a host is chosen.
 */
export default function SignIn({ onSignedIn }: { onSignedIn: (account: Account) => void }) {
  const [creating, setCreating] = useState(false);
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      /* Register then sign in, in that order and without asking again. Making
       * someone type the same two fields a second time to enter the thing they
       * just created is a step that exists only because the endpoints are two. */
      if (creating) await register(email, password);
      onSignedIn(await signIn(email, password));
    } catch (thrown) {
      setError(asApiError(thrown));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="signin">
      <div className="signin-card">
        <p className="wordmark">JobKeep</p>
        <h1>{creating ? 'Create your account' : 'Sign in'}</h1>
        <p className="quiet">
          {creating
            ? 'One account holds your applications, your CVs and every check you run.'
            : 'Your applications are behind this.'}
        </p>

        {error ? <SignInError error={error} creating={creating} /> : null}

        <form className="signin-form" onSubmit={submit}>
          <label className="field">
            <span>Email</span>
            <input
              type="email"
              name="email"
              autoComplete="username"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
          </label>

          <label className="field">
            <span>Password</span>
            {/* The autocomplete value is what tells a password manager whether to
                offer a saved password or to generate a new one, and getting it
                wrong is the difference between the browser helping and the
                browser silently doing nothing useful. */}
            <input
              type="password"
              name="password"
              autoComplete={creating ? 'new-password' : 'current-password'}
              aria-describedby={creating ? 'password-rules' : undefined}
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </label>

          {/* OUTSIDE the label, and described by it instead. A hint inside a
              <label> is part of the label, so the field's accessible name
              becomes "Password At least six characters, with an…" — which is
              what a screen reader announces on focus, and what a test looking
              for a field called "Password" fails to find. aria-describedby is
              the attribute for a hint: announced after the name, not as it. */}
          {creating ? (
            <p id="password-rules" className="field-hint quiet">
              At least six characters, with an uppercase and a lowercase letter, a
              digit and one symbol.
            </p>
          ) : null}

          <button type="submit" className="btn btn-primary" disabled={busy}>
            {busy ? 'Working…' : creating ? 'Create account' : 'Sign in'}
          </button>
        </form>

        {/* A button, not a link: it changes what this form does, it does not go
            anywhere. The email and password already typed are kept, because the
            usual reason to press it is having guessed wrong about which one you
            needed. */}
        <p className="quiet">
          {creating ? 'Already have an account?' : 'No account yet?'}{' '}
          <button
            type="button"
            className="linkish"
            onClick={() => {
              setCreating((c) => !c);
              setError(null);
            }}
          >
            {creating ? 'Sign in instead' : 'Create one'}
          </button>
        </p>
      </div>
    </main>
  );
}

/* The failure card is not the shared <Failure> one, and this is the reason: a
 * 401 here does not mean what it means anywhere else in the app. Everywhere else
 * it is the session ending, which App handles centrally; on this form it is the
 * password being wrong, and Identity's own detail for that is the word "Failed".
 * The other statuses are worth passing through — a 400 from register carries
 * "Username 'x' is already taken.", which is the whole message. */
function SignInError({ error, creating }: { error: ApiError; creating: boolean }) {
  const message = error.isUnauthenticated
    ? 'That email and password do not match an account.'
    : error.message;

  return (
    <div className="error" role="alert">
      <strong>{creating ? 'Could not create the account' : 'Could not sign in'}</strong>
      <p>{message}</p>
    </div>
  );
}
