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
   PostgreSQL (see Architecture), which is *not* serverless — the deployed
   DB runs on AWS RDS **free-tier** (free for 12 months, then always-on and
   billable). Local dev uses Postgres in Docker (free). This tradeoff was made
   deliberately for a cleaner relational model; keep flagging it.
2. **Each phase should end in something runnable.** The person has a
   history of abandoning projects when scope gets fuzzy. Don't let a
   phase sprawl — if a change is getting large, suggest splitting it.
   This is also why architecture changes are adopted *incrementally*, per
   phase, rather than as one big refactor.
3. **Explain, don't just generate.** The person is using this project to
   build real understanding (for interviews) as much as to build the app
   itself. Prefer short explanations of *why* alongside code changes,
   especially around design decisions (module boundaries, AWS service
   choices, AI provider abstractions).
4. **Local-first development.** Prefer developing against local/free
   equivalents (Postgres in Docker, Ollama) before touching real AWS or paid
   APIs, matching the pattern already established in Phases 1-2.

## Architecture

**Modular monolith with vertical slices — one deployable, module boundaries
drawn now so services can be extracted later if a real trigger appears.**
Full reasoning, the extraction triggers, and the decision record are in
`docs/architecture.md`.

- **Backend**: ASP.NET Core 8 (`src/`). See "Framework deadline" below.
- **API surfaces**: REST (minimal-API endpoints) **and** GraphQL
  (HotChocolate, `src/GraphQL/`, served at `/graphql`). Both sit on the same
  data layer — GraphQL didn't replace REST. Added in Phase 2b. This dual
  surface is a *portfolio* choice, not an industry norm: no comparable
  product ships a public API. Don't imply otherwise.
- **Storage**: **PostgreSQL via EF Core** (Phase 2). Schema lives in
  `src/Data/AppDbContext.cs` (Fluent API, one place) with EF migrations in
  `src/Migrations/`. An earlier draft of Phase 2 used DynamoDB; that was
  dropped in favour of a normalized relational model — see the Phase 2 doc.
- **AI calls**: planned to go behind `Microsoft.Extensions.AI`'s
  `IChatClient` (Phase 4), so Ollama (local, free) and a hosted API are
  swappable via config, not code changes.
- **Deployment target**: AWS Lambda + API Gateway (serverless, pay-per-use —
  see Phase 3 doc), with PostgreSQL on AWS RDS free-tier. Both the REST and
  GraphQL endpoints ride the same Lambda.

### Where new code goes

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
`Ats` (Phase 5), `Identity` (later).

**Two rules that matter:**
- **A slice owns its use case end to end.** Handlers use `AppDbContext`
  directly — EF's `DbContext` is already a unit-of-work plus a repository, so
  a hand-written repository over it is a layer that mostly forwards calls.
- **A module never reads another module's tables.** Modules share one
  database but not each other's data; cross-module access goes through a
  public contract on the owning module. This is what keeps a future service
  extraction a code-move rather than a redesign.

### Migration state (read this before editing `src/`)

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
  rule with no database in it — the Phase 2.5 status lifecycle is the first — is
  the exception, and gets a plain unit test.

## Commands

```bash
# Start local Postgres first — the app auto-migrates against it on startup
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=jobkeep postgres:16-alpine

cd src
dotnet build
dotnet run
```

App listens on `http://localhost:5080` (`Properties/launchSettings.json`).
Swagger UI at `/swagger` and the GraphQL Nitro IDE at `/graphql` — both
Development-only.

EF migrations (tool pinned to 8.0.11 in `src/dotnet-tools.json`):
```bash
dotnet tool restore
dotnet ef migrations add <Name>
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
repo root rather than `src/`. It installs both the 8.0 and 10.0 SDKs (8.0 to run
net8.0 tests, 10.0 to read `global.json`) and builds `src/Jobkeep.slnx`, which
**includes the test project** — that's why its test step can pass `--no-restore`.

`dotnet test` needs the **repo-root `global.json`** (`test.runner` =
`Microsoft.Testing.Platform`) and the **.NET 10 SDK** to read it — xUnit v3's runner
is MTP, and the .NET 10 SDK refuses the old VSTest bridge. Pass the project as
`--project <path>`, not positionally. No SDK version is pinned, so the `dotnet-ef`
8.0.11 tool is unaffected.

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

## Conventions

- Target framework: `net8.0`. **Framework deadline: .NET 8 reaches end of
  support 10 Nov 2026.** The upgrade to `net10.0` (LTS to Nov 2028) is
  Phase 2.6, before the AWS deploy. Flag this if Phase 3 starts first.
- Nullable reference types enabled — respect existing nullability
  annotations rather than suppressing warnings.
- Keep new NuGet dependencies minimal and justify additions in the
  relevant phase doc.

## Known gaps (don't re-discover these)

No `docker-compose`, no health check, no auth. These are **recorded**, not
forgotten — see the gap register in `docs/architecture.md`.

Tests and CI landed in **Phase 2.2**, scheduled straight after 2.1 because the gap
register called them the highest-value missing items. Findings still **recorded, not
fixed** — don't re-discover them:
- **Skill dedup is case-sensitive**, so `C#` and `c#` are two rows in the table whose
  purpose is deduplication. Company dedup has the same defect. Both need a
  case-insensitive natural key, which is a migration and so its own phase. Note the
  *filters* added in 2.3 are case-insensitive (ILIKE), so searching finds both rows —
  which hides the problem without fixing it.
- **`src` pairs Npgsql provider 8.0.10 with EF Design 8.0.11.** Harmless today;
  Phase 2.6 resolves it.
- **No index on `Status` or `DateApplied`** even though 2.3 filters and sorts on
  both. Deliberate — see F14 — and parked in Phase 2.7 with the rest of the audit
  migration.

**Fixed in Phase 2.3, so don't re-report them:** A2 (entities as the API contract),
A3 (the repository), A4 (surface-specific validation) and A7 (EF entities reachable
through the GraphQL schema). A1 is *partly* fixed — read decision 11 before
"finishing" it, because the obvious fix reopens A7.

## Where things are

- `docs/README.md` — the index: what each doc is for, and which wins.
- `docs/architecture.md` — how the code is shaped, why, and the decision
  record. **Check this before proposing structural changes.**
- `docs/phases/phase-N-*.md` — the plan and status for each build phase, in
  order. Check the current phase's doc before making changes so new
  work matches the intended scope for that stage.
- `docs/security-and-data-audit.md` — schema/config exposure, F1-F18, and the
  phased remediation plan.
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
  That skill derives the schema from `dotnet ef migrations script`, not from
  reading `Models/*.cs` — column types, precision, delete behaviour and index
  uniqueness live in Fluent API config and the Npgsql provider, so inferring
  them from the model classes produces a diagram that is wrong in exactly the
  places an interviewer would probe.
- `scripts/token-usage.py` — reads Claude Code's session transcripts and totals
  tokens per session, or per task within a session (`--task <prefix>`). The
  source for `docs/token-log.md`.
- `src/` — the actual .NET project.
- Root `README.md` — status table and quick start.

## When asked to move to the next phase

**Currently up next: Phase 2.4** (`docs/phases/phase-2.4-analytics.md`) — skill
demand and status funnel, as a read-only `Analytics` module. Its doc still tells you
to add `GetStatusFunnelAsync` to `IJobApplicationRepository`, which no longer exists;
write slices and correct the doc. `docs/README.md` has the full status table.

Read the relevant `docs/phases/phase-N-*.md` file first — it already has the
plan. Implement it, update that doc's "Status" field to "Done" when
working, and add any real deviations from the plan as notes in the doc
so it stays an accurate record (useful later for interview stories too).

When the phase is done, also **log what it cost**: run
`python scripts/token-usage.py`, add a row to the "By phase" table in
`docs/token-log.md`, and refresh its session table. The transcripts this reads
are local and not kept forever, so a phase that isn't logged when it ends may
not be recoverable later.

The phase docs were written before the architecture record. If a phase doc
contradicts `docs/architecture.md`, follow `architecture.md` and fix the
phase doc as part of the work.
