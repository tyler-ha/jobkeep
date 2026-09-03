# Phase 11 — authentication and owner scoping

**Status: Planned.** Not started. Runs immediately after
[Phase 10](phase-10-aws-deploy.md) and is coupled to it.

## Why it is here and not earlier

This is the roadmap's **largest compounding cost** — `architecture.md`'s gap
register says so directly: *"every phase built before it lands adds queries to
re-scope"* — and the rule this roadmap was ordered by is that compounding work
comes first. So its position needs an argument, not an assumption.

**The compounding is real but linear, and it is on the backend only.**

- **Backend: it compounds.** Every slice written before this lands is a query to
  re-scope. There are roughly two dozen. Phases 7–10 add perhaps five more. That
  is a ~20% growth in the size of this phase, not a multiple — meaningful, and not
  the same order as Phase 7's, which grows with every *row* the user files.
- **Front end: it barely compounds at all, and this was worth checking rather than
  assuming.** Every call to the API goes through one function — `request()` in
  `web/src/lib/api.ts` — so a credential is added in one place for all eight
  screens, however many screens there are. What this phase costs on the client is
  a login route, a route guard, and a 401 branch; none of that grows with screen
  count. The single choke point is why auth could be deferred past the front end
  without the usual penalty.

**And it is gated on two things that must come first.** A single-user tool on
`localhost` has no threat model to authenticate against — F1 is only load-bearing
once the API is reachable, which is Phase 10. And `architecture.md` **decision 9**
(`skills` stays global when scoping lands) is still status *Proposed*; it must be
confirmed or overturned before this is built, because it decides the scoping root.

So: not first, but it must not slip past the deploy. An unauthenticated
`/applications` on a public Function URL is F1 with the mitigating circumstance
removed.

## Scope

Closes **F1** and **F9**. `security-and-data-audit.md` §5 step 3.

- A `users` table, and `OwnerUserId` on `job_applications`, `job_postings` and
  `companies`. **`skills` stays global** — confirm decision 9 first. The accepted
  cost is real and should be said out loud: one user's skill taxonomy is visible
  in aggregate to another. Per-user skill rows would destroy the single `GROUP BY`
  that decision 1 (Postgres over DynamoDB) and the Phase 2.4 analytics both rest
  on, which is the entire reason Postgres was chosen.
- `CreatedBy` / `UpdatedBy` become nullable FKs to `users` — F9 was blocked on
  this phase because there was no actor to name.
- Enforcement is **an EF global query filter *plus* Postgres RLS**: the filter
  makes the app naturally query one user's rows; RLS makes a forgotten filter
  unable to leak. Belt and braces on purpose, and the reasoning is the interview
  material — a query filter is a convention the next slice can forget, RLS is not.
- The `Identity` module, which `CLAUDE.md` has listed as "(later)" since Phase 2.

## Frontend impact: **MEDIUM, but concentrated — not spread**

- **~5 lines in `api.ts`.** `request()` grows an `Authorization` header and a 401
  branch. `ApiError` already carries the status, so a 401 gets a getter beside
  `isRuleRefusal` and `isMissing` and the house rule extends naturally.
- **One new screen** (login) and one route guard in `App.tsx`. A new screen is a
  new file in `routes/` plus one `<Route>` — and *not* an entry in `NAV`, since
  login is not a destination.
- **The CORS policy must be revisited**, and this is the trap. Phase 6.1 added
  `AllowAnyOrigin`; **`AllowAnyOrigin` and credentials are mutually exclusive**,
  and `src/Program.cs` says so at the point it matters. Cookie-based auth
  therefore forces a named-origin policy in the same change. A bearer token in a
  header does not — which is an argument for the token, and it should be made
  explicitly rather than discovered at the browser.
- **No existing screen's layout or data shape changes.** That is the whole payoff
  of the choke point.

## Verification

- A test that a second user cannot read the first user's applications **through
  both surfaces**. The GraphQL half matters: F5 was a GraphQL-only exposure that
  REST did not have, and the guard against its class of mistake
  (`SurfaceParityTests`) is where this belongs.
- A test that RLS refuses a query with the EF filter deliberately disabled — that
  is the assertion that the second layer is actually a second layer.
- The **doc/security-audit sweep** is due when Phase 10 unparks; F1 is the largest
  finding in it, so re-read the audit before building rather than after.

## Next

[Phase 12](phase-12-feature-expansion.md) — feature expansion, which is where the
P3 backlog items get pulled from.
