# Phase 2.4 — Analytics endpoints

**Status: Not started**

> **Architecture note (2026-08-25):** build this as vertical slices under
> `src/Modules/`, per `docs/architecture.md`. Do **not** add methods to
> `IJobApplicationRepository` — it is retiring, not growing.

## Goal

Expose the analytics the relational model was *chosen for* via the API:

1. **Skill demand** — top in-demand skills across all your tracked postings
   (`GROUP BY` over the shared `skills` table).
2. **Status funnel** — count of applications by status
   (Applied / Interviewing / Offer / Rejected / Withdrawn).

Optionally: **roles per company** (count of postings/applications per company).

## Why this is Phase 2 work

The phase-2 doc names "top in-demand skills across all my tracked jobs ... a
single `GROUP BY`" as **the concrete business payoff** that justified Postgres
over DynamoDB. Right now that query only exists as a psql one-liner in the doc —
this sub-phase turns the phase's headline justification into an actual feature.

## Scope

- Result DTOs: `SkillDemand { Name, Category?, Count }`,
  `StatusCount { Status, Count }`, (optional) `CompanyRollup { Name, Count }`.
- One slice per query under `src/Modules/Analytics/`, each computing its
  aggregate **in the database** (EF `GroupBy` → SQL `GROUP BY`, not in-memory
  over a full table scan):
  - `SkillDemand.cs` (top N, default 20)
  - `StatusFunnel.cs`
  - (optional) `CompanyRollup.cs`

  Analytics is a **read-only** module: it reads `skills` and `posting_skills`
  and owns no tables of its own. Handlers take `AppDbContext` directly — this
  supersedes the earlier plan to add `GetSkillDemandAsync` /
  `GetStatusFunnelAsync` to `IJobApplicationRepository`.
- REST: `GET /stats/skill-demand`, `GET /stats/funnel`,
  (optional) `GET /stats/companies`.
- GraphQL: query fields `skillDemand`, `statusFunnel`, (optional) `companyRollup`.

## Out of scope

- Charts / any UI — that's Phase 6. This sub-phase returns JSON only.
- Time-series trends (applications per week, etc.) — nice later, not needed to
  demonstrate the payoff.

## Cost

Zero — local Postgres, no new packages.

## Verify locally

- Seed a few applications with overlapping skills, then
  `GET /stats/skill-demand` returns skills ordered by descending count, matching
  the psql query in the phase-2 doc:
  `SELECT s."Name", COUNT(*) FROM skills s JOIN posting_skills ps ON ps."SkillId"=s."Id" GROUP BY s."Name" ORDER BY 2 DESC;`
- `GET /stats/funnel` counts sum to the total number of applications.
- Confirm via EF logging that the aggregation runs as SQL `GROUP BY`, not a
  client-side count over a full load.

## Interview talking points

- The single strongest "why relational" story in the whole project: a
  normalized shared-`skills` table turning "what skills should I learn?" into one
  `GROUP BY`, which is genuinely awkward in a denormalized NoSQL model.
- Pushing aggregation into the database vs. loading rows and counting in C# — a
  correctness/performance decision you can articulate.

## Next

Phase 2.5 — enforce the application status lifecycle.
