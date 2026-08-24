# Phase 2.2 — Query, filter, sort & page the list

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
- New repo method `Task<PagedResult<JobApplication>> QueryAsync(ApplicationQuery q)`
  returning `{ Items, TotalCount, Page, PageSize }`. Reimplement `GetAllAsync` in
  terms of it (or keep both) so nothing else breaks.
- REST `GET /applications?status=&company=&skill=&from=&to=&sort=&page=&pageSize=`.
- GraphQL: `applications(filter: ..., page: ..., pageSize: ...)` returning a paged
  payload.
- Implement filtering **inside the repository** (translated to EF `Where`/
  `OrderBy`/`Skip`/`Take`), *not* via HotChocolate's `[UseFiltering]`/`[UsePaging]`
  attributes.

## Design note — why manual filtering, not HotChocolate pushdown

The phase-2 doc already flagged this: HotChocolate's `IQueryable` projection/
filtering is more efficient at scale but **bypasses `IJobApplicationRepository`**,
which CLAUDE.md forbids. At personal volume the repo-level filter is plenty fast
and keeps both API surfaces on one storage contract. Record the tradeoff rather
than silently taking the shortcut — it's a good "I chose the boundary on purpose"
interview beat.

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

Phase 2.3 — analytics endpoints (skill demand, status funnel).
