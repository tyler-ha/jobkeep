# CLAUDE.md

Context for Claude Code (or any Claude session) working in this repo.

## What this project is

A personal job-application tracker, built as a portfolio project by
someone with beginner-to-intermediate C# skills who is actively learning
AWS and preparing for a job search in the Melbourne market in ~1 year.
Also used as prep material for behavioral (Leadership Principle style)
interview stories — see "STAR log" in the root README.

## Priorities, in order

1. **Cost stays near-zero.** Every AWS/AI choice should default to free
   tier or on-demand/serverless pricing. Never suggest always-on
   infrastructure (e.g. a provisioned EC2 instance or provisioned-capacity
   DynamoDB) without flagging the cost tradeoff explicitly. Note: storage is
   now PostgreSQL (see Architecture), which is *not* serverless — the deployed
   DB runs on AWS RDS **free-tier** (free for 12 months, then always-on and
   billable). Local dev uses Postgres in Docker (free). This tradeoff was made
   deliberately for a cleaner relational model; keep flagging it.
2. **Each phase should end in something runnable.** The person has a
   history of abandoning projects when scope gets fuzzy. Don't let a
   phase sprawl — if a change is getting large, suggest splitting it.
2. **Explain, don't just generate.** The person is using this project to
   build real understanding (for interviews) as much as to build the app
   itself. Prefer short explanations of *why* alongside code changes,
   especially around design decisions (interfaces, AWS service choices,
   AI provider abstractions).
4. **Local-first development.** Prefer developing against local/free
   equivalents (Postgres in Docker, Ollama) before touching real AWS or paid
   APIs, matching the pattern already established in Phases 1-2.

## Architecture

- **Backend**: ASP.NET Core 8 minimal API (`src/Program.cs`).
- **API surfaces**: REST (minimal-API endpoints) **and** GraphQL
  (HotChocolate, `src/GraphQL/`, served at `/graphql`). Both sit on the same
  repository — GraphQL didn't replace REST. Added in Phase 2b.
- **Storage**: **PostgreSQL via EF Core** (Phase 2), behind
  `IJobApplicationRepository`. `PostgresJobApplicationRepository` is the real
  implementation; `InMemoryJobApplicationRepository` is kept as a no-DB dev
  fallback. Never bypass this interface; new storage backends implement it.
  DB schema lives in `src/Data/AppDbContext.cs` (+ EF migrations in
  `src/Migrations/`). NOTE: an earlier draft of Phase 2 used DynamoDB; that
  was dropped in favour of a normalized relational model — see the Phase 2 doc.
- **AI calls**: planned to go behind `Microsoft.Extensions.AI`'s
  `IChatClient` abstraction (Phase 4), so Ollama (local, free) and a
  hosted API (deployed) are swappable via config, not code changes.
- **Deployment target**: AWS Lambda + API Gateway for the app (serverless,
  pay-per-use — see Phase 3 doc), with PostgreSQL on AWS RDS free-tier. Both
  the REST and GraphQL endpoints ride the same Lambda.

## Where things are

- `docs/phase-N-*.md` — the plan and status for each build phase, in
  order. Check the current phase's doc before making changes so new
  work matches the intended scope for that stage.
- `src/` — the actual .NET project.
- Root `README.md` — status table and quick start.

## Conventions

- Target framework: `net8.0`.
- Minimal API style (no controllers) — keep `Program.cs` as the single
  place endpoints are defined unless it grows large enough to warrant
  splitting into endpoint group files.
- Nullable reference types enabled — respect existing nullability
  annotations rather than suppressing warnings.
- Keep new NuGet dependencies minimal and justify additions in the
  relevant phase doc.

## When asked to move to the next phase

Read the relevant `docs/phase-N-*.md` file first — it already has the
plan. Implement it, update that doc's "Status" field to "Done" when
working, and add any real deviations from the plan as notes in the doc
so it stays an accurate record (useful later for interview stories too).
