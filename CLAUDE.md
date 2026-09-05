# CLAUDE.md

Context for Claude Code (or any Claude session) working in this repo.

**`docs/architecture.md` is the authority on how the code is shaped and why.**
This file is the short version plus the working rules. If the two ever
disagree, `architecture.md` wins and this file should be corrected.

## What this project is

A personal job-application tracker, built as a portfolio project by
someone with beginner-to-intermediate C# skills who is actively learning
AWS and preparing for a job search in the Melbourne market in ~1 year.
Also used as prep material for behavioral (Leadership Principle style)
interview stories — see "STAR log" in the root README.

Two audiences, and they pull in different directions. Keep both in mind:
1. **The tool** — it should actually be useful for tracking applications.
2. **The evidence** — it should demonstrate skills a Melbourne .NET employer
   asks for, and every significant decision should be one the person can
   *defend out loud*, including the tradeoff they accepted.

A decision that can't be explained is worth less here than a simpler one that
can. Prefer the defensible choice over the impressive-sounding one.

## Priorities, in order

1. **Cost stays near-zero.** Every AWS/AI choice should default to free
   tier or on-demand/serverless pricing. Never suggest always-on
   infrastructure (e.g. a provisioned EC2 instance or provisioned-capacity
   DynamoDB) without flagging the cost tradeoff explicitly. Note: storage is
   PostgreSQL (see Architecture). Local dev uses Postgres in Docker (free).
   **There is no deployed DB, because there is no deploy** — the AWS phase was
   **dropped on 2026-09-04** (`architecture.md` decision 22) and a free host is
   still to be chosen. **Neon's free tier** (serverless Postgres, scales to zero,
   $0) remains the leading candidate and was never AWS-specific; it replaced RDS
   free-tier in `docs/phases/phase-10-aws-deploy.md`, which is now a researched
   record rather than a plan. Read it for the rejected alternatives and for the
   rule that outlived the target: **nothing in the deployed architecture may
   bill per hour.**
2. **Each phase should end in something runnable.** The person has a
   history of abandoning projects when scope gets fuzzy. Don't let a
   phase sprawl — if a change is getting large, suggest splitting it.
   This is also why architecture changes are adopted *incrementally*, per
   phase, rather than as one big refactor. It turns out to be the token-cost
   control too — see "Build cost" below.
3. **Explain, don't just generate.** The person is using this project to
   build real understanding (for interviews) as much as to build the app
   itself. Prefer short explanations of *why* alongside code changes,
   especially around design decisions (module boundaries, AWS service
   choices, AI provider abstractions). This means **in code, alongside the
   change** — it is not a mandate to keep the standing docs continuously
   current. See "Documenting as you go" for what that does and doesn't oblige.
4. **Local-first development.** Prefer developing against local/free
   equivalents (Postgres in Docker, Ollama) before touching real AWS or paid
   APIs, matching the pattern already established in Phases 1-2.

## Build cost, in tokens

Measured, not estimated. **`docs/token-log.md` holds the ledger** — per phase and
per session, generated from transcripts by `scripts/token-usage.py`. Deliberately
not duplicated here: running totals in a file that loads into every session are a
standing maintenance obligation, which is the thing this file is now trying to
avoid. What follows is only the part that is stable and changes decisions.

For scale: the project is a few hundred million tokens across ~2000 turns, ~95%
of it `cache_read` (context replay). Fresh input plus output is under 5% of gross.

**The one finding that should change behaviour: cost is superlinear in session
length, not in task difficulty.** Every turn replays the conversation so far, so
a session's later turns cost far more than its early ones:

| Session length | Cost per turn |
|---|---|
| under ~40 turns | 30–40k |
| ~90–130 turns | 55–65k |
| 160–210 turns | 120–170k |
| 300+ turns | 140–210k |

These brackets were fitted on Phases 1-2.2 and predict the *shape*, not the
number: Phase 2.4's first 78 turns ran at ~99k/turn, well above the bracket,
because the standing context every turn replays — this file, the docs, the source
— has grown since. The floor drifts up as the project does. The lever below is
unchanged.

**The operating rule that follows from this: keep the context window under 120k
tokens, then `/handoff`, `/clear`, and reload the handoff doc in a fresh session.**
The rule itself is in `~/.claude/CLAUDE.md` (user level, every project); what is
specific to *this* repo is the conversion, because nobody can act on a token count
they cannot see:

- Fixed overhead here is **~40k before a word is exchanged** — system prompt,
  tools, this file, the skills. So 120k total is only ~80k of actual conversation.
- Measured on the Phase 2.4 session: **78 turns = 147k total / 107k messages.**
  So **120k lands around turn 55-60** for turns of that shape — slices, tests, a
  few large file reads. Fewer if the turns are read-heavy, more if conversational.
- I cannot see my own context usage; `/context` is yours to run. I watch turn
  count as a proxy and will say when it trips rather than continuing silently.

Hand off **at the end of a runnable unit**, not mid-task — priority 2 and the cost
data agree here, which is the useful part. A handoff in the middle of a
half-applied refactor costs more than the context it saves.

Four phases have now been measured twice — logged mid-session, then corrected
after the session actually ended — and all four say the same thing:

| Phase | Front | Back | Back half costs |
|---|---|---|---|
| 2.2 | 185 turns @ 163k/turn | 112 turns @ 286k/turn | **+75%** per turn |
| 2.3 | 198 turns @ 167k/turn | 62 turns @ 311k/turn | **+86%** per turn |
| 2.4 | 78 turns @ 99k/turn | 85 turns @ 180k/turn | **+82%** per turn |
| 2.5 | 86 turns @ 89k/turn | 62 turns @ 168k/turn | **+87%** per turn |

Same session, same task, either side of the line. So the lever is *where a
session ends*, not how hard the work is. Finishing a phase and starting a fresh
session is worth more than any prompt-level economy.

Phase 2.5 sharpens it: its expensive back half was **not** the phase work, which
was done at turn 86. It was committing, opening two PRs and writing the handoff —
mechanical git housekeeping bought at 168k a turn because it happened at the end
of a long session instead of the start of a short one.

Two things worth not re-learning:
- **Don't read a total logged mid-session as final.** This has now been got
  wrong **four phases running**. Phase 2.2 was logged at 185 turns / 30.2M and
  finished at 297 / 62.2M — that wrong number sat in `token-log.md` for a phase
  and made it claim Phase 2 was the most expensive item. Phase 2.3 was logged at
  198 / 33.1M and finished at 260 / 52.4M. Phase 2.4 was logged at 78 / 7.7M and
  finished at **163 / 23.0M**, three times the figure, and the wrong number was
  load-bearing: `token-log.md` had built a whole paragraph on 2.4 being "the
  control" that escaped the pattern. It hadn't. Phase 2.5 was logged at 86 / 7.7M
  and finished at **148 / 18.1M**, and its row had *predicted* its own correction
  while still getting the size of it wrong. A real measurement of an unfinished
  thing is much easier to miss than an estimate — so **write the row saying it is
  provisional**, and correct it next phase.
- **Know which documentation actually recurs.** The architecture record, the
  diagrams and the security audit cost 53.1M between them — but those were
  three *one-off foundational* sessions. They do not repeat, and cutting that
  kind of work is cutting the wrong thing. What repeats is **sweeps over files
  the change never touched.** Phase 2.2 by prompt:

  | Prompt | Turns | Total |
  |---|---:|---:|
  | write the tests (the feature work) | 141 | 19.8M |
  | commit + renumber the phase docs | 37 | 10.2M |
  | *"audit again my docs phases … follows standard flow"* | 34 | **10.3M** |
  | *"check if i have skill to audit all the md file"* | 10 | **3.2M** |

  13.5M of re-reading markdown that had not changed — as much as the entire
  security audit cost to write from scratch — and both sweeps ran late in a
  297-turn session at ~286k/turn. That is what "Documenting as you go" exists
  to stop. Writing docs *beside* the code is the cheap case: the context is
  already loaded.

## Architecture

**Modular monolith with vertical slices — one deployable, module boundaries
drawn now so services can be extracted later if a real trigger appears.**
Full reasoning, the extraction triggers, and the decision record are in
`docs/architecture.md`.

- **Backend**: ASP.NET Core on `net10.0` (`src/`). See "Conventions" for the
  TFM and why it moved.
- **API surfaces**: REST (minimal-API endpoints) **and** GraphQL
  (HotChocolate, `src/GraphQL/`, served at `/graphql`). Both sit on the same
  data layer — GraphQL didn't replace REST. Added in Phase 2b. This dual
  surface is a *portfolio* choice, not an industry norm: no comparable
  product ships a public API. Don't imply otherwise.
- **Storage**: **PostgreSQL via EF Core** (Phase 2). Since Phase 13.3b the schema
  is **20 tables in six Postgres schemas** (13 of them, in five schemas, until
  Phase 11.1a added `identity` and Identity's seven), one per table-owning module, each with
  its own `DbContext`, its own Fluent API configuration (`<Module>/Persistence/`),
  its own migrations (`<Module>/Migrations/`) and its own `__EFMigrationsHistory`.
  **Seven foreign keys, not thirteen** — the six that crossed a schema are enforced
  in application code since 13.3c. An earlier draft of Phase 2 used DynamoDB; that
  was dropped in favour of a normalized relational model — see the Phase 2 doc.
- **AI calls**: behind `Microsoft.Extensions.AI`'s `IChatClient` (Phase 4), so
  Ollama (local, free) and a hosted API are swappable via config, not code
  changes. Registered in `src/Shared/ModelClient.cs`, **not** in the Ai module —
  Phase 4.5 made Documents a second caller, and the rule that produced is that
  **`Ai` owns the `ai_analyses` table, not the technology**. `IChatClient` is a
  shared dependency any module may inject, like `AppDbContext`.
- **Deployment target**: **undecided — AWS was dropped 2026-09-04** (decision 22).
  The API is a container (`src/Dockerfile`, `compose.yaml`) and no hosting-specific
  code was ever written, so the drop cost zero source changes and the next choice
  costs none either. The constraint that survives: **nothing may bill per hour**,
  which is what rejected RDS, Aurora and a NAT Gateway. Neon's free tier is still
  the leading candidate for the database. `docs/phases/phase-10-aws-deploy.md` keeps
  the Lambda/Function-URL research as a dated answer to "why not AWS".

### Where new code goes

**Rewritten at Phase 13.6 (2026-09-03), when the shape stopped moving.**
`src/` is **eleven projects**: seven modules, plus SharedKernel, Contracts,
Persistence and Api. (It was ten until Phase 11.1a added `Jobkeep.Modules.Identity`
on 2026-09-04.)

```
src/Jobkeep.Modules.<X>/Application/<UseCase>.cs   request + handler + response, together
src/Jobkeep.Modules.<X>/Domain/                    entities, enums, pure rules
src/Jobkeep.Modules.<X>/Persistence/               <X>DbContext + Fluent API config
src/Jobkeep.Modules.<X>/Infrastructure/            this module's own contract impl
src/Jobkeep.Modules.<X>/Migrations/                its schema, its own history table
src/Jobkeep.Modules.<X>/<X>Module.cs               DI registration
src/Jobkeep.Contracts/<X>/                         how OTHER modules reach this one
src/Jobkeep.SharedKernel/                          SliceResult, NaturalKey, IAuditable
src/Jobkeep.Api/Controllers/<X>Controller.cs       the routes
src/Jobkeep.Api/GraphQL/                           Query.cs, Mutation.cs
src/Jobkeep.Api/Program.cs                         wiring only
```

Modules: `Applications` (core), `Analytics` (read-only), `Ai`, `Documents`, `Match`,
`Skills`, `Identity` (Phase 11, now **last** on the roadmap).

**A new slice is one file plus two lines**: register the handler in `<X>Module.cs`,
add an action to `<X>Controller.cs`. `Program.cs` only ever calls `Add*Module()`;
routing is one `MapControllers()`.

**Five rules that matter:**

- **A slice owns its use case end to end.** The handler takes **its own module's**
  `DbContext` directly, validates in the handler (not at either edge), and returns a
  **response DTO**, never an EF entity. EF's `DbContext` is already a unit-of-work
  plus a repository; wrapping it adds a layer that mostly forwards calls. Don't
  reintroduce a repository, a service layer or an `Api/Endpoints/*.cs` file — all
  three existed and all three were deleted.
- **Every crossing between modules goes through `Jobkeep.Contracts`, reads
  included.** There is no project reference to cross with, so this is the compiler's
  rule, not a convention. It **reverses decision 17** ("reads are free"), which was a
  correct answer to *is this safe?* and the wrong answer to *can this module be
  lifted out?* — a `SELECT` across a boundary is exactly what stops working when the
  boundary becomes a network.
- **Whether a method belongs on a contract is a question about the method, not a
  count.** Does it name a **fact about the thing**, or a **question the caller has
  about its own feature**? The second kind stays with the caller. That test replaced
  `IPostingContract`'s old two-method cap, and it is why the ATS skill gap never
  became a fifth method.
- **`Domain/` knows nothing of EF or of the rest of its module.** Entities are the
  layer that survives an extraction unchanged. Mapping goes in `Persistence/`,
  behaviour in a slice. Pinned by `Architecture/LayeringTests.cs`.
- **A namespace begins with the name of the project that holds it.** Also pinned by
  `LayeringTests`. Not cosmetic — before 13.6, `Jobkeep.Modules.Skills` named both
  the module and Contracts, and that is how `DispatchTests` came to check none of
  Skills' handlers behind a line that compiled and passed.

**A cross-module CASCADE is a notification; a cross-module RESTRICT is a contract
check.** The asymmetry is the point: a cascade is a consequence, announced *after*
the publisher commits; a restrict is a question, asked *before*. Publish after
`SaveChangesAsync`, never before — on failure that leaves an invisible orphan rather
than destroying work on a row that survived. There is **no outbox**; that is written
down, not forgotten, and it is Phase 15's.

**`ISkillCatalog.FindOrCreateAsync` SAVES** — call it before adding anything of your
own to the change tracker, or a failure after its save leaves the skill rows
committed and yours not. `CommitImport.CommitResumeAsync` gets the order right and
says why at length.

Two files in `Applications/Application/` are shared by several slices, and the
distinction matters because it is the one the repository got wrong:
`ApplicationDetail.cs` holds the detail response records plus the EF projection;
`CompanyLookup.cs` holds one find-or-create. **Neither owns *access*** — every slice
still writes its own query, and a slice needing something different writes it rather
than growing these. Prefer a flat `.Select(...)` projection to `Include(...)`; an
include graph is how the over-fetch in A1 got there.

### Superseded rules, kept so the reversals stay legible

Each of these was a rule in an earlier version of this file, and each was reversed
for a reason worth being able to say out loud:

- *"Never bypass `IJobApplicationRepository`"* — the interface is deleted
  (Phase 2.3, decision 5).
- *"Keep `Program.cs` as the single place endpoints are defined"* — routes moved to
  modules, then to controllers (Phase 13.5, decision 7, which also **reversed** the
  standing recommendation to avoid controllers).
- *"A module only queries the tables it owns"* → *"only a **write** needs a
  contract"* (Phase 5, decision 17) → *"**every** crossing needs a contract"*
  (Phase 13, decision 19). The middle version is the one to stop quoting.
- *"`skills` is owned by nobody, deliberately"* — it is the `Skills` module's, since
  Phase 13.2. A table with no owner has no context and no schema.

The Phase 2.1-2.3 slice migration and the Phase 13.1-13.3b project split are both
**finished**. `Endpoints/`, `Repositories/`, `Models/Dtos.cs`, `AppDbContext` and
`Jobkeep.Infrastructure.Data` no longer exist — don't go looking for them, and don't
recreate them.

## What good looks like here

- **Aggregate in SQL, not in memory.** EF `GroupBy` that translates to a
  real `GROUP BY` — never load a table and count in C#.
- **DTOs at the edge.** Don't return EF entities from endpoints or resolvers;
  the API contract shouldn't move every time the schema does. `Program.cs` used
  to set `ReferenceHandler.IgnoreCycles` to paper over the navigation cycles that
  caused — Phase 2.3 removed it. If it ever needs to come back, something is
  returning an entity again.
- **One rule, one implementation.** Validation and business rules live in the
  slice, so REST and GraphQL can't enforce different things.
- **GraphQL should fetch what was asked for.** The eager-loaded include graph
  is gone (Phase 2.3) but projection is still per-DTO, not per-field — see
  `architecture.md` A1 and decision 11 for why the obvious fix was refused.
- **Write down the tradeoff.** Existing comments explain *why* (captive
  dependency, `AsSplitQuery`, delete behaviour). Match that density; it's the
  interview material.
- **A phase ships with its tests.** Since Phase 2.2 there is a suite, so new work
  extends it in the same change rather than leaving it for later — that is the
  whole reason tests were pulled ahead of the remaining features. Prefer an
  integration test through the real surface over a unit test with a fake; the
  bugs this project actually has (SQL that doesn't translate, delete behaviour,
  one rule enforced on one surface only) are invisible to fakes. A pure domain
  rule with no database in it — the Phase 2.5 status lifecycle is the only one so
  far, in `tests/Jobkeep.Tests/Domain/` — is the exception, and gets a plain unit
  test.

## Documenting as you go

**Adopted 2026-08-26** (`architecture.md` decision 12), replacing a practice of
refreshing every standing doc every phase. Three tiers.

**Always, in the same change — these are near-free, and they are the evidence:**
- In-code comments explaining a *tradeoff*. The context is already loaded, so
  they cost almost nothing, and they are the interview material.
- The phase doc's `Status`, and its real deviations from the plan. Write these
  while the work is fresh — they are not recoverable later, and they are where
  the STAR stories come from.
- Tests (see "A phase ships with its tests").

**Only when the change made it wrong.** For `architecture.md`, `CLAUDE.md`, the
two READMEs and the audit, the test is *factually wrong* — not *doesn't mention
this yet*. A doc naming a file the change deleted gets fixed. A doc that is
merely silent about the new feature does not.

So, concretely, do **not**: re-read the finding tables (A1-A9, F1-F18) for
findings the change didn't move; rewrite prose that is still true so it names the
new phase; regenerate a diagram "to be safe"; or open a doc just to check it.
Prefer putting the detail in the **phase doc** — one accurate place beats four
half-synchronised ones.

**On a cadence, never per feature.** Doc audits and consistency sweeps run at
phase-group boundaries only — before whatever deploy replaces the dropped Phase 10,
and before Phase 6
— and **always in a fresh session**. The cadence and the session boundary do the
saving together: the same sweep late in a long session costs roughly 3x what it
costs early.

The accepted cost, stated plainly: between sweeps the standing docs will lag what
the code does. That is tolerable because the ones that would be actively *wrong*
get fixed immediately, and because a stale sentence is cheaper than the sweep
that would have prevented it.

### Frozen until 1.0 ships on master

**Adopted 2026-09-02, at the user's instruction, after Phase 13.3c spent a
meaningful slice of a session redrawing two SVGs.** Until **1.0 is merged to
master**, do not spend tokens on regenerated or re-rendered artefacts. Then redraw
once, deliberately, and only what is actually needed.

Frozen — do NOT produce these mid-phase, even when a change makes them wrong, and
even when a phase doc or a skill says to:

- **`docs/diagrams/*.svg`.** Both of them. The `schema-diagram` skill is not to be
  invoked; a migration or a module-boundary move is no longer a trigger.
- **Any other picture, chart or rendering** — no ASCII diagrams "to illustrate", no
  Mermaid, no artifacts, nothing generated to be looked at rather than run.
- **Re-reading or re-syncing standing docs a change did not touch.** This was
  already the rule (decision 12); it is restated here because it is the same money.

Still done in the same change, unchanged — these are near-free and they are the
evidence:

- In-code comments explaining a tradeoff.
- The phase doc's Status and its real deviations.
- Tests.
- A doc sentence that the change made **factually wrong** — a one-line fix, not a
  sweep.

**When a change would have triggered a redraw, write one line in the phase doc
saying so** (e.g. *"schema moved; diagrams deliberately not redrawn — frozen until
1.0"*). That keeps the debt visible and makes the eventual redraw a list rather than
an investigation.

**1.0 is the trigger, and it is a merge to master, not a phase number.** At that
point the accumulated list gets redrawn in one fresh session — which is also the
cheapest place to do it, per the cost table above.

## Commands

**`docker compose up --build` starts the whole local stack** — Postgres, the API
and the front end, three containers, `compose.yaml` at the root plus
`src/Dockerfile` and `web/Dockerfile`. It needs nothing installed but Docker.
`docker compose down` stops it, `down -v` drops the database, `logs -f api`
follows one service.

- The front end is the **real Vite dev server** with `./web` bind-mounted — but
  **HOT RELOAD DOES NOT FIRE FOR EDITS MADE ON THE HOST** (observed 2026-09-03: a
  `web/src/styles/*.css` edit did not reach the browser, and survived a
  ctrl+shift+R hard reload; only `docker compose restart web` picked it up). The
  bind mount carries the bytes; it does not carry the inotify event from Windows
  into the Linux container, and `vite.config.ts` sets no `server.watch.usePolling`.
  **So after editing anything under `web/`, run `docker compose restart web`** — or
  add `usePolling` if this becomes a per-minute cost. Do not debug a stale screen as
  a code bug; it took three wrong turns the first time. The API is a **published
  build**, so a C# edit costs
  `docker compose up --build api`. That asymmetry is deliberate — `dotnet watch`
  over a bind mount would drag the host's Windows `obj/` into a Linux container.
- Adding an npm dependency also needs `up --build`: an anonymous volume masks
  `node_modules` so the container keeps its Linux packages.

**There was a second launcher, `run.cmd` / `scripts/run.ps1`, and it was deleted
on 2026-09-01** at the user's instruction — one way to start the app instead of
two that bind the same three ports (:5432, :5080, :5173). Don't reintroduce it,
and don't look for it in docs that predate the removal. Two things it used to do
now have to be done by hand:

- It killed the stray `Jobkeep.exe` that makes the next build fail with **MSB3027**
  ("Exceeded retry count of 10 … locked by Jobkeep"). That trap is still live for
  anyone running `dotnet run` directly — stop the process before rebuilding.
- It waited for each layer to answer before starting the next, so a failure named
  the layer that failed. Compose has a `pg_isready` healthcheck on `db` and
  `depends_on: service_healthy` on `api`, which covers the race that actually
  mattered; the front end has ordering only, deliberately, since the browser is
  what calls the API.

The compose stack keeps its rows in the `pgdata` volume. **The old `jobkeep-db`
container from `run.cmd` may still exist on this machine with different data in
it** — `docker rm -f jobkeep-db` if it gets in the way of :5432. The test suite is
in neither: it starts its own throwaway Postgres via Testcontainers.

**Ollama is deliberately NOT in compose.** It runs on the host; the API container
reaches it at `host.docker.internal:11434` (`Ai__Endpoint` in `compose.yaml`).
`ollama serve` + `ollama pull llama3.2:3b` are prerequisites for the three model
callers, and nothing else in the app needs them. See "Where the model runs" in
the root `README.md` for which caller degrades and which fails.

By hand:

```bash
# Start local Postgres first — the app auto-migrates against it on startup
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=jobkeep postgres:16-alpine

cd src
dotnet build Jobkeep.slnx
dotnet run --project Jobkeep.Api
```

Since Phase 13.1 there are several projects under `src/` (**eleven**, as of 11.1a), so a
bare `dotnet build` / `dotnet run` in that directory fails with **MSB1011** — name the
solution or the project, as above. `docker compose up --build` is unaffected and remains the
normal way to start the stack.

App listens on `http://localhost:5080` (`Jobkeep.Api/Properties/launchSettings.json`).
Swagger UI at `/swagger` and the GraphQL Nitro IDE at `/graphql` — both
Development-only.

EF migrations (tool pinned to 10.0.11 in `src/dotnet-tools.json`, `rollForward: false`
— bump it in lockstep with `Microsoft.EntityFrameworkCore.Design`):
```bash
dotnet tool restore

# Since Phase 13.1 the model lives in one project and the connection string and
# provider are resolved through another, so both have to be named. Since 13.3b there
# are SIX models — one per table-owning module, each with its own schema and its own
# __EFMigrationsHistory — so the context has to be named too. Without all three flags
# the command fails in a way that reads like a broken tool install.
dotnet ef migrations add <Name> \
  --project Jobkeep.Modules.Documents \
  --startup-project Jobkeep.Api \
  --context DocumentsDbContext

# The six, with their schemas: ApplicationsDbContext (applications),
# SkillsDbContext (skills), DocumentsDbContext (documents), AiDbContext (ai),
# MatchDbContext (ats — the SCHEMA kept its old name on purpose; see
# MatchResultConfiguration.cs) and, since Phase 11.1a, IdentityDbContext
# (identity). AnalyticsDbContext owns no tables and no migrations.

# The cheap check that a refactor did not move the schema. Run it per context.
dotnet ef migrations has-pending-model-changes \
  --project Jobkeep.Modules.Documents \
  --startup-project Jobkeep.Api \
  --context DocumentsDbContext
```

Tests (Phase 2.2) — xUnit v3 + Testcontainers, in `tests/Jobkeep.Tests/`:
```bash
cd src
dotnet test --project ../tests/Jobkeep.Tests/Jobkeep.Tests.csproj
```
Docker must be running. The suite starts its **own** `postgres:16-alpine` container
and never touches the dev database — `PostgresFixture` refuses to run if the app
ever resolves a connection string other than the container's. There is no
`compose.yaml`; Testcontainers manages its own database.

CI (`.github/workflows/ci.yml`) runs the same suite on every branch push, from the
repo root rather than `src/`. Since Phase 2.6 it installs **one** SDK (10.0.x —
it both runs the `net10.0` tests and reads `global.json`) and builds
`src/Jobkeep.slnx`, which **includes the test project** — that's why its test step
can pass `--no-restore`.

`dotnet test` needs the **repo-root `global.json`** (`test.runner` =
`Microsoft.Testing.Platform`) and the **.NET 10 SDK** to read it — xUnit v3's runner
is MTP, and the .NET 10 SDK refuses the old VSTest bridge. Pass the project as
`--project <path>`, not positionally. No SDK version is pinned in `global.json`,
so the `dotnet-ef` tool pin is independent of it.

Front end (Phase 6) — Vite + React in `web/`, and `npm` is run from there:

```bash
cd web
npm run dev     # http://localhost:5173 — expects the API already up on 5080
npm test        # Vitest, 35 tests, ~2s. No container, no API needed
npm run build   # tsc -b && vite build
npm run lint    # oxlint
```

`npm run dev` makes a **genuine cross-origin request** to the API — there is no
dev-server proxy, deliberately, so the CORS policy is exercised from day one
rather than first met on deploy. Nothing in `web/` needs Docker.

You can still exercise endpoints by hand via Swagger, the Nitro IDE, or
`src/Jobkeep.http` (works in VS / VS Code / Rider, no account needed).

## Gotchas

- **Keep `src/` to one solution file.** Visual Studio has twice recreated a
  stale `JobTracker.slnx` (it points at a `JobTracker.csproj` that doesn't
  exist). With two `.slnx` files present, bare `dotnet build` / `dotnet run`
  fail with `MSB1011: Specify which project or solution file to use`. Delete
  the stray file, or pass `Jobkeep.slnx` / `Jobkeep.csproj` explicitly.
- **Migrations auto-apply on startup in Development only** (`Program.cs`).
  Deployed environments are expected to apply them as a deliberate release step.
- **Enums serialize by name**, not int, and are stored as strings —
  `"Interviewing"` over REST, `INTERVIEWING` over GraphQL.
- **`ReferenceHandler.IgnoreCycles` is gone** (Phase 2.3), and should stay gone.
  It was load-bearing only because endpoints returned EF entities whose navigation
  properties cycle (posting ↔ skills). Responses are DTOs now. Needing the flag
  back means something is returning an entity.
- **`appsettings*.json` contain `//` comments.** ASP.NET's config reader accepts
  them; strict JSON parsers won't. Don't "fix" them.
- **Never put `[FromForm]` on an `IFormFile` parameter.** Swashbuckle 10 refuses
  an action carrying both, and it refuses by *throwing*, so one unrepresentable
  route makes `GET /swagger/v1/swagger.json` answer 500 and Swagger UI show "Fetch
  error" for **every** endpoint. A minimal API binds `IFormFile` from the
  multipart body without the attribute; the scalars beside it still need it, or
  they bind from the query string instead. This shipped in Phase 4.5 and went
  unnoticed for two phases — `tests/Jobkeep.Tests/Documents/SwaggerDocumentTests.cs`
  now pins it.

## Conventions

- Target framework: `net10.0` (LTS through **Nov 2028**), moved from `net8.0`
  in Phase 2.6 ahead of the .NET 8 EOL on 10 Nov 2026. `src/` and
  `tests/` share the TFM and move together.
- Nullable reference types enabled — respect existing nullability
  annotations rather than suppressing warnings.
- Keep new NuGet dependencies minimal and justify additions in the
  relevant phase doc.

## Known gaps (don't re-discover these)

No health check. **Auth came off this line on 2026-09-05** — Phase 11.1b gave it
a sign-in and 11.2a made every route require one; what is still missing is
*scoping*, which is 11.2b. The rest is **recorded**, not forgotten — see the gap
register in `docs/architecture.md`. (`docker-compose` used to be on this line;
`compose.yaml` landed 2026-09-01.)

Tests and CI landed in **Phase 2.2**, scheduled straight after 2.1 because the gap
register called them the highest-value missing items. Findings still **recorded, not
fixed** — don't re-discover them:
- ~~**Skill dedup is case-sensitive**~~ — **FIXED in Phase 7.** All three tables
  (`skills.Name`, `companies.Name`, `resumes.Label`) now carry a STORED generated
  column, `lower(...)`, and the unique index sits on that. Use
  `Jobkeep.Shared.NaturalKey.Of(name)` before any find-or-create — **the C# side and
  the generated column must agree**, or a lookup misses a row the index then refuses
  to insert, and the user gets a 500 on an ordinary name. Two tests asserted the
  defect and both broke on the migration, which is exactly what writing defects down
  as tests is for; both were flipped in place. Résumés are **not** merged — duplicate
  labels were suffixed, because two documents are two documents.
- **The EF version asymmetry survived Phase 2.6 — it was never about the major
  version.** The app resolves EF 10.0.11 throughout, but the Npgsql provider
  declares a *range* (`[10.0.4, 11.0.0)`) while EF Design pins an *exact* 10.0.11.
  A transitive-only reference therefore lands on the floor, so
  `Jobkeep.Tests.csproj` must keep naming `Microsoft.EntityFrameworkCore` and
  `.Relational` explicitly; removing them fails with CS1705. Bump those two in
  lockstep with EF Design. Reasons are in the csproj comment — read it first.
- ~~**No index on `Status` or `DateApplied`**~~ — **FIXED in Phase 7**, along with
  F7 (`xmin` concurrency), F8 (the audit interceptor), F11 (DB-side defaults), F12
  (CHECK constraints) and F13 (bounded text). The one rule worth carrying out of it:
  **`UpdatedAtUtc` records when THAT ROW changed, not when anything beneath it did.**
  Adding a skill to a posting does not bump the posting — the interceptor only sees
  entities EF marks `Added`/`Modified`, and stamping parents would need an aggregate
  definition this codebase has never written down.

**Fixed in Phase 2.3, so don't re-report them:** A2 (entities as the API contract),
A3 (the repository), A4 (surface-specific validation) and A7 (EF entities reachable
through the GraphQL schema). A1 is *partly* fixed — read decision 11 before
"finishing" it, because the obvious fix reopens A7.

## Before you explore

Two logs exist so that work already paid for is not paid for twice. **Read them
before spawning a subagent or picking a tool**, not after:

- `docs/agent-log.md` — every subagent exploration run on this repo, with its
  findings compacted. **Check it before spawning an agent.** If an entry covers
  your ground, read it and verify only the specific facts your change depends on
  — a `grep` for one symbol costs a thousandth of an agent. Spawn only for ground
  no entry covers, and **add a row when it returns.** The standing rule is
  unchanged: do not spawn subagents unless the user asks.
- `docs/tool-usage.md` — which tool is right for which job here, and the traps
  that have already cost a turn (heredocs through Bash, non-persistent `cd`,
  escaped backslashes in the markdown, CRLF+BOM in EF's SQL, `pg_dump` over
  `migrations script`). **Check it before a bulk edit or a schema derivation.**

Both carry dates. A finding that names a file, a line or a constant is a claim
about that date — verify it before relying on it.

## Where things are

- `docs/README.md` — the index: what each doc is for, and which wins.
- `docs/agent-log.md` — subagent runs and their compacted findings. Read before
  spawning one.
- `docs/tool-usage.md` — tool selection and the known traps. Read before a bulk
  edit.
- `docs/architecture.md` — how the code is shaped, why, and the decision
  record. **Check this before proposing structural changes.**
- `docs/phases/phase-N-*.md` — the plan and status for each build phase, in
  order. Check the current phase's doc before making changes so new
  work matches the intended scope for that stage.
- `docs/phases/phase-6.5-upload-experience.md` — **read this before touching the
  upload flow instead of re-exploring it.** It already records the 180-second
  synchronous model block, the server-side filename label default, the drop zone
  the screen never had, the `stubFetch` throw, the `Description` cap that 500s,
  and the design pass's eight defects.
- `docs/phases/phase-6.6-the-ad-goes-somewhere.md` — **read this before touching
  the add form, the Job post screen or anything that asks where the ad text
  lives.** The one fact worth having in advance: **`job_postings.Description` is
  the ad and `job_applications.Notes` is your commentary, and only `Description`
  is read by anything.** The analyser, the ATS check and the extractor all read
  `Description`; nothing reads `Notes`. Phase 6.3 wired the add form's only
  textarea to `Notes`, which is why a pasted advertisement produced no skills.
- `docs/security-and-data-audit.md` — schema/config exposure, F1-F18, and the
  phased remediation plan. **Refresh on a cadence, not per phase:** once before
  whatever deploy replaces the dropped Phase 10 ships, and once before auth lands
  (Phase 11, now last). Those are the points where a stale finding would actually
  cost something.
- `docs/user-journeys.md` — what the user actually does, step by step, and where
  that procedure has holes. The counterpart to `architecture.md`: that one
  describes the system from the code's side, this one from the user's.
- `docs/backlog.md` — considered-but-not-committed features, and the
  verified market comparison.
- `docs/token-log.md` — what each phase cost to build, in tokens. Regenerate
  with `python scripts/token-usage.py`; see "When asked to move to the next
  phase" below.
- `docs/diagrams/` — `schema-erd.svg` and `architecture.svg`, embedded in
  `README.md` and `docs/architecture.md`. **DO NOT REDRAW THESE UNTIL 1.0 IS ON
  MASTER** — see "Frozen until 1.0" below. The per-change trigger that used to live
  here is suspended, deliberately; both files will lag the code and that is the
  accepted cost.
  When they are eventually redrawn, use the `schema-diagram` skill
  (`.claude/skills/schema-diagram/`) and note the one method rule worth keeping:
  derive the schema from `pg_dump --schema-only` against the migrated database, not
  from `dotnet ef migrations script` (a sequence, not a final state) and never from
  reading the entity classes — column types, precision, delete behaviour and index
  uniqueness live in Fluent API config and the Npgsql provider.
- `scripts/token-usage.py` — reads Claude Code's session transcripts and totals
  tokens per session, or per task within a session (`--task <prefix>`). The
  source for `docs/token-log.md`.
- `src/` — the actual .NET project.
- `web/` — the React front end (Phase 6). Its own `README.md` covers the layout;
  the rules for where front-end code goes are in
  `docs/phases/phase-12-feature-expansion.md`.
- `PRODUCT.md` — the binding brand commitments and the measured contrast table.
  **Read before touching UI**; the palette and the tone rules are decided there,
  not re-derived per screen.
- Root `README.md` — status table and quick start.

## When asked to move to the next phase

**PHASE 14 IS DONE (2026-09-03) — the skill vocabulary.** One migration
(`SkillKindAndAliases`, `skills` schema), suite 268 → 281, seed of 228 skills and
322 aliases. `docs/phases/phase-14-skill-vocabulary.md` has the whole record; five
things from it change how new code is written:

- **`skills.skill_aliases` exists and `SkillCatalog` resolves through it. Skills
  first, aliases only on a miss** — so an alias colliding with a real skill row is
  inert. **No call site changed and none should start**: reaching for the alias
  table outside `SkillCatalog` is the same bug as reaching for `NaturalKey.Of`.
- **`SkillKind { Unknown, Technical, Soft }` is in Contracts** (`SharedEnums.cs`),
  a THIRD shared enum, for the same GraphQL reason as the other two. It is a
  SECOND AXIS — `Category` still means the family, and C# is Technical *and* a
  Language. Advisory on create like `Category`: **first writer names it**, which
  is how the seed corrected the model calling Scrum a soft skill.
- **The vocabulary is `src/Jobkeep.Modules.Skills/skills-seed.json`**, embedded,
  applied idempotently at startup. Edit the file and restart; no migration.
  **An alias must be a SYNONYM, not a relative** — `Docker Containers` → `Docker`
  yes, `PostgreSQL` → `SQL` never. A wrong alias fails invisibly, by claiming a
  match the CV has not earned.
- **`Skills:SeedOnStartup` is false under test only** (`JobkeepAppFactory`).
  Respawn truncates between tests, so a seeded catalogue would re-materialise into
  every unrelated arrange. Not a knob — a test seam.
- **A model returns PHRASES unless told not to.** `"Excellent communication
  skills"`, `"CI/CD pipelines"`. Fixed in the `[Description]` — *"the name of the
  skill itself, not the sentence it appears in"* — not with more aliases. A
  catalogue cannot alias its way out of an open set of sentence fragments.

**THE MATCH-CHECK RENAME LANDED 2026-09-04** (suite 285, one migration,
`RenameAtsResultsToMatchResults`). The feature called "ATS check" was mostly a
CV-vs-one-ad comparison, which the industry calls a *match rate*. It is now
`Jobkeep.Modules.Match` / `Jobkeep.Contracts.Match`, table `match_results`, routes
`POST`/`GET /applications/{id}/match-check`, GraphQL `runMatchCheck` / `matchResult`,
and `web/src/routes/MatchCheck.tsx`. Three things worth not re-deriving:

- **The Postgres SCHEMA is still `ats`, deliberately.** It holds its own
  `__EFMigrationsHistory` (`Program.cs` puts each module's history in its own schema),
  and EF resolves that history table *before* it applies anything — so a migration that
  renamed the schema would leave EF looking in `match` for a history table still in
  `ats`, conclude nothing had been applied, and re-run `InitialCreate` against tables
  that already exist. Argued at length above the `ToTable` call. `match` was checked
  and is a legal unquoted Postgres identifier; that is not what stopped it.
- **The rename is NOT the split.** The four stages are unchanged, so the
  parseability half is still one stage of the comparison feature. "ATS check" is now a
  free name waiting for it, and it stays a `docs/backlog.md` row.
- **Genuine-ATS prose survived on purpose.** Lines like *"the biggest ATS risk in that
  document was never keyword coverage"* and *"An ATS reads the same text this did"* are
  about a real applicant tracking system and were hand-classified out of the rename;
  `AtsEndpoints.cs` likewise stays as a historical filename in a comment. Don't sweep
  them.

**PHASE 13 IS DONE (2026-09-03). 13.6 landed with it** — namespaces, this file's
"Where new code goes", `architecture.md` sections 1-3 and decisions 5, 6, 7, 12, 13,
15, 17 superseded in place, plus decisions 19-21 added. Suite 283 → 285. No
migration; **no schema moved, so EF reports no drift** — the five model snapshots
name entity types by CLR full name and were rewritten with the rename, which is the
one trap in that step. Two rules from it are in "Where new code goes" above; the
whole record is `docs/phases/phase-13-clean-architecture.md` §13.6.

**THE ROADMAP CHANGED ON 2026-09-04 — no code, docs only** (`architecture.md`
decision 22). **Phase 10, the AWS deploy, is DROPPED**; a free host is to be
chosen later. **Phase 11, auth, moves to LAST** — after 8, 9 and 12 — and **keeps
its number**, which knowingly breaks decision 18's "numbers are build order".
Three questions were settled at the same time and are recorded in
`docs/phases/phase-11-auth.md` so they are not re-asked: **decision 9 is CONFIRMED**
(`skills` stays global; status moved *Proposed* → *Accepted*), auth will use
**ASP.NET Core Identity in full** (one new package, approved — chosen over the
smaller hand-rolled option **the ponytail ladder argued for**, deliberately, for
the platform answer an interviewer expects), and there is **no third-party login**.

**PHASE 9 IS DONE (2026-09-04)** — all three gaps, no migration in any of them,
suite 314 → 332 and web 52 → 55. Gap 2 is the status *set*, gap 3 is
`GET /applications/board`, and **gap 1 put the "CV match" column back on the
Applications screen**. The one rule from gap 1 worth not re-deriving: **a stored
match summary on a list row is a CONTRACT CALL, not a projection.** The plan said
it was "legal under decision 17 and needs no contract"; Phase 13 reversed that, so
it is `IMatchContract.GetSummariesAsync(ids)` — batched over the page, keyed by
application id, **absent means never checked**. It passes `CLAUDE.md`'s own test
because it names a *fact about the row* (`match_results` is 1:1 with an
application and already stores the three lists), which is the same reason
`CountResultsForResumeAsync` belongs there and the opposite of the ATS skill gap,
which stayed with its caller. `MatchSummary` is two integers on purpose — the
keyword lists, the date and the warning are all left off, and the column
deliberately cannot tell a stale check from a fresh one.

**PHASE 11 IS IN PROGRESS — started early, at the user's instruction on
2026-09-04.** The phase doc's gate said *"this ships before whatever deploy
replaces Phase 10, and not before"*; that is an argument about when auth becomes
load-bearing, **not a dependency** — nothing in the phase needs a host, only the
CORS named origin does, and that is config. It is **split into six runnable
sub-steps** (11.1a, 11.1b, 11.1c, **11.2a**, **11.2b**, 11.3) in
`phase-11-auth.md` — 11.2 was split at 11.2a, along the line between *"is anyone
there"* and *"whose row is this"*.

**11.2a LANDED 2026-09-05: NOTHING IS REACHABLE WITHOUT SIGNING IN.** Suite
338 → 341, nine files, no migration, no `web/` change. Five things from it:

- **Every controller carries `[Authorize]` and GraphQL is behind
  `app.MapGraphQL().RequireAuthorization()`.** Not a fallback policy — it would
  also catch `/identity/login`, and the escape hatch (`AllowAnonymous` on the
  identity group) collides with the `RequireAuthorization()` those framework
  routes already carry. Not `[Authorize]` on resolvers either: that needs
  `HotChocolate.AspNetCore.Authorization`, a new package, for per-field policies
  this app has no use for.
- **The guard is `AuthorizationTests`, and it reads the app's own
  `EndpointDataSource`** — every endpoint outside `/identity/` must carry
  `IAuthorizeData`. So a sixth controller, a minimal API or a second GraphQL
  mount are covered on the day they are added. Two more tests ask **both
  surfaces** over the wire, because metadata is a declaration and only a request
  proves enforcement.
- **`TestAuthHandler` is how the other 338 tests still pass** — an
  `X-Test-User` header becomes a principal, registered by `JobkeepAppFactory`
  only. **Its forward target is Identity's COMPOSITE default scheme, not the
  application cookie**: the cookie alone REDIRECTS an unmet challenge to
  `/Account/Login` (a Razor page this app lacks), where the composite answers
  401. Getting that wrong made the suite see a 404 where the app sends a 401, and
  cost a wrong "fix" to `Program.cs` before the cause was found.
- **ANTIFORGERY WAS RE-READ AND STAYS OFF.** What holds it off is now
  `SameSite=Lax`, which refuses to attach the cookie to any cross-site
  POST/PUT/DELETE — the upload included. **Its expiry date is the cookie's own**:
  the change that sets `SameSite=None` for a cross-site deploy is the change that
  must add antiforgery tokens, not a later one. Both notes are in `Program.cs`.
- **Swagger UI and `swagger.json` stay open**, deliberately — Development-only,
  and they are what a person reads in order to sign in. They are middleware, not
  endpoints, so the endpoint test does not see them; that is luck, not an
  exemption someone should "fix".

**11.2b is next: `OwnerUserId`, the query filter, the re-scoped slices, the three
published views re-cut.** Note `HasQueryFilter` does not reach raw SQL, and the
five existing `IgnoreQueryFilters()` call sites would drop an owner filter too —
EF 10's **named** query filters are the way out.

**11.1c LANDED 2026-09-04**: **the front end is behind the sign-in.** Web suite
55 → 62, five files, no backend change but a comment. Five things from it:

- **The sign-in is NOT A ROUTE**, deviating from the plan on purpose. `App` holds
  one `Account | null | undefined` and renders `<SignIn>` *instead of* the shell
  when signed out, whatever the address — so signing in lands you on the page you
  opened, with no `?returnUrl` and no code to carry one. **`undefined` is the
  third state and it matters**: the cookie is `HttpOnly`, so "am I signed in" is a
  round trip, and the first paint waits rather than flashing the wrong screen.
- **The 401 handler lives in `request()`** (`onUnauthenticated`, a module-level
  slot `App` fills), not in eight screens. A session expires between requests,
  not between screens. `ApiError.isUnauthenticated` exists for the ONE place that
  must tell 401s apart: the form, where it means *wrong password*.
- **`request()` had two latent bugs this found.** A **200 with no body threw** —
  `res.json()`'s `SyntaxError` escapes the fetch try/catch, so a successful login
  would have said "Could not reach the API"; it reads text first now. And
  **`ValidationProblemDetails` was read wrong** — the sentence is in `errors`,
  `title` is "One or more validation errors occurred.".
- **A hint inside a `<label>` becomes part of the label.** `.field-hint` as a
  `<span>` in `.field` made the input's accessible name "Password At least six
  characters, with an…". Use `aria-describedby` — and note `.field-hint` is used
  that way elsewhere in `web/`.
- **`SameSite` is `Lax` and that has an expiry date.** `:5173` → `:5080` is
  cross-origin but same-site, so it works. A deployed front end on another domain
  is cross-site and the cookie **silently stops being attached**. The fix is
  `SameSite=None`, which needs `Secure`/HTTPS and so cannot be set while local dev
  is http. Argued in `Program.cs`.

**Registration is OPEN** — right for localhost, first thing to close when a host
is chosen (`ponytail:` note on `register` in `web/src/lib/api.ts`).

**11.1b LANDED 2026-09-04**: **you can register and sign in.**
`AddIdentityApiEndpoints<JobkeepUser>()` + `MapIdentityApi<JobkeepUser>()` in a
`/identity` group, plus a hand-written `/identity/logout`. Suite 332 → 338, no
migration. Four things from it change how new code is written:

- **`MapIdentityApi` is a DELIBERATE exception to "every route is a controller
  action"** (13.5). The routes are the framework's; re-typing them as a
  controller would re-type password hashing, lockout and the token flows, which
  is the work the package was chosen to avoid. **Do not "fix" it into a
  controller**, and do not read it as licence to add hand-written minimal APIs.
- **The CORS trap cost ONE LINE — `.AllowCredentials()`.** Phase 6.1 had already
  refused `AllowAnyOrigin` and named this as the reason, so the origin list was
  already explicit. Both the plan and the handoff budgeted a named-origin policy
  as work in this step; there was none.
- **`UseAuthentication`/`UseAuthorization` are written out on purpose**, after
  `UseCors`. A preflight `OPTIONS` carries no cookie, so authorization first
  refuses it before CORS answers — and it shows up in the browser as a CORS
  error with nothing server-side to explain it.
- **`AddEndpointsApiExplorer()` is back**, because `MapIdentityApi`'s routes are
  minimal APIs and MVC's explorer does not see them. Without it they work and
  are invisible in Swagger UI.

Two ceilings written down, not fixed: `forgotPassword`/`resendConfirmationEmail`
answer 200 against a **no-op `IEmailSender`** (nothing is mailed; confirmation is
not required to log in), and **antiforgery is still off** — re-read at 11.2a and
kept off, held off now by `SameSite=Lax` rather than by there being no cookie.

**11.1a LANDED 2026-09-04**: `src/` is now **eleven projects**, there is a
**sixth migrating context** and a **sixth schema, `identity`**, holding ASP.NET
Core Identity's seven tables and its own `__EFMigrationsHistory`. Suite 332, no
change — the new assertions are in `SmokeTests`. **Nothing is enforced yet and
nobody can sign in**; that is 11.1b. Five things from it change how new code is
written:

- **`JobkeepUser : IdentityUser<Guid>` — the key is a Guid, not Identity's default
  string.** Otherwise `OwnerUserId` becomes a `varchar` foreign key on every
  scoped table at 11.2.
- **The platform's table names are KEPT** (`AspNetUsers`, not `users`). The schema
  qualifier does the separating; the names are what makes the database recognisable
  as Identity. Do not "tidy" them.
- **`IdentityDbContext` is the ONE context that does not call
  `ModelConventions.ApplyDatabaseDefaults`**, and it says why at length: three of
  the seven tables have composite keys made of foreign keys, so the Guid-PK default
  would put `gen_random_uuid()` on a foreign key column, where the only row it can
  produce is one that fails the constraint.
- **`PostgresFixture.ModuleSchemas` gained `identity`**, so Respawn truncates the
  seven tables between tests. A module schema missing from that array is a table
  that leaks state across tests.
- **`src/Dockerfile` COPIES EACH `.csproj` BY NAME.** A new project must be added
  there or `docker compose up --build api` fails *after* a successful restore, on a
  `ProjectReference` to a directory that was never copied — which reads like a
  compiler error, not a missing COPY. This cost a turn; the Dockerfile now warns
  above the list.

Also live, unscheduled: **Phase 6 step 6.4** (the README), the **Phase 6 visual
pass** on the other seven screens, and the **`docs/token-log.md` backfill**
(Phases 8-14 have no rows and Phase 14's is provisional; the ledger's own rule
says do it in a *fresh* session).
(This line has five times named a step that had already landed — 13.4 at `24fbb49`,
then 13.5, then 13.6, then the match-check rename, then group 6 and group 4 with it
— so check the branch before trusting it. **Phase 6.5 is now DONE in full.**)

**13.5 LANDED 2026-09-03** (suite 268 → 283, no migration, no `web/` change). The
29 routes are five `[ApiController]` classes in `src/Jobkeep.Api/Controllers/` and
**`Api/Endpoints/` is deleted** — do not recreate it, and do not add a route
anywhere but a controller. Four things from it change how new code is written:

- **An action returns `Task<IResult>` and calls `.ToHttpResult()`, unchanged.** MVC
  converts an `IResult` return itself (`HttpActionResult` is `internal` — it is not
  meant to be used by hand). That is also what keeps responses byte-identical:
  `Results.*` serializes through `Http.Json.JsonOptions`, so
  `Results.BadRequest("message")` is a bare JSON string, where
  `ControllerBase.BadRequest("message")` would be `text/plain`. **Do not "modernise"
  an action to `ActionResult<T>`** — that changes the wire.
- **JSON is configured in TWO places and both are load-bearing.** MVC does not read
  `ConfigureHttpJsonOptions`. Requests deserialize through MVC's `JsonOptions`
  (`AddJsonOptions`, where the enum converter is), responses through the Http.Json
  one. Adding a converter to only one is a silent half-fix.
- **The slice owns the RULES; the framework owns whether there was anything to apply
  them to.** `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` is on,
  so `POST {}` reaches the handler and both surfaces answer with the same sentence
  (finding A4). `SuppressModelStateInvalidFilter` is deliberately **off**: it was
  tried, and an unbindable request then binds `null` and 500s — measured on an empty
  body and a 6 MB upload. Both halves are pinned by tests.
- **A limit that comes from config cannot be an attribute.** `[RequestSizeLimit]` and
  `[RequestFormLimits]` take constants, so the upload's caps are attached as endpoint
  metadata at `MapControllers()` in `Program.cs`, where the bound `DocumentOptions`
  exists. A `const` would work today and fail silently the day the `Documents`
  config section set anything.

**13.4 needs a decision before any code:** 33 requests become `IRequest<T>` and 53
call sites become `Send(...)`, and the mediator library is chosen *at that step*.
MediatR went commercial (free below a revenue threshold; a personal portfolio sits
under it — **confirm and record the finding**), `martinothamar/Mediator` is MIT and
source-generated. **Either needs the user's approval before it is added.** 13.3c
already built the notification half by hand — `Jobkeep.SharedKernel/DomainEvents.cs`,
three types, ~30 lines — deliberately so that 13.4 swaps the types and leaves every
call site alone.

**13.3c LANDED 2026-09-02** (suite 256 → 266, no migration, no `web/` change). It is
application code and diagrams only, and four things from it change how new code is
written:

- **A cross-module CASCADE is now a NOTIFICATION and a cross-module RESTRICT is a
  CONTRACT CHECK.** That is the rule, and the asymmetry is the point: a cascade is a
  consequence, announced after the publisher commits; a restrict is a question, asked
  before. `Applications` publishes `ApplicationDeleted` and `PostingDeleted` and
  names no subscriber; `Match` and `Ai` subscribe. Do not add a contract method that
  deletes another module's rows — that inverts the direction on purpose chosen here,
  and `SharedKernel/DomainEvents.cs` argues why at length.
- **Publish AFTER `SaveChangesAsync`, never before.** On failure that leaves an
  invisible orphan rather than destroying work on a row that survived. Same call
  `ISkillCatalog.FindOrCreateAsync` makes about its own save. There is **no outbox**,
  so a crash between commit and publish loses the event; that is written down, not
  forgotten, and it is **Phase 15's** — Phase 14 is the skill vocabulary, which
  took the number on 2026-09-03.
- **A delete-side contract check is WEAKER than the RESTRICT it replaced, and the
  gap is a TOCTOU race** — a foreign key refuses inside the transaction, two counts
  and a delete do not. Accepted with reasons in `DeleteResume.cs`; the read paths
  already tolerate the residue (`ApplicationDetail` leaves `ResumeLabel` null,
  `GetMatchResult` the same). Do not "fix" that tolerance — it is the safety net.
- **Two routes now exist that never did: `DELETE /postings/{id}` and
  `DELETE /resumes/{id}`.** Both were built because a replacement with no publisher
  or no caller is a replacement nobody can verify. The posting refusal is still
  enforced by a live FK (same schema) and the check only buys a 400 instead of a 500;
  the résumé refusals are the whole protection.

**Also corrected at 13.3c, having been wrong since 13.3b: SIX foreign keys were
dropped, not five.** `posting_skills.SkillId → skills` was missed because it is a
link table's second key that no module's *code* mentions. Counted out of
`pg_dump --schema-only`: 13 before, **7 after** (5 CASCADE / 2 RESTRICT), all
intra-schema. Both diagrams were redrawn from that dump and `schema-erd.svg` gained
a dotted edge style meaning *"a relationship Postgres no longer knows about"*.

Two questions from the 13.3b handoff are **closed, not built**: a skill id with no
catalog row and a `match_results.ResumeId` pointing at a deleted résumé are both now
unreachable through the app (nothing deletes a skill; a résumé delete is refused
while either table points at one), so they got no warning field. Don't re-open them.

**13.3b LANDED 2026-09-02** (suite 254 → 256, five schemas, compose stack up from a
dropped volume). It is the physical split, so most of what this file said about the
data layer changed with it:

- **`Jobkeep.Infrastructure.Data` is deleted** and `src/` is **nine** projects. The
  thirteen entities live in the five modules that own their tables (`<Module>/Domain/`),
  their configurations beside them (`<Module>/Persistence/`), and the six
  `I<X>DbContext` interfaces are **gone**, replaced by six real contexts. `AppDbContext`
  no longer exists — do not look for it, and do not reintroduce anything like it.
- **Five schemas, five migration histories:** `applications`, `skills`, `documents`,
  `ai`, `ats`. Analytics has a context and neither, because it owns no tables; it reads
  three views that live in `applications`. `Program.cs` migrates SIX contexts of seven
  since Phase 11.1a — five until then; Analytics is the one that is never migrated.
- ~~**Entities kept `namespace Jobkeep.Models`.**~~ **Done at 13.6**, in one pass as
  planned. Entities are `Jobkeep.Modules.<X>.Domain`, the shared enums are
  `Jobkeep.Contracts.Shared`, and `Jobkeep.Shared` / `Jobkeep.GraphQL` are gone. The
  rule and the bug it uncovered are in "Where new code goes" above.
- **`ApplicationStatus` and `SkillSource` are in Contracts, not copied.** Both appear in
  two modules' response DTOs, so both reach the GraphQL schema, and two CLR enums of one
  name is a schema-**build** failure. `PostingRequirementKind` and `ResumeSourceFormat`
  stay copies because their entity half is never published. That is the test: **copy
  only when one side is unpublished.**
- **The six contexts are six units of work.** `ISkillCatalog.FindOrCreateAsync` saves in
  its own transaction now, genuinely — its "call me before adding rows of your own"
  ordering rule was belt-and-braces before and is the entire safeguard since.
- **`dotnet ef` needs `--context` as well as both project flags**, e.g.
  `dotnet ef migrations add X --project Jobkeep.Modules.Documents --startup-project
  Jobkeep.Api --context DocumentsDbContext`.
- **Raw SQL must name its schema.** Unqualified names resolve through `search_path` to
  `public`, which now holds nothing. This bit eight tests, `::regclass` included.
- **Dropping an FK silently drops its indexes**, including the UNIQUE index that made a
  one-to-one "one". Four indexes had to be restated by hand; if you drop another
  relationship, check what went with it.

**13.3c did all of the above and is done** — see its own section higher up. The one
correction it made to this block: the FK count was **six**, not five.

**The dev database was dropped at 13.3b**, as agreed. Asked and answered; do not re-ask.


**13.2 IS DONE** (2026-09-01, all five sub-steps, suite 244 → 246 → 249 → 253 → 253).
The property it bought, and the one 13.3b then made physical: **no module names another
module's table.** 13.2 did it logically, with nothing moving in Postgres, which is what
let 13.3 be a schema change and nothing else.

Six things from 13.2 still change how new code is written:

- **A module takes its OWN `DbContext` and no other.** 13.2's six `I<X>DbContext`
  interfaces did this by omission; since 13.3b there are six real contexts and the
  property is structural — another module's tables are not in the model.
  `ModuleBoundaryTests.No_module_takes_a_context_it_does_not_own` enforces it (rewritten
  at 13.3b, because the old version looked for `AppDbContext` by name and would have
  gone vacuous when that type was deleted). Its allowlist, the conditional that read it
  and the canary that guarded it were all **deleted in 13.2e** when the list emptied —
  as the list's own comment instructed. Don't reintroduce one.
- **Every cross-module crossing is a contract call, in `src/Jobkeep.Contracts/`.**
  Four interfaces: `IApplicationContract` (2 methods), `IPostingContract` (4),
  `IResumeContract` (3), `ISkillCatalog` (3). Implementations sit in
  `<OwningModule>/Infrastructure/`.
- **`IPostingContract`'s two-method cap was LIFTED in 13.2e and its reasoning
  rewritten in place.** The cap argued from decision 17 — cross-module reads are
  ordinary, so only writes need a contract — which Phase 13 reverses. **The rule that
  replaced the number is the test `ISkillCatalog` already carried: does a method name
  a fact about the thing, or a question the caller has about its own feature?** The
  second kind stays with the caller. That is why the ATS skill gap did not become a
  fifth method on `IPostingContract`.
- **`ISkillCatalog` is finished at three verbs** — `GetAsync` (ids → names, batched),
  `FindByNameAsync` (one name → row, on the natural key), `FindOrCreateAsync`
  (batched, keyed by the name you passed in). Since 13.2c, `NaturalKey.Of` is called
  in exactly ONE file in `src/`. Do not reach for it near a skill name; that is the bug.
- **`FindOrCreateAsync` SAVES — call it before adding anything of your own to the
  change tracker.** Until 13.3b all six interfaces resolved one scoped `AppDbContext`,
  so the catalog's save flushed your pending changes too; since 13.3b it is simply a
  different context and a different transaction. The rule is unchanged and the reason is
  now the plain one: a failure after that save leaves the skill rows committed and yours
  not. `CommitImport.CommitResumeAsync` gets the order right and says why at length. The
  accepted cost is an orphan skill row when a link fails; it is harmless and is written
  down, not to be re-discovered.
- **A contract that writes must report a PARTIAL write, not throw.**
  `IApplicationContract.CommitPostingAsync` returns the ids alongside the error
  (`PostingCommitResult.Incomplete`) because the one thing a caller needs after a
  half-finished write is what an exception cannot carry: what got created. **13.2e
  showed the other half of the rule** — Match writes only its own table and every
  contract call it makes is a read that happens *before* the first row reaches the
  change tracker, so no partial write is possible. Ordering is what buys that, and
  `RunMatchCheck.cs` says so above the store block.
- **Two rules in this file were knowingly broken, both argued in code.** The ATS
  skill gap is an in-memory `Except` over two contract calls rather than a SQL set
  difference ("aggregate in SQL, not in memory") — justified because both sets are
  tens of items bounded by what a human typed, and because the alternative is a join
  that will not exist. And skill-name sorting left the database collation for
  `StringComparer.OrdinalIgnoreCase`, matching `GetResume` and `ListApplications`;
  a test pins it.

**Phase 6.5 group 4 (paste text) was parked** until the 13.3 boundary, and **shipped
2026-09-04** — see the block below. Deferring it was right for the reason recorded at
the time: it touches `src/`, and doing it mid-phase would have meant writing a slice
13.4 and 13.5 then rewrote twice.

**13.2c is the one sub-step that touched the front end**, and only as a widened type:
`ImportStatus` gained `CommitFailed` in `web/src/lib/api.ts`, plus a fourth queue tab
and a banner on the Upload screen. No URL moved. It needed no migration — the column
is `varchar(20)` with no CHECK constraint — but a closed TypeScript union is a wire
contract, so leaving it would have been a lie in a type.

**Phase 6.5** (`docs/phases/phase-6.5-upload-experience.md`) is the Upload screen,
opened 2026-09-01 by the first real feedback the front end has had. Groups 1-3, 5
**is DONE in full** as of 2026-09-04 — the import → upload rename (**UI wording only;
the wire keeps `/imports`**), the drop zone, the timer-driven progress bar, the
spacing, **the upload no longer blocking on the model** (group 6) and **paste an ad's
text** (group 4). Do not re-argue the URL scraper — it is refused with reasons in
`docs/backlog.md`.

**GROUP 4 LANDED 2026-09-04** (suite 290 → 299, web 49 → 50, no migration).
`POST /imports/text` and the `importText` mutation take a pasted advertisement
instead of a file. Four things worth not re-deriving:

- **`ImportTextHandler` DELEGATES to `ImportDocument` through the mediator.** It
  owns exactly one rule — a paste under `MinTextChars` is refused where a file
  under it is saved and warned about — and then sends ordinary bytes. So "a paste
  and an uploaded `.txt` are the same import" is a property of the call graph, not
  a comment: same content hash, same extracted text, same format, same status, and
  `ImportTextTests` asserts it as identity. **Do not "optimise" this into a second
  call to `IDocumentTextExtractor`** — that is the version the plan specified, and
  it recreates the filename truncation, the hash, the save-before-parse ordering
  and the enqueue as a second copy that agrees only by inspection.
- **A sibling ROUTE, not an optional `file` parameter.** One endpoint with two
  mutually exclusive bodies is the neighbourhood of the `[FromForm]`/`IFormFile`
  trap that made `swagger.json` answer 500 for the whole document in Phase 4.5.
- **The paste is trimmed before it is hashed**, so a sloppy browser selection
  dedups against a tidy one and matches a `.txt` of the same words.
- **`DraftLimits.MaxDescriptionLength` (20000) now clips on confirm.**
  `job_postings.Description` is `varchar(20000)` and `CommitImport` falls back to
  the whole extracted text when the model proposes no description — a long ad
  confirmed into a 500. Clip, not refuse, matching every other field there.

**GROUP 6 LANDED 2026-09-04** (suite 285 → 290, no migration). `POST /imports` no
longer calls the model: it extracts, saves, and returns with the row in a new
`ImportStatus.Parsing`, and **`ImportParseWorker` structures it in the background**.
Six things worth not re-deriving:

- **`document_imports.Status == Parsing` IS THE QUEUE.** No new table, no
  migration — the durable work list is a column that already existed.
  `ImportParseQueue`'s `Channel<Guid>` sits on top only so the worker need not poll
  for a row the request thread already knew about; losing a channel message costs
  nothing, because the worker **sweeps every `Parsing` row on startup**. That sweep
  is the crash recovery, and it is the reason the queue is a column.
- **This REVERSED a client-driven design shipped the same day.** The first version
  had the review screen drive `POST /imports/{id}/reparse`. It was replaced at the
  user's instruction because it still left a browser tab owning the work: close the
  tab and the row stranded. **The refusal it rested on — Lambda freezes background
  threads after a response — was sound**, and `phase-10-aws-deploy.md:84` makes it
  stronger than it first looked (the Lambda avoids a VPC *so that* the AI call
  works there). It is moot because **the AWS plan was dropped on 2026-09-04**.
  Re-make that argument if a serverless target ever returns.
- **Enqueue AFTER `SaveChangesAsync`, and not as a mediator notification.**
  `AddMediator` is `ServiceLifetime.Scoped` and `IPublisher.Publish` awaits inline,
  so a notification would run the model *inside* the upload request and reinstate
  the block. The channel is the boundary the request thread does not cross.
- **`Documents:ParseInBackground` is a TEST SEAM, not a knob** — same as
  `Skills:SeedOnStartup`. A worker racing Respawn would structure documents out
  from under unrelated arranges. Only `ImportParseWorkerTests` turns it back on.
- **`/reparse` has two callers and branches on which.** Finishing an upload closes
  a model failure out into `AwaitingReview` with a warning; a re-parse a human
  pressed returns `Invalid` and leaves the existing draft untouched. The plan
  assumed the endpoint already accepted a `Parsing` row — **it did not**, and that
  was the one gap that would have shipped broken.
- **`POST /imports` still returns 201, and the scanned-PDF path never enters
  `Parsing`.** A document with no text layer is finished when saved, so marking it
  `Parsing` would strand it in a state with no exit — which is also why the status
  code stayed 201 rather than becoming a 202 that varied by document.

The remaining ceiling is **concurrency, not durability**: two instances would both
parse the same row, because there is no lease. One runs today. A lease column and a
reaper stay **Phase 15's**, next to the outbox.

**That refusal is about the SERVER fetching a URL, and it is not the last word on
intake.** On 2026-09-01 the user named the real gap — *"we are missing the aspect that
where can we get those data for job ad"* — and `docs/backlog.md` gained an **intake
question** section for it. The short version worth having before touching this area: a
**browser extension** reads a page the user already opened, so it answers every one of
the scraper refusal's objections rather than reviving them, and this app is unusually
well placed for one because it already turns unstructured text into a draft (Phase 4.5
`DocumentStructurer`) and so needs **no CSS selectors to break on a redesign**. It is
blocked on Phase 11 for any public ship, and paste-the-ad (group 4) — **which shipped
2026-09-04** — is its backend either way.

Phase 6 itself has two things left: the **visual pass on the other seven screens**
(the user has seen the app and says there are problems, but has not said which —
ask directly, screen by screen; all eight were built on the same patterns, so a
systemic problem is eightfold) and **step 6.4**, the README. Steps 6.1-6.3 are done (2026-08-29 to 2026-08-31): CORS
and the résumé reads, the Vite scaffold and the token system, then all eight
screens plus a Vitest suite of 35. **The stack and the design are decided** —
React, Vite, react-router, dnd-kit, lucide-react, no component kit. Don't re-open
any of it; the user asked to be asked before any new dependency is added.

**Phase 7 is DONE** (2026-09-01) — one migration, `DataIntegrityAndNaturalKeys`,
suite 228 → 239 green. `docs/diagrams/schema-erd.svg` was redrawn on 2026-09-01,
in the session after the one that moved the schema, so that trigger is discharged.
Final state **as at Phase 7**, if you need it without re-deriving: 13 tables, 13 FKs
(7 CASCADE / 6 RESTRICT), 5 unique indexes, 12 plain. **Phase 13.3b changed this and
the numbers above are history, not the current schema** — it was then 13 tables in five
schemas, **7** FKs (5 CASCADE / 2 RESTRICT), 5 unique and **10** plain, with the six
dropped keys enforced in application code since 13.3c. One method note worth keeping — the
redraw derived the schema from `pg_dump --schema-only` against the migrated
database rather than from `dotnet ef migrations script`, because an *idempotent*
script is a sequence of migrations and later `ALTER`s silently correct earlier
`CREATE`s; reading the final state out of that text is guesswork, and the dump is
the applied result.

**PHASE 8 IS DONE (2026-09-04)** — soft delete. Two migrations (`SoftDelete`, in
`applications` and `documents`), suite 303 → 314, web 50 → 52. Six things from it
change how new code is written, and the first is the one that costs money to
re-learn:

- **`Remove()` MEANS ARCHIVE.** `ISoftDeletable` (SharedKernel) is matched by
  `AuditSaveChangesInterceptor`, which converts `Deleted` → `Modified` and stamps
  the two columns. So a slice cannot hard-delete by writing the obvious thing —
  and **`ExecuteDelete` still can**, because it bypasses the change tracker. Three
  entities carry it: `JobApplication`, `JobPosting`, `Resume`, the three with a
  delete slice. **`Company` and `Skill` do NOT**, because nothing deletes them; the
  phase doc corrects the plan's demand for filtered indexes on their names.
- **`HasQueryFilter` DOES NOT REACH RAW SQL, AND THAT IS THE SILENT ONE.** All
  three of Analytics' published views kept counting archived rows until the
  migration re-cut them by hand; `v_posting_skill_demand` gained a JOIN it never
  had, since `posting_skills` has no `IsDeleted`. Same trap applies to
  `ExecuteUpdate`/`ExecuteDelete` and any future function or view.
- **THE 13.3c DELETE NOTIFICATIONS ARE NO LONGER PUBLISHED.** `ApplicationDeleted`
  and `PostingDeleted` existed to delete derived rows; archiving must not destroy a
  stored match check about a row that survived — which is the weighing
  `DeleteApplication.cs` already made in 13.3c, now pointing the other way. Both
  events and both handlers are **unreachable through the app**, kept for the purge
  (F18) that is their real caller. So `match_results` and `ai_analyses` now outlive
  every archive and **nothing deletes them any more**.
- **`resumes.LabelNormalized`'s unique index is FILTERED** (`NOT "IsDeleted"`), so
  archiving frees the label — otherwise an archive silently burns a name. The price
  is that **`RestoreResume` can be refused** with a 400 when a live résumé took the
  label meanwhile. It asks first rather than letting the index throw a 500.
- **`?includeArchived=true` means INCLUDE, not ONLY**, on `/applications` and
  `/resumes`; both list items carry `isArchived`. On applications it calls
  `IgnoreQueryFilters()` on the whole query, deliberately — `job_postings` is
  filtered too, and an inner join would otherwise drop an application whose ad was
  also archived, silently and only for that row.
- **The archive button is NOT `.btn-danger`.** An archive is reversible and
  deliberate; the alert red is for failures and destruction (`PRODUCT.md`). The
  undo is an inline banner with **no timeout**, because the row is recoverable for
  as long as it exists and a timer only punishes someone who looked away.

**The plan's front-end estimate was wrong by an order of magnitude, and the reason
is worth not repeating.** It said "five list routes, five empty states" and called
this the highest front-end blast radius on the roadmap. **One screen changed.**
The plan assumed delete affordances existed to convert — none did;
`deleteApplication` was exported and called by nothing. The other four list routes
needed no change at all, because the global filter excludes archived rows from
fetches nobody rewrote. A blast-radius estimate written against a *planned* UI
decays as fast as the plan does.

**The roadmap was reordered and renumbered on 2026-09-01** (architecture.md
decision 18). Read `docs/README.md` for the table; what matters here is the rule
that produced it and the two traps it leaves:

- **Phases are now ordered by *compounding* cost, not by appeal.** The test is
  "does deferring this make the later work bigger?" Almost nothing passes it —
  reminders, contacts, export, interview rounds, a target profile and the
  HotChocolate major all cost the same in six months, so they are P3/P4 and wait.
  Three items pass and are now Phases 7, 8 and 11.
- **Numbers are history for built work and build order for unbuilt work.** Phases
  1-6 keep the numbers they shipped under. **Phase 3 is now Phase 10** and
  **"Phase 2.7" is now Phase 7**; the old placeholder Phase 7 is now Phase 12.
- **Done phase docs still say "Phase 3" and "Phase 2.7", deliberately.** They are
  dated records of what was decided then. Do not sweep them — that is exactly the
  re-reading-unchanged-markdown cost decision 12 exists to stop. Forward-looking
  docs and `src/` comments were updated; both renamed docs carry a "formerly" note.

**The commit before any front-end code exists is tagged `checkpoint/backend-complete`**,
and `docs/phases/phase-6-frontend.md` freezes the API surface as at that point. From
here **a feature has two halves** — a slice *and* a screen — so estimates carried
over from Phases 2-5 are about half the real cost. That, and the checklist it
implies, is `docs/phases/phase-12-feature-expansion.md`; it is deliberately not a
feature list, because `docs/backlog.md` already is one. It also records the **three
backend gaps the front end found**, which are now Phase 9.

Phase 5 is done (2026-08-28): `Modules.Match/` (named `Modules/Ats/` until the 2026-09-04 rename), two slices, both surfaces. Four
things from it are worth carrying forward, and the first two change how new work
should be written:

- **Decision 17 narrowed rule 2 to writes — and Phase 13 REVERSES it. Do not
  follow it for new code.** Decision 17 said a module may read another module's
  tables and only a write needs a contract, which is why Ats read five tables it did
  not own for five phases. It answers *"is this safe?"*, and it still answers it
  correctly: a reader cannot leave anyone's data in a state they did not choose.
  Phase 13 asks a different question — *"can this module be lifted out?"* — and
  against that one read-only buys nothing, because a `SELECT` across a boundary is
  precisely what stops working when the boundary becomes a network. **Every crossing
  needs a contract now, reads included.** 13.2e duly added the `GetPostingSkills`
  method this bullet used to say was unnecessary. **Decision 17 is now superseded in
  `architecture.md`** (13.6), along with 5, 6, 7, 12, 13 and 15.
- **Use a model only where a query cannot answer.** The plan said to prompt the
  model for the keyword match; it shipped as a SQL set difference over the shared
  `skills` table, which is exact, instant and free. The model now answers only
  free-text requirement coverage. Three of the check's four stages need no model,
  so it **degrades** on an outage rather than failing — and the warning is stored,
  because an unstored one lets a later read of an empty `UnmetRequirements` claim
  every requirement is met.
- **The skill gap matches skill *rows*, not skill *text*, and the verification
  proved it costs something.** Run against the real CV and a real Melbourne ad, it
  reported `PostgreSQL` as missing even though the CV names it in prose — the
  resume's structured skill list says `SQL`. Same family as the case-sensitive
  dedup gap already recorded below; fix them together, not separately, because
  both want a normalised natural key on `skills`. **BOTH HALVES ARE NOW FIXED** —
  Phase 7 the case key, **Phase 14 the aliases** (`skills.skill_aliases`, resolved
  inside `SkillCatalog`, so no call site changed). `.NET Core` → `.NET` is one of
  the seeded aliases. `PostgreSQL` → `SQL` deliberately is NOT: one is an instance
  of the other, and aliasing them would claim a match the CV has not earned. The
  **correction path** shipped
  with the phase — `POST /resumes/{id}/skills` and the `addSkillToResume` mutation
  (`Modules/Documents/AddSkillToResume.cs`) — so a near-miss costs one click
  rather than a re-import. It is not the synonym fix, and it is the first write to
  `resume_skills` outside the Phase 4.5 import cycle; it is also what backs the
  CV-centre drag in the Phase 6 design.
- **`match_results` is 1:1 with the application and its `ResumeId` says which
  resume the surviving row judged.** Re-checking overwrites; latest wins.

Phase 4 is done (2026-08-27), and its story has a tail worth knowing: its tests
were written but **never executed** — Docker was down that session — and were run
for the first time during Phase 4.5, passing 10/10 unchanged. Don't repeat the
pattern: a phase whose tests have not run is not verified, whatever the doc says.

**THE AWS DEPLOY IS DROPPED (2026-09-04), at the user's instruction** — *"we are
going to drop the AWS deploy, we gonna use different free tools later on."* Phase
10 went from *Parked* to *Dropped*; `architecture.md` decision 3 is superseded and
decision 22 records it. **It cost zero source changes, which is the whole point:**
the Lambda entry point was never written, so four phases were built past the deploy
without accumulating any AWS coupling, and the container the compose stack already
runs is the portable half. Don't propose an AWS variant without reading
`docs/phases/phase-10-aws-deploy.md` first — the Aurora and API Gateway rejections,
the cold-start figures and "this account has **no free tier left**" are still valid
dated findings, so "t3.micro is free" style advice still does not apply.

Three things survive the target and should not be re-derived: **the rule**
(*nothing in the deployed architecture may bill per hour* — it was never an AWS
rule and it is the test any replacement host must pass), **Neon's free tier** (a
database choice, not an AWS one), and **the research as a "why not X"**.

**The real hazard is a trigger hung off an event that will now never happen.**
Two things were due "when the deploy unparks": the **doc/security-audit sweep**
(see "Documenting as you go" — cadence, fresh session) and the audit's **transport
& secrets hardening**. Both are re-hung on **before whatever deploy replaces Phase
10**. Same failure mode decision 18 named about unowned numbers.

Phase 2.6 is done (2026-08-26): `net10.0` everywhere, EF/Npgsql/`dotnet-ef` on
the 10.x line, CI down to one SDK. **No C# changed** — the whole upgrade is four
project/config files. Two things worth carrying forward:
- **It caught a critical CVE, which is the real story.** The restore surfaced
  NU1904 on `HotChocolate.Language` 14.3.0 — an uncatchable stack-overflow DoS
  reachable from the unauthenticated `/graphql` endpoint, *before* validation
  runs. Fixed by 14.3.1 (patch, no API change). The 14 → 16 major jump was
  refused and is in `backlog.md`, along with a parse-depth guard for Phase 10.
- **`net8.0` is gone from the build but not from the migrations.** The snapshot
  and initial designer file still say `ProductVersion "8.0.11"`. That is metadata,
  deliberately left; EF 10 reports no model drift from it. Don't regenerate
  migrations to tidy the string.

Phase 2.5 is done (2026-08-26): the status lifecycle, in
`Models/ApplicationStatusTransitions.cs`, consulted by the update slice. Two things
not to re-litigate — both were confirmed with the user, and the reasoning is in the
phase doc:
- **The table is deliberately permissive.** `Applied → Offer` is legal, and
  `Rejected`/`Withdrawn` are *closed*, not terminal — Huntr and Teal both let a user
  move a job back out of a closed stage, and shipping a stricter rule than any
  product in the category was judged the wrong trade. The invariant that remains is
  **an `Offer` can only be reached from an active application**.
- **`tests/Jobkeep.Tests/Domain/` is the suite's only unit test**, and that is on
  purpose — the rule is a pure function of two enums, so a container buys nothing.
  Everything a database can still get wrong is pinned in
  `Parity/SurfaceParityTests.cs`. Don't read it as licence to unit-test the slices.

Phase 2.4 is done (2026-08-26): `Modules/Analytics/`, read-only, three `GROUP BY`
slices on both surfaces. Its one architectural consequence was **decision 13** — a
read-only reporting module allowed to read other modules' tables, which rule 2 then
forbade. **Phase 5 generalised it into decision 17: the boundary rule is about
writes, not reads.** A module may read another module's tables; only a write needs a
contract. So a second cross-module reader needs no exception — read decision 17, not
13, and note that rule 2's old wording ("a module only queries the tables it owns")
is superseded.

Read the relevant `docs/phases/phase-N-*.md` file first — it already has the
plan. Implement it, update that doc's "Status" field to "Done" when
working, and add any real deviations from the plan as notes in the doc
so it stays an accurate record (useful later for interview stories too).

When the phase is done, also **log what it cost**: run
`python scripts/token-usage.py`, add a row to the "By phase" table in
`docs/token-log.md` and refresh its session table. That is the whole checklist —
phase doc, token log, stop. The transcripts this reads are local and not kept
forever, so a phase that isn't logged when it ends may not be recoverable later.

Logging necessarily happens *inside* the session it measures, so the row always
understates the session it is written in. Say so in the row rather than implying
the number is final — the Phase 2.2 row did not, and was wrong by half.

The phase docs were written before the architecture record. If a phase doc
contradicts `docs/architecture.md`, follow `architecture.md` and fix the
phase doc as part of the work.
