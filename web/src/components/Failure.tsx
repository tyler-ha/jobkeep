import { ApiError } from '../lib/api';

/* The error state, shared by every screen that fetches.
 *
 * Two failures that look the same in a try/catch read completely differently to
 * the user, so they are separated here: status 0 means nothing answered — the
 * API is down, or CORS refused — and the recovery is to start it. Anything else
 * means the API answered and said no, and the recovery is whatever it said.
 *
 * Errors name the problem and the recovery. Neither of these says
 * "Something went wrong". */
export function Failure({ error, what }: { error: ApiError; what: string }) {
  const unreachable = error.status === 0;

  return (
    <div className="error" role="alert">
      <strong>{unreachable ? 'No answer from the API' : `Could not ${what}`}</strong>
      {unreachable ? (
        <p>
          {error.message} Start it with <code>cd src &amp;&amp; dotnet run</code>, and
          check Postgres is running.
        </p>
      ) : (
        <p>
          {error.message}
          {error.status >= 500 ? ' The API logs will have the stack trace.' : ''}
        </p>
      )}
    </div>
  );
}
