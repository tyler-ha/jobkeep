# Phase 2 — Relational model on PostgreSQL + GraphQL

**Status: Done** (Phase 2a + 2b complete, verified locally)

## Goal

Replace the thin Phase-1 record with a **richer relational domain model** that
mirrors a real job posting (company, posting, skills, requirements, application),
store it in **PostgreSQL via EF Core**, and put a **GraphQL** API in front —
kept alongside the existing REST endpoints. Develop against a free local Postgres
(Docker) before touching AWS, so this phase still costs nothing.

## Why we dropped DynamoDB

An earlier draft of this phase used DynamoDB. We changed course to Postgres:

- The domain is **naturally relational** — a posting has many skills and
  requirements; skills are **shared** across postings. In DynamoDB that means
  either denormalizing (losing query flexibility) or hand-building single-table
  designs, which are hard to reason about long-term.
- Postgres gives real **joins**, a self-documenting schema, and lets the shared
  `skills` table answer questions like *"top in-demand skills across all my
  tracked jobs"* with a single `GROUP BY`. That analytic is the concrete
  business payoff, and it's painful in NoSQL.
- Tradeoff, honestly recorded: Postgres isn't serverless. Local dev is free
  (Docker); the deployed DB will run on **AWS RDS free-tier** (free 12 months,
  then always-on/billable). See Phase 3.

*(Interview story: choosing a datastore by matching it to the access patterns —
relational normalization vs. NoSQL denormalization — not by defaulting to one.)*

## Data model

Aggregate root is still `JobApplication` (so `IJobApplicationRepository` keeps
its contract), now referencing a `JobPosting`. Enums are stored as **strings**
so rows are self-documenting in psql.

```
companies ─< job_postings ─< posting_skills >─ skills   (skills SHARED)
                   │            (IsRequired, Source)
                   ├─< job_requirements
                   └── ai_analyses (1:1)
job_postings ─< job_applications ─── ats_results (1:1)
```

| Table | Business value |
|---|---|
| `companies` | dedupe employers; company-level rollups |
| `job_postings` | the ad; the unit Phase 4 analyzes |
| `skills` | **shared/normalized** → skill-demand analytics in one GROUP BY |
| `posting_skills` (join) | must-have vs nice-to-have; human- vs AI-sourced |
| `job_requirements` | structured requirements for the Phase 5 ATS check |
| `job_applications` | your tracking record — the core |
| `ai_analyses` | Phase 4 output |
| `ats_results` | Phase 5 output (keyword lists as Postgres `text[]`) |

Key rules (Fluent API in `src/Data/AppDbContext.cs`): unique index on
`companies.Name` and `skills.Name` (backs find-or-create dedup); delete a
posting cascades to its skills/requirements/analysis; delete an application
cascades to its ATS result but **leaves the posting** (Restrict).

## Delivered in two runnable sub-phases

**Phase 2a — model + EF Core + Postgres (REST).**
- New entity classes in `src/Models/`, `AppDbContext` + `InitialCreate`
  migration, `PostgresJobApplicationRepository` (implements the interface with
  `.Include(...)` for the full aggregate + find-or-create Company/Skill by name).
- `Program.cs`: `AddDbContext` (Npgsql from `ConnectionStrings:Postgres`),
  repo registered **Scoped**, Development-only `Database.Migrate()` on startup.
- Packages added: `Npgsql.EntityFrameworkCore.PostgreSQL`,
  `Microsoft.EntityFrameworkCore.Design` (pinned to EF **8.x** for `net8.0`);
  `dotnet-ef` as a local tool (`dotnet-tools.json`). DynamoDB package removed.

**Phase 2b — GraphQL via HotChocolate.**
- `src/GraphQL/Query.cs` + `Mutation.cs` (thin resolvers reusing the repo),
  `app.MapGraphQL()`. Package: `HotChocolate.AspNetCore` (14.x).
- **Why HotChocolate, not AWS AppSync:** it runs in-process on the same
  ASP.NET app, so it rides the same Lambda deploy in Phase 3 (no new infra/cost),
  stays C#, and reuses the existing repository via DI. AppSync would be a
  separate managed service with its own resolvers/cost — overkill for one user.

## Notes / deviations worth recording

- **Enums now serialize as names** on both surfaces. Added a
  `JsonStringEnumConverter` so REST speaks `"Interviewing"`/`"FullTime"` (fixing
  the Phase-1 int quirk); GraphQL uses SCREAMING_CASE (`APPLIED`, `FULL_TIME`).
- **REST returns EF entities**, so back-reference navigations are marked
  `[JsonIgnore]` to avoid serialization cycles / `[null]` arrays. GraphQL is
  immune (it resolves only requested fields). Returning DTOs instead is a future
  option if the coupling ever bites.
- **EF "set-key means existing" gotcha.** Because entity `Id`s are initialized
  with `Guid.NewGuid()`, a new `Skill` reached via a navigation off an
  already-tracked application is assumed by EF to already exist and skipped on
  insert (FK violation). Fixed by explicitly `_db.Skills.Add(skill)` in
  `AddSkillToPostingAsync`. (`CreateAsync` is unaffected — `Add(root)`
  force-cascades `Added` to the whole graph.)
- **`IJobApplicationRepository` kept** (CLAUDE.md forbids bypassing it). Using
  HotChocolate's `IQueryable`/projection pushdown would be more efficient at
  scale but bypasses the repo — deliberately deferred at personal volume.

## Cost notes

- Local Postgres in Docker: free. `docker run -d -p 5432:5432 -e
  POSTGRES_PASSWORD=dev -e POSTGRES_DB=jobkeep postgres:16-alpine`.
- Deployed (Phase 3): AWS RDS free-tier — free for 12 months (covers the
  ~1-year job-search runway), then always-on/billable. Tear down afterwards.

## Interview talking points from this phase

- Picking a datastore by access pattern (relational vs NoSQL), and being able
  to explain the tradeoff both ways.
- Normalization for a real payoff (shared-skill analytics), not dogma.
- One repository, two API surfaces (REST + GraphQL) — an abstraction paying off.
- A concrete EF Core change-tracking bug and its root cause.

## Verify locally

```bash
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=jobkeep postgres:16-alpine
cd src && dotnet run        # auto-applies migrations in Development
```
- REST: `curl -X POST localhost:5080/applications -H 'Content-Type: application/json' -d '{"company":"Canva","title":"Backend Engineer"}'`
- GraphQL IDE: open `http://localhost:5080/graphql` (Nitro).
- Skill-demand payoff (psql): `SELECT s."Name", COUNT(*) FROM skills s JOIN posting_skills ps ON ps."SkillId"=s."Id" GROUP BY s."Name" ORDER BY 2 DESC;`

## Next

Before deploying, close the gaps in the model surface built here — a set of
runnable sub-phases, each in its own doc:

- **Phase 2.1** (`phase-2.1-write-surface.md`) — complete the write surface:
  skills over REST + requirements CRUD (the `job_requirements` table is
  currently write-unreachable).
- **Phase 2.2** (`phase-2.2-tests-and-ci.md`) — automated tests + CI. Scheduled
  before the remaining features, because the delete behaviour and find-or-create
  dedup this phase designed had nothing verifying them.
- **Phase 2.3** (`phase-2.3-list-queries.md`) — filter / sort / page the list.
- **Phase 2.4** (`phase-2.4-analytics.md`) — analytics endpoints (skill demand,
  status funnel) — exposes the `GROUP BY` payoff this phase was justified by.
- **Phase 2.5** (`phase-2.5-status-rules.md`) — enforce the status lifecycle.
- **Phase 2.6** (`phase-2.6-dotnet10-upgrade.md`) — move to .NET 10 LTS, since
  .NET 8 goes out of support 10 Nov 2026. A prerequisite for Phase 3.

Then Phase 3 — deploy the API to AWS Lambda + API Gateway, with Postgres on RDS.
