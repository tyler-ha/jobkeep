# Phase 2.3 — Query, filter, sort & page the list

**Status: Done** (2026-08-26)

> **Architecture note (2026-08-25):** build this as vertical slices under
> `src/Modules/`, per `docs/architecture.md`. Do **not** add methods to
> `IJobApplicationRepository` — it is retiring, not growing.
>
> It retired. See "What actually happened".

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

Zero — local Postgres, no new packages. **As built: still zero, and deliberately so
— see deviation 2.**

## Verify locally

- `GET /applications?status=Interviewing` returns only interviewing rows.
- `GET /applications?skill=C%23` returns only applications whose posting lists C#.
- `GET /applications?sort=dateApplied&page=2&pageSize=5` pages correctly and
  `totalCount` matches the unfiltered total.

---

## What actually happened

Four deviations, three of them deliberate widenings and one a narrowing.

### 1. The repository retired whole, not by halves

The scope above says "retire `GetAllAsync` / `GetByIdAsync`" — the read half. But
the same section claims this phase is where `ReferenceHandler.IgnoreCycles` "stops
being load-bearing," and those two statements cannot both hold: the flag existed
because endpoints returned EF entities, and `CreateAsync` / `UpdateAsync` would
still have been returning them. Half the migration leaves the band-aid on.

So create, update and delete became slices too, and the phase deleted
`Repositories/`, `Endpoints/ApplicationEndpoints.cs` and `Models/Dtos.cs`. `src/`
now has **one** shape rather than two, roughly a phase earlier than planned.

What that bought, beyond the stated goal:

| Finding | Status after this phase |
|---|---|
| **A2** — EF entities as the API contract | Fixed. Every route returns a DTO; `IgnoreCycles` is gone from `Program.cs`. |
| **A3** — the repository is the wrong abstraction | Fixed. There is no repository. |
| **A4** — validation is surface-specific | Fixed. `createApplication` over GraphQL now rejects the blank title REST always rejected, and `PATCH` validates what it is sent. |
| **A7** — EF entities reachable through the GraphQL schema | Fixed, and this one was not planned — see deviation 3. |

### 2. A1 is *narrowed*, not closed — and no package was added

The scope says to push projection into HotChocolate. Doing that properly needs
`HotChocolate.Data`, which is not referenced, and `[UseProjection]` only works over
an `IQueryable` of the **entity** — so the resolver would have to return
`IQueryable<JobApplication>` again. That reinstates A7 (below) to fix A1, and puts
EF entities back in the published schema in the same move.

What was built instead: every handler ends in a flat `.Select(...)` into its DTO.
That is a real, measurable fix — a list request is now **two** SQL statements (a
`count(*)` and one paged `SELECT` of named columns) where the old include graph was
five round-trips behind `AsSplitQuery`, and no request loads `Description`,
`ResumeText`, `AiAnalysis` or `AtsResult` unless it is asked for.

**The residual gap, stated plainly:** this is aggregate-level, not per-field. A
GraphQL query selecting only `title` still loads every column in
`ApplicationListItem`. That is recorded in `architecture.md` A1 rather than
described as done.

### 3. A7 closed as a side effect

Not in the plan, and worth recording because of *why* it fell out. HotChocolate
builds the schema from resolver return types. While the resolvers returned
`JobApplication`, the schema published its navigation properties too — the
`[JsonIgnore]` attributes that hide back-references from REST mean nothing to
HotChocolate, which honours `[GraphQLIgnore]`. A client could walk
`application → posting → company → postings → applications → resumeText` and read
every résumé in the database.

Once every root field returns a DTO, the entity types are not in the schema at all.
The walk is closed by construction rather than by remembering to annotate each new
navigation property. `SurfaceParityTests.NoEfEntityIsReachableFromTheGraphQLSchema`
asserts it against the emitted SDL — the same way the finding was originally made,
rather than by reasoning about the attributes.

### 4. No index migration

The scope did not mention indexes, but adding filtering to unindexed columns
invites the question. `job_applications` has no index on `Status` or `DateApplied`,
and this phase deliberately did not add one: at a few hundred personal rows
Postgres seq-scans in well under a millisecond, and the gap register already parks
"the two missing indexes" in the Phase 2.7 audit-and-integrity migration. Keeping
them there leaves 2.7 as one migration instead of two, and leaves the ERD unmoved
by this phase.

### Smaller decisions worth defending

- **Nullable everything in `ApplicationQuery`.** `[AsParameters]` binds each
  property independently and treats a non-nullable value type as *required*, so
  `?sort=` omitted returned `400 Required parameter "ApplicationSort Sort" was not
  provided` — a property initializer does not change that. Every property is
  nullable and the defaults are applied once, in the handler, so "omitted" is
  expressible on both surfaces.
- **`ThenBy(a => a.Id)` on every sort.** `DateApplied` is a `DateOnly`, so same-day
  rows tie, and OFFSET paging over a non-deterministic `ORDER BY` can return one row
  on two pages while never returning another. Phase 2.2 met the same tie from the
  other side and had to drop an ordering assertion as flaky; the tiebreak is what
  makes it assertable, and `Paging_IsStableAcrossPages_...` is that assertion.
- **Invalid paging is rejected, not clamped.** Turning `?page=0` into page 1 hands
  the caller a page they did not ask for with no way to notice. The `pageSize`
  ceiling of 100 is also the only thing between an unauthenticated `GET` and
  `?pageSize=1000000`.
- **`%` and `_` escaped before they reach ILIKE.** Both are wildcards, so an
  unescaped search for `A_lassian` matches `Atlassian`. Not a security hole — the
  value is parameterised, never concatenated — but a correctness one.
- **Skill matches exactly (case-insensitively); company and title match anywhere.**
  A skill list is full of names that are prefixes of each other (`C`, `C#`, `C++`),
  so a contains-match makes the filter useless.
- **`ApplicationPage` is concrete, not `PagedResult<T>`.** HotChocolate names GraphQL
  types after the CLR type, and a generic lands in the SDL as a generated name. The
  cost: Phase 2.4 declares its own page type.

### Breaking changes

- `GET /applications` returns `{ items, totalCount, page, pageSize, totalPages }`
  instead of a bare array.
- Response bodies are DTOs: `posting.postingSkills[].skill.name` is now
  `posting.skills[].skillName`, and `postingId` / `companyId` are gone.
- `GET /applications/{id}` on an unknown id still 404s but now carries a message
  body where it used to be empty.
- GraphQL `deleteApplication` on an unknown id raises `NOT_FOUND` instead of
  returning `false` — `false` read identically to a successful no-op, and REST has
  always answered 404.

### Tests

86 passing, up from 55. New: `Rest/ListApplicationsTests.cs` (filters, sorting,
paging stability, validation, and the projection assertion that pins A1). Updated:
`SmokeTests` for the paged envelope, `ApplicationsCrudTests` for the DTO shape and
the new `PATCH` validation, `DeleteBehaviourTests` for the renamed skills field.
The two `A4_` tests in `SurfaceParityTests` were written to fail the day the
finding was fixed; they did, and they are flipped, with the old assertion recorded
in each comment.

## Interview talking points

- Filtering by a shared skill = one JOIN over the normalized `skills` table; the
  concrete "why relational" example.
- **Fixing one finding by reinstating another.** The obvious way to close A1
  (GraphQL over-fetch) was `HotChocolate.Data`'s `[UseProjection]` — which requires
  resolvers to return `IQueryable<JobApplication>`, putting EF entities back in the
  published schema and reopening A7. Taking the partial fix and writing down what
  it does not cover was the better trade, and being able to say *why* is the point.
- Deleting an abstraction rather than growing it: the repository went from "never
  bypass this" (Phase 1-2) to "retiring, not growing" (2.1) to gone (2.3), with each
  reversal recorded rather than quietly applied.

## Next

Phase 2.4 — analytics endpoints (skill demand, status funnel).
