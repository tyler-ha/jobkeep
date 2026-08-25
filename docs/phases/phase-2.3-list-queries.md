# Phase 2.3 — Query, filter, sort & page the list

**Status: Not started**

> **Architecture note (2026-08-25):** build this as vertical slices under
> `src/Modules/`, per `docs/architecture.md`. Do **not** add methods to
> `IJobApplicationRepository` — it is retiring, not growing.

## Goal

Replace the single "all applications, newest first" read with a real query
surface: filter by status / company / skill / applied-date range, choose a sort,
and page the results.

## Why this is Phase 2 work

This is the payoff the phase-2 doc promised when it chose Postgres over DynamoDB:
"real joins and query flexibility." Filtering by a *shared skill* across all your
applications is exactly the query that's a single JOIN in Postgres and painful in
NoSQL — so building it here is the phase's thesis paying off, not new scope.

## Scope

- A query object, e.g. `ApplicationQuery { Status?, Company?, Skill?, AppliedFrom?,
  AppliedTo?, Sort, Page = 1, PageSize = 20 }`.
- A slice, `src/Modules/Applications/ListApplications.cs`, holding the query, the
  handler and the response together. It returns `SliceResult<PagedResult<...>>`
  where `PagedResult` is `{ Items, TotalCount, Page, PageSize }`, and `Items` are
  **response DTOs, not `JobApplication` entities** — this is where the rest of A2
  gets fixed and `ReferenceHandler.IgnoreCycles` stops being load-bearing.
- Retire `GetAllAsync` / `GetByIdAsync` from `IJobApplicationRepository` once both
  surfaces read through the slice. Per architecture.md decision 5 the interface is
  retiring, not growing — Phase 2.1 already made it smaller, and this phase is what
  removes the read half.
- REST `GET /applications?status=&company=&skill=&from=&to=&sort=&page=&pageSize=`.
- GraphQL: `applications(filter: ..., page: ..., pageSize: ...)` returning a paged
  payload.
- Build the filter as EF `Where` / `OrderBy` / `Skip` / `Take` composed inside the
  handler, so **one implementation serves both surfaces** and neither can filter by
  a rule the other does not have.
- Fold in **A1**: have the GraphQL resolver project over `IQueryable` rather than
  calling the eager-loading include graph, so a query asking for `title` stops
  loading company + skills + requirements + AI analysis + ATS result.

## Design note — where filtering lives, and why

**Corrected 2026-08-25.** An earlier version of this doc said to add a `QueryAsync`
method to `IJobApplicationRepository` and to avoid HotChocolate's `IQueryable`
pushdown because it "bypasses `IJobApplicationRepository`, which CLAUDE.md forbids."

That rule has since been **retired** — see `architecture.md` decision 5 and the
superseded-rules note in `CLAUDE.md`. The repository is being dismantled, so
"bypasses the repository" is no longer an argument against anything.

The reasoning that survives is narrower and still worth writing down: filtering is a
**business rule**, so it belongs in the slice handler where REST and GraphQL both
reach it. Projection is a **transport concern** — which fields this particular caller
asked for — so pushing that into HotChocolate is correct and is the fix for A1. The
line is rule versus shape, not repository versus resolver.

## Out of scope

- Full-text search over descriptions (Postgres `tsvector`) — overkill for one
  user; note it as a future option.
- Cursor-based paging — offset paging is fine at this volume.

## Cost

Zero — local Postgres, no new packages.

## Verify locally

- `GET /applications?status=Interviewing` returns only interviewing rows.
- `GET /applications?skill=C%23` returns only applications whose posting lists C#.
- `GET /applications?sort=dateApplied&page=2&pageSize=5` pages correctly and
  `totalCount` matches the unfiltered total.

## Interview talking points

- Filtering by a shared skill = one JOIN over the normalized `skills` table; the
  concrete "why relational" example.
- Deliberately keeping query logic behind the repository instead of leaking
  `IQueryable` to the GraphQL layer — an abstraction-boundary decision with a
  stated cost.

## Next

Phase 2.4 — analytics endpoints (skill demand, status funnel).
