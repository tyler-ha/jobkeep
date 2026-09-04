# Phase 11 — authentication and owner scoping

**Status: Planned, and deliberately LAST.** Not started. It was to run
immediately after [Phase 10](phase-10-aws-deploy.md) and be coupled to it;
**Phase 10 was dropped on 2026-09-04** and this phase was pushed to the end of
the roadmap in the same decision. See `architecture.md` decision 22.

## Decided 2026-09-04, before any code — do not re-litigate

Four questions were put to the user together. The answers narrow this plan and
are recorded here so the next session builds rather than re-asks:

1. **Build it last, not now.** The AWS deploy is dropped and a replacement host
   is undecided, so the threat model this phase was gated on still does not
   exist. Phases 8, 9 and 12 go first. **Its number stays 11** — see decision 22
   for why the number was not swapped with 12.
2. **`architecture.md` decision 9 is CONFIRMED** and its status moved from
   *Proposed* to **Accepted** on 2026-09-04. `skills` stays global; every other
   table gets `OwnerUserId`. The accepted cost is unchanged and still worth
   saying out loud: one user's skill taxonomy is visible in aggregate to
   another. That gate is now closed and does not need re-asking.
3. **ASP.NET Core Identity, in full** — one new NuGet package
   (`Microsoft.AspNetCore.Identity.EntityFrameworkCore`), **approved by the user
   on 2026-09-04**. This was chosen *over* the smaller option, which was a
   hand-rolled `users` table using `PasswordHasher<T>` and cookie auth — both
   already in the ASP.NET Core shared framework, so zero new packages. The
   tradeoff the user accepted: seven tables (users, roles, claims, logins,
   tokens, user-roles, role-claims) for a tool with one user, in exchange for
   lockout, 2FA and token flows that are already written and already audited,
   and for the fact that "I used the platform's identity system" is the answer a
   .NET interviewer expects. **This is the one place in the repo where the
   ponytail ladder was overruled on purpose, and by whom.**
4. **No third-party login.** The user took the licence they had already offered
   (*"if this scope too big, we can leave it out"*). OAuth needs a registered
   redirect URI at a public origin, which is the deploy that no longer exists.
   Identity supports external providers natively, so adding one later is
   configuration plus a callback route, not a rewrite.

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

**It was gated on two things. One is now closed; the other outlived its phase.**
Decision 9 (the scoping root) was *Proposed* and is now **Accepted** — closed.
The other gate was Phase 10: a single-user tool on `localhost` has no threat
model to authenticate against, so F1 is only load-bearing once the API is
reachable. **Dropping the AWS deploy did not close that gate, it removed its
date.** The premise still holds — there is still nothing to authenticate against
— so the gate now reads: **this ships before whatever deploy replaces Phase 10,
and not before.** An unauthenticated `/applications` on any public URL is F1
with the mitigating circumstance removed.

## Scope

Closes **F1** and **F9**. `security-and-data-audit.md` §5 step 3.

- A `users` table, and `OwnerUserId` on `job_applications`, `job_postings` and
  `companies`. **`skills` stays global** — decision 9, confirmed 2026-09-04. The accepted
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
  It hosts ASP.NET Core Identity's seven tables in its own `identity` schema, with
  its own `DbContext` and its own `__EFMigrationsHistory` — a **sixth** context;
  `Program.cs` migrates five today.
- **Identity's default is cookie auth, so the CORS trap below fires.** The bearer
  token that would have dodged it was the hand-rolled option, and that option was
  not taken. Budget the named-origin policy as part of this phase, not as a
  surprise at the browser.

## Frontend impact: **MEDIUM, but concentrated — not spread**

- **~5 lines in `api.ts`.** With Identity's cookie default that is
  `credentials: 'include'` on the one `fetch`, not an `Authorization` header —
  plus a 401 branch. `ApiError` already carries the status, so a 401 gets a getter beside
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
- The **doc/security-audit sweep** was due "when Phase 10 unparks", and Phase 10
  is dropped; it is now due before whatever deploy replaces it. F1 is the largest
  finding in it, so re-read the audit before building rather than after.

## Next

Nothing. This is the **last** phase on the roadmap as of 2026-09-04.
[Phase 12](phase-12-feature-expansion.md) — feature expansion — now runs
*before* it, not after.
