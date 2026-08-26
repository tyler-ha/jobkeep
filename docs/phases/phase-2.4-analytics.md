# Phase 2.4 — Analytics endpoints

**Status: Done** (2026-08-26)

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

## What actually shipped

`src/Modules/Analytics/` — three slices, `AnalyticsModule.cs` for DI and the
`/stats` route group, three GraphQL fields on `Query`, and
`tests/Jobkeep.Tests/Analytics/AnalyticsTests.cs` (15 tests). Suite is 101 green.

| | REST | GraphQL |
|---|---|---|
| Skill demand | `GET /stats/skill-demand?top=` | `skillDemand(top:)` |
| Status funnel | `GET /stats/funnel` | `statusFunnel` |
| Company rollup | `GET /stats/companies?top=` | `companyRollup(top:)` |

## Deviations from the plan

**1. The optional company rollup was built.** Not for completeness — because
`Company.cs` justifies storing an employer as its own row with the words *"that's
what enables company-level rollups like '3 roles at Canva'"*, and nothing in the
API had ever asked that question. A normalization decision defended by a query
nobody runs is the kind of claim that falls over when an interviewer pokes it.
Forty lines, one `GROUP BY`, promise now executable.

**2. It counts applications per company, not postings.** Grouping from the
application side is one `GROUP BY` with two joins; walking `companies` and
counting each one's postings' applications is a correlated subquery per row. The
consequence is written into the slice rather than hidden: a company with postings
but *no* application never appears, because it has no row to group. That state is
currently unreachable — companies only exist via `CreateApplication`'s
find-or-create — so the alternative would be paying for a case that cannot happen.
If a posting-only import path ever lands (scraping, Phase 4), this becomes a real
omission and the query has to start from `companies` with a `LEFT JOIN`.

**3. The repository warning was already spent.** `CLAUDE.md` sent this phase in
expecting to find `GetSkillDemandAsync` / `GetStatusFunnelAsync` still specified
against `IJobApplicationRepository` and to have to correct the doc. It didn't:
Phase 2.3 had already rewritten the Scope section to say handlers take
`AppDbContext` directly. Nothing to fix here — `CLAUDE.md`'s "up next" note is
what was stale, and it has been updated.

**4. The module boundary got bent, deliberately.** This is the phase's real
decision and it was not anticipated in the plan. `architecture.md` rule 2 says a
module queries only the tables it owns; the ownership table assigned Analytics
`skills` + `posting_skills`. But the funnel counts `job_applications` and the
rollup joins `job_postings` and `companies` — all owned by Applications. The
options were a contract on Applications (one method per analytics question, which
is `IJobApplicationRepository` coming back under a new name one phase after it was
deleted for growing exactly that way), splitting the feature across two modules on
a technicality, or letting a read-only reporting module read across and writing
down the cost. Took the third. Recorded as **decision 13**, the ownership table
and the architecture diagram's caption both corrected, and the reasoning lives in
`AnalyticsModule.cs` where someone adding a slice will read it.

**5. `int? top` instead of an `[AsParameters]` query record.** `ListApplications`
uses a record because ten filters have to stay in step across REST, GraphQL and
Swagger. One optional scalar does not earn an input type, and `skillDemand(top: 5)`
reads better than `skillDemand(query: { top: 5 })`. It is capped at 100 and
**rejects** out of range rather than clamping — same reasoning as `pageSize`: this
is an unauthenticated surface, and a caller silently given 100 rows when it asked
for 1000 has no way to tell.

**6. The funnel zero-fills its empty stages in C#.** A stage with no applications
has no row to `GROUP BY`, so SQL cannot return it — and "no offers yet" is exactly
the fact the view exists to show. This is not a breach of *aggregate in SQL*: the
counting is in SQL, and the fill is a loop over the five values of an enum, not
over table rows. It stays O(stages) however many applications exist.

**7. The known case-sensitivity gap is now a passing test.** `skills` dedups
case-sensitively, so `C#` and `c#` are two rows that split one skill's count —
and a demand ranking is precisely what a duplicate row corrupts. Written as
`SkillDemand_SplitsSkillsDifferingOnlyInCase_WhichIsTheKnownDedupGap`, asserting
the defect, the way the Phase 2.2 parity tests recorded theirs. When the
case-insensitive natural key lands, that test fails, and the failure is the signal
the fix worked. Not fixed here: it is a migration, so it is its own phase.

## Verified

Ran against local Postgres with EF command logging on. All three aggregates
translate to real SQL, with `LIMIT` pushed down — not a load-and-count in C#:

```sql
SELECT s."Name", s."Category", count(*)::int
FROM posting_skills AS p INNER JOIN skills AS s ON p."SkillId" = s."Id"
GROUP BY s."Name", s."Category" ORDER BY count(*)::int DESC, s."Name" LIMIT @__p_0

SELECT j."Status", count(*)::int AS "Count" FROM job_applications AS j GROUP BY j."Status"

SELECT c."Name", count(*)::int
FROM job_applications AS j
  INNER JOIN job_postings AS j0 ON j."PostingId" = j0."Id"
  INNER JOIN companies AS c ON j0."CompanyId" = c."Id"
GROUP BY c."Name" ORDER BY count(*)::int DESC, c."Name" LIMIT @__p_0
```

Worth knowing for later: since EF Core 3.0 an untranslatable `GroupBy` **throws**
rather than falling back to client evaluation. So the integration tests passing
against real Postgres is itself the "aggregation happens in the database" check —
which is also why a fake repository would have reported all fifteen green while
proving nothing.

`/stats/funnel` totals matched `SELECT COUNT(*) FROM job_applications`, and
`/stats/skill-demand` matched the psql one-liner from the phase-2 doc, both as
assertions rather than by hand.

## Next

Phase 2.5 — enforce the application status lifecycle.
