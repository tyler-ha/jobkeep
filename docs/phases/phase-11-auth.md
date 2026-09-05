# Phase 11 — authentication and owner scoping

**Status: IN PROGRESS. 11.1a, 11.1b and 11.1c landed 2026-09-04; 11.2a on 2026-09-05.** It was to run immediately
after [Phase 10](phase-10-aws-deploy.md) and be coupled to it; **Phase 10 was
dropped on 2026-09-04** and this phase was pushed to the end of the roadmap in
the same decision. See `architecture.md` decision 22.

**It was then started early, at the user's instruction on 2026-09-04**, with the
gate below read out first and accepted. The gate said *"this ships before
whatever deploy replaces Phase 10, and not before"* — that is an argument about
when authentication becomes load-bearing, not a dependency: **nothing in this
phase needs a host to exist.** The one thing that does is the CORS named origin,
which is a config value. So building it early costs nothing that has to be
redone; it only means the tool is authenticated before anything can reach it.

## Sub-steps, and why it is split

The phase does not fit in one session, so it is split the way every large phase
here has been (13.1–13.6, 6.1–6.5). Each ends in something runnable.

| Step | What it is | Status |
|---|---|---|
| **11.1a** | The Identity module exists and migrates. Sixth `DbContext`, `identity` schema, seven tables, its own `__EFMigrationsHistory`. **No sign-in yet, nothing enforced.** | **Done 2026-09-04** |
| **11.1b** | Register and sign in. `AddIdentity*` wiring in `Jobkeep.Api`, the endpoints, and the **named-origin CORS policy** — the trap below, which fires here and not later | **Done 2026-09-04** |
| **11.1c** | The client half: a login route, a route guard in `App.tsx`, `credentials: 'include'` on the one `request()`, and a 401 branch on `ApiError` | **Done 2026-09-04** |
| **11.2a** | Lock the doors. Every controller `[Authorize]`d, GraphQL behind `RequireAuthorization()`, and a suite that can satisfy both. **No schema change, no owner column yet.** | **Done 2026-09-05** |
| **11.2b** | Owner scoping. `OwnerUserId`, the EF global query filter, the slices re-scoped, the three published views re-cut | |
| **11.3** | Postgres RLS — the second layer — plus the test that disables the EF filter and proves the database still refuses | |

**The split is along "what can be verified", not "what is convenient".** 11.1a is
a schema you can look at; 11.1b is an account you can create; **11.2a is a
signed-out browser that gets nothing**; 11.2b is a second
user who cannot see the first's rows; 11.3 is that same guarantee with the
application's own filter switched off.

### 11.1a, as built

Ten projects became eleven: `src/Jobkeep.Modules.Identity/`, a plain class
library like every other module. That survives ASP.NET Core Identity, which is
worth stating because it looks like it should not —
`Microsoft.AspNetCore.Identity.EntityFrameworkCore` brings the **stores** (seven
entity types and their EF mapping) and nothing that needs a web host. The
middleware, the cookie handler and `AddIdentity*` are ASP.NET Core proper and
land in `Jobkeep.Api` at 11.1b. The layering claim is unchanged: this project
knows about a database, not about HTTP.

Four decisions inside it, none of them re-openable without a reason:

- **`JobkeepUser : IdentityUser<Guid>`, and the subclass is empty.** The Guid key
  is the point: `IdentityUser`'s `TKey` defaults to `string` and stores a GUID's
  text form, which would make this the only table in the database with a text
  primary key — and would make 11.2's `OwnerUserId` a `varchar` foreign key on
  every scoped table. The class exists now, empty, because Identity's machinery is
  generic on the user type; introducing it later would touch every one of those
  generic parameters, and introducing it now costs one empty class.
- **The platform's table names are KEPT** — `AspNetUsers`, not `users`. They look
  out of place beside `job_applications` and `match_results`, and they stay:
  they are the names every Identity sample, tutorial and diagnostic tool uses, so
  a reviewer opening the database recognises what it is in one glance. Renaming
  buys cosmetic consistency at the price of a schema that no longer looks like the
  thing it is. **The schema qualifier does the real separating.**
- **`ModelConventions.ApplyDatabaseDefaults` is deliberately NOT called** — the
  only context in the repo that skips it. It exists to put a floor under writers
  that are not EF: `gen_random_uuid()` on Guid primary keys, `now()` on the audit
  pair. Neither applies. None of Identity's types is `IAuditable`, and three of the
  seven have **composite keys made of foreign keys** (`AspNetUserRoles` is
  `(UserId, RoleId)`, both Guid, both primary-key properties by that convention's
  test). Defaulting those to a random uuid puts a default on a foreign key column
  where the only row it can produce is one that fails the constraint. **A default
  that can only generate an error is worse than no default.**
- **The audit interceptor IS registered**, and is a deliberate no-op — nothing here
  is `IAuditable` or `ISoftDeletable`. Keeping "every module context is built the
  same way" true is worth more than saving one delegate.

**Seven tables, not four.** `IdentityUserContext` would drop roles and give four.
Not taken: roles are what an interviewer asks about next, and adding them later is
a migration against a table that already has rows. This is the trade recorded in
decision 3 below, and it is the one place in this repo where the ponytail ladder
was overruled on purpose.

**Schema moved — diagrams deliberately not redrawn, frozen until 1.0.** A sixth
schema and seven new tables would have triggered both `docs/diagrams/*.svg` under
the old rule. Logged here so the eventual redraw is a list rather than an
investigation; 11.2 will add to it.

**The trap it actually hit:** `src/Dockerfile` copies each `.csproj` by name
before restoring, and a new project has to be added to that list. It fails *late*
and misleadingly — restore succeeds against the projects it was given, then the
build dies on a `ProjectReference` to a directory nobody copied, which reads like
a compiler error. The Dockerfile now says so above the list.

### 11.1b, as built

**Ten routes and one of them is ours.** `AddIdentityApiEndpoints<JobkeepUser>()`
plus `MapIdentityApi<JobkeepUser>()` inside a `/identity` group is the whole
sign-in surface: register, login, refresh, confirmEmail, resendConfirmationEmail,
forgotPassword, resetPassword, manage/2fa, manage/info. The tenth is `logout`,
written here, because `MapIdentityApi` has none — and that is not an oversight
on Microsoft's part: signing out a *bearer token* is something only the client
can do. With a cookie it is the opposite, so it is four lines and a
`RequireAuthorization()`.

Five things decided in the step:

- **It is a deliberate exception to 13.5's "every route is a controller
  action".** These routes are not written here, they are the framework's, and
  re-typing them as a controller would mean re-typing password hashing, the
  security stamp, lockout and the token flows — precisely the work decision 3
  chose this package to avoid. What 13.5 actually bought was *"the composition
  root has one shape for HTTP"*, and one framework-supplied group beside
  `MapControllers()` does not spend that. A rule that has to be re-argued in
  order to add hand-written routes is doing its job.
- **The CORS trap cost one line, not a rewrite, and that is the payoff of a
  decision made three phases earlier.** Phase 6.1 refused `AllowAnyOrigin` and
  wrote down why — *"AllowAnyOrigin and AllowCredentials are mutually exclusive,
  so writing the wildcard now would have to be undone the moment auth lands"*.
  It landed; the origin list was already explicit; `.AllowCredentials()` was the
  entire change. **The handoff and this plan both budgeted a named-origin policy
  as work in this step. There was none to do.**
- **`UseAuthentication()`/`UseAuthorization()` are written out, not left to
  `WebApplication`'s auto-insertion**, because the order against `UseCors` is the
  point: a preflight `OPTIONS` carries no cookie, so authorization running first
  refuses it before CORS ever answers — and that surfaces in the browser as a
  CORS error with nothing on the server to explain it.
- **`AddEndpointsApiExplorer()` came back.** 13.5 dropped it because it left no
  minimal APIs; `MapIdentityApi`'s routes are minimal APIs and MVC's explorer
  does not see them. Without it the endpoints work and are invisible in Swagger
  UI — which is the one place a human signs in from before 11.1c exists.
- **Roles are still not registered** (`.AddRoles<IdentityRole<Guid>>()`). The
  three tables exist so that adding them later is not a migration against rows;
  nothing authorizes on a role yet, so a `RoleManager` nobody injects would be
  registration for its own sake.

**Verified against the running stack, not only the suite** — register 200, login
200 with a `Set-Cookie`, `manage/info` 200 with it and 401 without, logout 204
and 401 after, and `swagger.json` still 200 with all ten paths in it.

**Two known ceilings, written down rather than fixed.**
`forgotPassword` and `resendConfirmationEmail` are mapped and answer 200, but
`AddIdentityApiEndpoints` registers a **no-op `IEmailSender`** — so no mail is
sent and no token ever reaches a human. Email confirmation is not required to
log in, so nothing is broken by it today; the day a real deploy exists, either
wire a sender or unmap those two. And **antiforgery is still off**
(`DocumentsModule.cs` argues it, on the grounds that there were no cookies for a
browser to attach). There are now. It stays off while nothing is `[Authorize]`d —
11.2 is when a forged cross-site POST can do something, and that is where the
paragraph gets re-read.

Suite 332 → 338: five in `tests/Jobkeep.Tests/Rest/IdentityTests.cs` and one
added to `CorsTests`. No migration — 11.1a's is the only one this phase has so
far.

### 11.1c, as built

Five files: `web/src/routes/SignIn.tsx` (new), `App.tsx`, `lib/api.ts`,
`styles/shell.css`, and the fixtures. Web suite **55 → 62**, no backend change
except one comment. **The estimate in "Frontend impact" was right** — the choke
point held, and no existing screen changed.

**IT IS NOT A ROUTE, which is a deliberate deviation from the plan.** The plan
said "a login route and a route guard in `App.tsx`". What shipped is one piece
of state in `App` with three values — `undefined` (not asked yet), `null`
(signed out), an `Account` — and the signed-out branch renders `<SignIn>`
*instead of* the shell, whatever the address is. Two things fall out of that,
and both are better than the `/login` version:

- **The address survives.** Open a bookmarked `/applications/{id}` with an
  expired session, sign in, and you are on that job post. No `?returnUrl`, and no
  code to carry one.
- **There is no signed-out URL to strand on.** A `/login` route is reachable
  while signed in, and that state then needs a rule of its own.

It is also less code, which is the tiebreak rather than the argument.

**The third state is the one that matters.** There is no token in the client to
inspect — the cookie is `HttpOnly` — so the only honest way to know whether a
session is live is to ask the server, and that is a round trip. `undefined`
renders nothing for its duration: showing the shell would flash the app at
someone about to be given a sign-in form, and showing the form would flash a
sign-in at someone already signed in.

**The 401 handler is in `request()`, not in eight screens.** `onUnauthenticated`
is a module-level slot `App` fills with "forget who is signed in". A session
expires between *requests*, not between screens, so a per-screen branch would be
eight copies of a rule with one cause — and nothing on the Applications screen
knows about authentication, which is the property worth keeping. `ApiError`
gained `isUnauthenticated` beside `isRuleRefusal` and `isMissing`, used in
exactly one place: the sign-in form, where a 401 means *wrong password* rather
than *session over*.

**Two bugs found on the way, both in `request()` and both pre-existing in shape:**

- **A 200 with no body threw.** `/identity/login` and `/identity/register` both
  answer one, and `res.json()` on an empty body throws a `SyntaxError` *outside*
  the try that catches fetch failures — so a successful sign-in would have
  reported "Could not reach the API". Now reads text first.
- **`ValidationProblemDetails` was read wrong.** The sentence a person needs is
  in `errors`; `title` holds "One or more validation errors occurred.". Identity's
  register is the first endpoint in this app to answer that shape, and "Username
  'x' is already taken." is the whole message.

**The form can also create the account.** One boolean and a different URL, since
11.1b mapped both — and a sign-in form whose only alternative is Swagger is a
dead end. Registration is **open**; that is right for localhost and is the first
thing to close when a host is chosen (`ponytail:` note on `register` in
`api.ts`).

**A hint inside a `<label>` is part of the label.** The password rules were a
`<span>` inside the field's label, which made the input's accessible name
"Password At least six characters, with an…" — announced that way on focus, and
unfindable by a test looking for a field called "Password". Moved out and wired
with `aria-describedby`. Worth knowing because `.field-hint` is used inside
`.field` elsewhere in `web/`.

**The ceiling this step leaves, and it has an expiry date: `SameSite` is `Lax`.**
`:5173` calling `:5080` is cross-*origin* but same-*site* — `SameSite` is blind
to the port — so the browser attaches the cookie and this works. A deployed front
end on a different domain from the API would be genuinely cross-site and the
cookie would **silently stop being attached**: signed in, then anonymous, with
nothing wrong on either side to find. The fix is `SameSite=None`, which requires
`Secure` and therefore HTTPS, so it cannot be set today without breaking local
development over http. Argued at the registration in `Program.cs`.

**Not verified in a browser.** The Chrome extension was not connected in the
session that built it, so what is proven is the suite (which renders the real
`App` through the real gate) plus 11.1b's curl round trip against the running
stack. **The appearance of the sign-in card is unreviewed** — it belongs in the
Phase 6 visual pass that is already waiting on the user.

### 11.2a, as built

**Nine files, no migration, no `web/` change, suite 338 → 341.** The whole of it
is five attributes, one `RequireAuthorization()`, and a test double that lets the
other 338 tests keep working. It is the smallest step in the phase and it is the
one that closes F1's actual hole: before it, every route in this application
answered an anonymous caller in full.

**The split from 11.2b was made here, not in the plan**, and the line is between
*"is anyone there"* and *"whose row is this"*. Locking the doors needs no column,
no migration and no re-scoped query; scoping needs all three and touches twenty
tables. Doing them together would have meant a session that could not be verified
in halves — and the two questions fail differently, so they are worth being able
to answer separately.

**Six things from it.**

- **`[Authorize]` on each controller, NOT a fallback policy.** A fallback policy
  in `Program.cs` is one line and covers every future route, which is the version
  that looks lazier. It was refused because it also covers `/identity/login` and
  `/identity/register`, and the escape — `.AllowAnonymous()` on the identity group
  — is applied to a route GROUP whose members already carry their own
  `RequireAuthorization()` (`manage/info`, `manage/2fa`, our `logout`). Whether
  the group's convention or the endpoint's own metadata wins is a detail of how
  conventions are ordered, and betting the lock on it is the wrong bet. Five
  attributes cannot be misread.
- **The test that makes the five attributes safe reads the ROUTING TABLE, not the
  controllers.** `AuthorizationTests.Every_endpoint_outside_identity_requires_authorization`
  asks the app's own `EndpointDataSource` for every endpoint and fails on any
  outside `/identity/` with no `IAuthorizeData`. A reflection test over
  `ControllerBase` subclasses would have passed just as well today and would not
  have covered the GraphQL mount, a hand-written minimal API, or the sixth
  controller. **A test that only inspects the doors it was told about is not a
  lock.** It also asserts the endpoint list is non-empty, because the interesting
  way for this test to break is to stop finding anything.
- **GraphQL is locked at the ENDPOINT, which avoided a new package.**
  `[Authorize]` on a resolver needs `HotChocolate.AspNetCore.Authorization`, and
  it buys per-field policies this app has no use for — there is one rule and it is
  "be signed in". `app.MapGraphQL().RequireAuthorization()` uses the authorization
  services already registered, covers the surface at once, and cannot be forgotten
  on a new resolver. The one visible cost is that a signed-out browser gets a 401
  from the Nitro IDE instead of the IDE.
- **BOTH SURFACES ARE ASKED OVER THE WIRE, and that is deliberate rather than
  thorough.** The metadata test would still pass if `UseAuthorization()` were
  deleted from the pipeline: metadata is a declaration, and only a request proves
  something enforces it. Two more tests send an anonymous REST GET and an
  anonymous GraphQL POST and require 401 from each. F5 was a GraphQL-only exposure
  — "REST is locked" has never been an answer in this repo.
- **THE ANTIFORGERY PARAGRAPH WAS RE-READ, AS 11.1b PROMISED, AND ANTIFORGERY
  STAYS OFF.** The condition it rested on is genuinely gone: there are credentials
  for a browser to attach now. What holds it off is the cookie's **`SameSite=Lax`**
  — Lax attaches a cookie to top-level GET navigations and to nothing else, so a
  cross-site POST, PUT or DELETE arrives anonymous and is refused before any
  handler runs, the multipart upload included. That is CSRF protection; it is just
  not the token kind. **Its expiry date is the cookie's own**: a deployed front end
  on another site forces `SameSite=None`, which is precisely "attach this cookie
  cross-site", and antiforgery becomes mandatory in the same change that sets it —
  not in a later one. The two notes are written next to each other in `Program.cs`
  so they are found together.
- **Swagger UI and `swagger.json` are still open, deliberately.** Both are
  Development-only, so a deployed app serves neither, and requiring a sign-in for
  the document a person reads *in order to sign in* is a circle. The endpoint test
  does not see them because they are middleware rather than endpoints, which is
  luck rather than design — worth knowing before someone "fixes" the exemption
  that is not there.

**How the suite gets past the lock: `TestAuthHandler`.** An authentication scheme
registered by `JobkeepAppFactory` only, which turns an `X-Test-User` header into a
principal with a `NameIdentifier` claim. `IntegrationTestBase` puts the header on
`Client`; `IdentityTests` overrides `AuthenticateClient` to false so its client
still meets the real cookie.

The alternative — register and log in a real user in every arrange — was measured
rather than dismissed: Respawn truncates the `identity` schema between tests so no
account survives, and Identity hashes a password in roughly a tenth of a second,
which is a minute added to a forty-second suite to re-prove what `IdentityTests`
proves once. **It is not a bypass of the authorization rule — every request still
has to satisfy it — only of the login round trip.**

**The trap inside the test double, which cost a wrong fix to `Program.cs` before
the cause was found.** The scheme registers itself as `DefaultScheme` and forwards
every request without the header back to Identity, and the first version forwarded
to `IdentityConstants.ApplicationScheme` — the application cookie, the obvious
target. It is the wrong one. Identity's real default is a **composite** scheme
that forwards an unmet challenge to the *bearer* handler, which answers **401**;
the cookie handler on its own **redirects** to `LoginPath`, which is
`/Account/Login`, a Razor page this application does not have. So the suite saw a
404 where the running app sends a 401 — a test double lying about the thing it
exists to stand in for. It was diagnosed as a production defect first, and
`ConfigureApplicationCookie` was written to turn that redirect into a 401 before
the real cause surfaced; **that change was then deleted, because production never
had the defect.** The forward target is now read off `AuthenticationOptions`
rather than retyped, since `IdentityConstants`' field for the composite is
`internal`.

**Verified against the running stack as well as the suite**, because a
pipeline-order mistake is exactly the kind that a `WebApplicationFactory` and a
browser can disagree about: anonymous `GET /applications` 401, anonymous
`POST /graphql` 401, anonymous `GET /stats/skill-demand` 401, then login 200 and
both surfaces 200 with the cookie, and `swagger.json` still 200.

**Not verified in a browser.** The Chrome extension is still not connected. The
front end needed no change — 11.1c's `credentials: 'include'` and its
`onUnauthenticated` slot were built for exactly this day — and its own suite is
unaffected, so what is unproven is only that the sign-in screen appears when a
session expires mid-session rather than at first paint.

**A pre-existing web-suite failure was found and deliberately left alone.**
`web/src/routes/screens.test.tsx > the add form offers the ad, and sends it as the
description` fails in a full-file run and **passes in isolation**, so it is order
dependent: something earlier in the file leaves `App`'s account probe resolving to
signed-out, and the add form renders the sign-in card instead. It is on `develop`
at `a575630` with no `web/` file touched by this step, it is 11.1c's, and the web
suite is not in CI. Recorded rather than folded into this step's diff.

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
