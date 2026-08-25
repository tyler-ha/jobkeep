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
src/Shared/                          — AppDbContext, cross-cutting contracts
src/Program.cs                       — wiring only (DI, middleware, Map* calls)
```

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

The code has **not** been restructured yet — this is deliberate, so each phase
stays runnable. As of 2026-08-25 `src/` still has `Endpoints/` and
`Repositories/` from Phase 2, and `IJobApplicationRepository` is still wired up.

- **New** code: write it as a slice in a module.
- **Existing** code: migrate the parts a phase actually touches. Don't do a
  sweeping refactor as a side effect of a feature.
- `IJobApplicationRepository` is **retiring, not growing.** Do not add methods
  to it. If a phase doc says to (Phase 2.3 does), write a slice instead and
  correct the phase doc.

Superseded rules from earlier versions of this file, kept here so their
reversal is legible: *"never bypass `IJobApplicationRepository`"* and *"keep
`Program.cs` as the single place endpoints are defined"* are both retired.
See `docs/architecture.md` decision 5.

## What good looks like here

- **Aggregate in SQL, not in memory.** EF `GroupBy` that translates to a
  real `GROUP BY` — never load a table and count in C#.
- **DTOs at the edge.** Don't return EF entities from endpoints or resolvers;
  the API contract shouldn't move every time the schema does. (The
  `ReferenceHandler.IgnoreCycles` setting in `Program.cs` is a symptom of the
  current violation, not a preference.)
- **One rule, one implementation.** Validation and business rules live in the
  slice, so REST and GraphQL can't enforce different things.
- **GraphQL should fetch what was asked for.** Resolvers currently eager-load
  the whole object graph regardless of the query — see `architecture.md` A1.
- **Write down the tradeoff.** Existing comments explain *why* (captive
  dependency, `AsSplitQuery`, delete behaviour). Match that density; it's the
  interview material.

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

There is **no test project.** Verify changes by building, then exercising the
endpoint via Swagger, the Nitro IDE, or curl against a running local Postgres.
If you add tests, note it here.

## Gotchas

- **Keep `src/` to one solution file.** Visual Studio has twice recreated a
  stale `JobTracker.slnx` (it points at a `JobTracker.csproj` that doesn't
  exist). With two `.slnx` files present, bare `dotnet build` / `dotnet run`
  fail with `MSB1011: Specify which project or solution file to use`. Delete
  the stray file, or pass `Jobkeep.slnx` / `Jobkeep.csproj` explicitly.
- **Migrations auto-apply on startup in Development only** (`Program.cs`).
  Deployed environments are expected to apply them as a deliberate release step.
- **Enums serialize by name**, not int — `"Interviewing"` over REST,
  `INTERVIEWING` over GraphQL.
- **`ReferenceHandler.IgnoreCycles` is load-bearing.** EF navigation properties
  (posting ↔ skills) cycle; removing it breaks REST serialization. GraphQL is
  unaffected since it resolves only requested fields.
- **`appsettings*.json` contain `//` comments.** ASP.NET's config reader accepts
  them; strict JSON parsers won't. Don't "fix" them.

## Conventions

- Target framework: `net8.0`. **Framework deadline: .NET 8 reaches end of
  support 10 Nov 2026.** The upgrade to `net10.0` (LTS to Nov 2028) is
  Phase 2.5, before the AWS deploy. Flag this if Phase 3 starts first.
- Nullable reference types enabled — respect existing nullability
  annotations rather than suppressing warnings.
- Enums serialize by *name* on both surfaces, and are stored as strings.
- Keep new NuGet dependencies minimal and justify additions in the
  relevant phase doc.
- Migrations auto-apply in Development only; deployed environments apply
  them as a deliberate release step.

## Known gaps (don't re-discover these)

No tests, no CI, no `docker-compose`, no health check, no auth. These are
**recorded**, not forgotten — see the gap register in `docs/architecture.md`.
Automated tests + CI are the highest-value missing items for the portfolio,
above any further architecture work.

## Where things are

- `docs/architecture.md` — how the code is shaped, why, and the decision
  record. **Check this before proposing structural changes.**
- `docs/phase-N-*.md` — the plan and status for each build phase, in
  order. Check the current phase's doc before making changes so new
  work matches the intended scope for that stage.
- `docs/backlog.md` — considered-but-not-committed features, and the
  verified market comparison.
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
- `src/` — the actual .NET project.
- Root `README.md` — status table and quick start.

## When asked to move to the next phase

Read the relevant `docs/phase-N-*.md` file first — it already has the
plan. Implement it, update that doc's "Status" field to "Done" when
working, and add any real deviations from the plan as notes in the doc
so it stays an accurate record (useful later for interview stories too).

The phase docs were written before the architecture record. If a phase doc
contradicts `docs/architecture.md`, follow `architecture.md` and fix the
phase doc as part of the work.
