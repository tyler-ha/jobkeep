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
   PostgreSQL (see Architecture). Local dev uses Postgres in Docker (free);
   the deployed DB is **Neon's free tier** (serverless Postgres, scales to
   zero, $0). This replaced RDS free-tier in the deploy phase (now Phase 10, see
   `docs/phases/phase-10-aws-deploy.md`) — see that doc for why,
   and for the rule it produced: **nothing in the deployed architecture may
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
- **Storage**: **PostgreSQL via EF Core** (Phase 2). Schema lives in
  `src/Data/AppDbContext.cs` (Fluent API, one place) with EF migrations in
  `src/Migrations/`. An earlier draft of Phase 2 used DynamoDB; that was
  dropped in favour of a normalized relational model — see the Phase 2 doc.
- **AI calls**: behind `Microsoft.Extensions.AI`'s `IChatClient` (Phase 4), so
  Ollama (local, free) and a hosted API are swappable via config, not code
  changes. Registered in `src/Shared/ModelClient.cs`, **not** in the Ai module —
  Phase 4.5 made Documents a second caller, and the rule that produced is that
  **`Ai` owns the `ai_analyses` table, not the technology**. `IChatClient` is a
  shared dependency any module may inject, like `AppDbContext`.
- **Deployment target**: AWS Lambda behind a **Function URL** (no API Gateway
  — see the Phase 10 doc), with PostgreSQL on **Neon's free tier**. The Lambda
  deliberately stays *out* of a VPC, so no NAT Gateway is ever needed. Both the
  REST and GraphQL endpoints ride the same Lambda.

### Where new code goes

> **PHASE 13 IS IN PROGRESS AND THIS SECTION IS OUT OF DATE.** Step 13.1 landed
> 2026-09-01: `src/` is now **ten projects**, one per module plus SharedKernel,
> Contracts and Api — see `docs/phases/phase-13-clean-architecture.md` for the
> target shape and the reference rule (*a module never references another module*),
> which `tests/Jobkeep.Tests/Architecture/ModuleBoundaryTests.cs` enforces. The
> vertical-slice rules below still describe how a **use case** is written, and that
> has not changed; only the paths have. This section is rewritten in full at 13.6,
> deliberately last, so it is rewritten once rather than after every step.

New feature work goes in a **module**, as a **slice per use case**:

```
src/Modules/<Module>/<UseCase>.cs   — request + handler + response, together
src/Modules/<Module>/<Module>Module.cs — DI + MapGroup routes for that module
src/Shared/                          — SliceResult + cross-cutting contracts
src/Data/AppDbContext.cs             — the schema, in one place (Fluent API)
src/Program.cs                       — wiring only (DI, middleware, Map* calls)
```

A new slice is a new file plus two lines in `<Module>Module.cs` (register the
handler, map the route) — `Program.cs` only ever calls `Add*Module()` /
`Map*Module()`.

Modules: `Applications` (core), `Analytics` (read-only), `Ai` (Phase 4),
`Documents` (Phase 4.5), `Ats` (Phase 5), `Identity` (later).

**Two rules that matter:**
- **A slice owns its use case end to end.** Handlers use `AppDbContext`
  directly — EF's `DbContext` is already a unit-of-work plus a repository, so
  a hand-written repository over it is a layer that mostly forwards calls.
- **A module never *writes* another module's tables.** Modules share one
  database. Reading across the boundary is free; writing needs a contract on
  the owning module, or a call to one of its own use cases, because a second
  writer re-decides invariants the owner owns. This is what keeps a future
  service extraction a code-move rather than a redesign. Narrowed to writes in
  Phase 5 — architecture.md decision 17; the old wording ("never reads another
  module's tables") is superseded.

### Migration state (read this before editing `src/`)

> **Superseded in part by Phase 13.** The Phase 2.1-2.3 migration described below
> did finish, and its rules about *how a slice is written* still hold. What has
> changed is where the files live: since 13.1 each module is its own project, and
> the entities plus `AppDbContext` sit temporarily in `Jobkeep.Infrastructure.Data`,
> a project whose own csproj explains that it is scheduled for deletion in 13.3.

**The migration is finished.** It ran incrementally across Phases 2.1-2.3 so each
phase stayed runnable, and as of Phase 2.3 (2026-08-26) `src/` has **one** shape:
every use case is a slice under `Modules/Applications/`. `Endpoints/`,
`Repositories/` and `Models/Dtos.cs` no longer exist — don't go looking for them,
and don't recreate them.

Two files in `Modules/Applications/` are shared by several slices, and the
distinction matters because it is the one the repository got wrong:

- `ApplicationDetail.cs` — the detail response records plus the EF projection
  expression, used by the get / create / update slices.
- `CompanyLookup.cs` — find-or-create a company by name, used by create and update.

Neither owns *access*. Every slice still writes its own query; a slice that needs
something different writes it rather than growing these files. A repository owns the
queries themselves, which is why every use case had to become one of its methods,
and why it kept growing.

The rules:

- Write every use case as a slice in a module. A slice handler takes `AppDbContext`
  directly, validates in the handler (not at either edge), and returns a **response
  DTO**, never an EF entity.
- Don't reintroduce a repository, a service layer, or a `Endpoints/*.cs` file. If a
  phase doc says to add a method to `IJobApplicationRepository` (Phase 2.4 does),
  write a slice instead and correct the phase doc — which is what 2.1 and 2.3 did.
- Prefer a flat `.Select(...)` projection to `Include(...)`. Every read in `src/`
  projects; an include graph is how the over-fetch in A1 got there.

Superseded rules from earlier versions of this file, kept here so their
reversal is legible: *"never bypass `IJobApplicationRepository`"* and *"keep
`Program.cs` as the single place endpoints are defined"* are both retired, and
the interface they protected is deleted. See `docs/architecture.md` decision 5.

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
phase-group boundaries only — before the AWS deploy (Phase 10) and before Phase 6
— and **always in a fresh session**. The cadence and the session boundary do the
saving together: the same sweep late in a long session costs roughly 3x what it
costs early.

The accepted cost, stated plainly: between sweeps the standing docs will lag what
the code does. That is tolerable because the ones that would be actively *wrong*
get fixed immediately, and because a stale sentence is cheaper than the sweep
that would have prevented it.

## Commands

**`docker compose up --build` starts the whole local stack** — Postgres, the API
and the front end, three containers, `compose.yaml` at the root plus
`src/Dockerfile` and `web/Dockerfile`. It needs nothing installed but Docker.
`docker compose down` stops it, `down -v` drops the database, `logs -f api`
follows one service.

- The front end is the **real Vite dev server** with `./web` bind-mounted, so hot
  reload survives. The API is a **published build**, so a C# edit costs
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

Since Phase 13.1 there are ten projects under `src/`, so a bare `dotnet build` /
`dotnet run` in that directory fails with **MSB1011** — name the solution or the
project, as above. `docker compose up --build` is unaffected and remains the
normal way to start the stack.

App listens on `http://localhost:5080` (`Jobkeep.Api/Properties/launchSettings.json`).
Swagger UI at `/swagger` and the GraphQL Nitro IDE at `/graphql` — both
Development-only.

EF migrations (tool pinned to 10.0.11 in `src/dotnet-tools.json`, `rollForward: false`
— bump it in lockstep with `Microsoft.EntityFrameworkCore.Design`):
```bash
dotnet tool restore

# Since Phase 13.1: the model lives in one project and the connection string and
# provider are resolved through another, so both have to be named. Without these
# two flags the command fails in a way that reads like a broken tool install.
dotnet ef migrations add <Name> --project Jobkeep.Infrastructure.Data --startup-project Jobkeep.Api
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

No health check, no auth. These are **recorded**, not forgotten — see the gap
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
  the AWS deploy ships (Phase 10), and once before auth lands (Phase 11). Those are the points where a
  stale finding would actually cost something.
- `docs/user-journeys.md` — what the user actually does, step by step, and where
  that procedure has holes. The counterpart to `architecture.md`: that one
  describes the system from the code's side, this one from the user's.
- `docs/backlog.md` — considered-but-not-committed features, and the
  verified market comparison.
- `docs/token-log.md` — what each phase cost to build, in tokens. Regenerate
  with `python scripts/token-usage.py`; see "When asked to move to the next
  phase" below.
- `docs/diagrams/` — `schema-erd.svg` and `architecture.svg`, embedded in
  `README.md` and `docs/architecture.md`. **Committed artefacts that go stale
  silently** — nothing fails a build when the schema moves and the picture
  doesn't. Redraw them with the `schema-diagram` skill
  (`.claude/skills/schema-diagram/`) in the same change that moves the schema.
  This trigger stays per-change because it fires rarely — only on a migration or
  a module-boundary move. Phase 2.3 had no migration and correctly left
  `schema-erd.svg` untouched while redrawing `architecture.svg`.
  That skill derives the schema from `dotnet ef migrations script`, not from
  reading `Models/*.cs` — column types, precision, delete behaviour and index
  uniqueness live in Fluent API config and the Npgsql provider, so inferring
  them from the model classes produces a diagram that is wrong in exactly the
  places an interviewer would probe.
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

**Currently up next: Phase 13.2e — Ats, the last sub-step of 13.2.** Read
`docs/phases/phase-13-clean-architecture.md`; it is the live plan, and it was
rewritten on 2026-09-01 when the user confirmed **microservices is the destination**.
The short version: `src/` is now ten projects, one per module, and 13.2 puts every
cross-module read behind a contract *while the tables stay put*, so 13.3 can split
the schema without also being the step that changes behaviour.

**13.2 is split into five sub-steps and four of them have landed** (2026-09-01, suite
244 → 246 → 249 → 253). Six things from them change how 13.2e is written:

- **A module takes its own `I<X>DbContext`, never `AppDbContext`.** Six interfaces
  live in `src/Jobkeep.Infrastructure.Data/Contexts/`, each exposing only one
  module's `DbSet`s; the shared context implements all six and is named only in
  `Program.cs`. `ModuleBoundaryTests.No_module_takes_the_shared_context` enforces it,
  with an allowlist now naming only **Ats** — **delete that entry as you convert it**,
  or its canary fails, and then delete the list and its conditional too.
- **`Jobkeep.Modules.Skills` owns `skills`**, and `ISkillCatalog` is finished at three
  verbs: `GetAsync` (ids → names, batched), `FindByNameAsync` (one name → row, on the
  natural key), `FindOrCreateAsync` (batched, keyed by the name you passed in). Since
  13.2c, `NaturalKey.Of` is called in exactly ONE file in `src/`. Do not reach for it
  near a skill name; that is the bug.
- **`FindOrCreateAsync` SAVES — call it before adding anything of your own to the
  change tracker.** All six interfaces still resolve the same scoped `AppDbContext`,
  so a save in the catalog flushes your pending changes too, in a different
  transaction from the rest of your unit of work. `CommitImport.CommitResumeAsync`
  gets the order right and says why at length. The accepted cost is an orphan skill
  row when a link fails; it is harmless and is written down, not to be re-discovered.
- **Cross-module *navigation traversals* count as crossings** — `ps.Skill.Name`,
  `a.Resume.Label`. The phase doc's original count missed them; they are in scope,
  and the correction is recorded there.
- **A contract that writes must report a PARTIAL write, not throw.** 13.2c's
  `IApplicationContract.CommitPostingAsync` returns the ids alongside the error
  (`PostingCommitResult.Incomplete`) because the one thing a caller needs after a
  half-finished write is what an exception cannot carry: what got created. Documents
  stores that id as its idempotency guard, which is what makes `ImportStatus.CommitFailed`
  re-runnable instead of a duplicate waiting to happen. Expect the same question in
  13.2e, where Ats writes.
- **`IResumeContract` already exists** (13.2d, owned by Documents, one method:
  `GetAsync(id)` → `ResumeRef(Id, Label)` or null). Ats needs a résumé label in
  `GetAtsResult` and résumé skills in the gap check — the label is already served;
  the skills are not, and that is 13.2e's call to make. Do not add a second
  résumé contract.

**Phase 6.5 group 4 (paste text) is parked**, by decision, until the 13.3 boundary.

**13.2c is the one sub-step that touched the front end**, and only as a widened type:
`ImportStatus` gained `CommitFailed` in `web/src/lib/api.ts`, plus a fourth queue tab
and a banner on the Upload screen. No URL moved. It needed no migration — the column
is `varchar(20)` with no CHECK constraint — but a closed TypeScript union is a wire
contract, so leaving it would have been a lie in a type.

**Phase 6.5** (`docs/phases/phase-6.5-upload-experience.md`) is the Upload screen,
opened 2026-09-01 by the first real feedback the front end has had. Groups 1-3 and
5 are done — the import → upload rename (**UI wording only; the wire keeps
`/imports`**), the drop zone, the timer-driven progress bar and the spacing.
**Group 4 is what remains**: paste an ad's text through the same pipeline as a
parsed file. It is the only group that touches `src/`. The phase doc has the whole
plan; do not re-derive it, and do not re-argue the URL scraper — it is refused
with reasons in `docs/backlog.md`.

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
Final state, if you need it without re-deriving: 13 tables, 13 FKs (7 CASCADE /
6 RESTRICT), 5 unique indexes, 12 plain. One method note worth keeping — the
redraw derived the schema from `pg_dump --schema-only` against the migrated
database rather than from `dotnet ef migrations script`, because an *idempotent*
script is a sequence of migrations and later `ALTER`s silently correct earlier
`CREATE`s; reading the final state out of that text is guesswork, and the dump is
the applied result.

**Then Phase 8** (`docs/phases/phase-8-soft-delete.md`) — soft delete, which needs
the filtered unique indexes Phase 7's natural-key work created. Note its cost is
overwhelmingly *front-end*: five list routes, five empty states, an undo.

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

Phase 5 is done (2026-08-28): `Modules/Ats/`, two slices, both surfaces. Four
things from it are worth carrying forward, and the first two change how new work
should be written:

- **Decision 17 narrowed rule 2: the boundary is about writes, not reads.** A
  module may read another module's tables; only a write needs a contract. This
  supersedes rule 2's old wording and generalises decision 13, so a new
  cross-module *reader* needs no exception and no contract. Do not add a third
  method to `IPostingContract` for posting skills — decision 17 exists so that
  you do not have to.
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
  both want a normalised natural key on `skills`. The **correction path** shipped
  with the phase — `POST /resumes/{id}/skills` and the `addSkillToResume` mutation
  (`Modules/Documents/AddSkillToResume.cs`) — so a near-miss costs one click
  rather than a re-import. It is not the synonym fix, and it is the first write to
  `resume_skills` outside the Phase 4.5 import cycle; it is also what backs the
  CV-centre drag in the Phase 6 design.
- **`ats_results` is 1:1 with the application and its `ResumeId` says which
  resume the surviving row judged.** Re-checking overwrites; latest wins.

Phase 4 is done (2026-08-27), and its story has a tail worth knowing: its tests
were written but **never executed** — Docker was down that session — and were run
for the first time during Phase 4.5, passing 10/10 unchanged. Don't repeat the
pattern: a phase whose tests have not run is not verified, whatever the doc says.

**The deploy — now Phase 10, formerly Phase 3 — is parked, not blocked**
(2026-08-27). Its plan is complete, researched and costs $0/month; the decision
was that *time* is better spent on local feature work first, and that deploying is
only worth doing once there is enough tool to justify clicking the link. Nothing
about it expires — the always-free grants have no clock. Read
`docs/phases/phase-10-aws-deploy.md` before reopening it; the Aurora
and API Gateway alternatives are already rejected there with reasons, and the
account has **no free tier left**, so "t3.micro is free" style advice does not
apply. Being built past by four phases is what triggered the 2026-09-01 renumber:
the number said "third" and the schedule said "tenth".

Two things are due **when the deploy unparks**, not on a calendar: the
**doc/security-audit sweep** (see "Documenting as you go" — cadence, and in a
fresh session) and the audit's **transport & secrets hardening**. Both were tied
to "before Phase 3 ships to AWS", so the trigger moved with the phase — and moved
again with its number, to Phase 10.

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
