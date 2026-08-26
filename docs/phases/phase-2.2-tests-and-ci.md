# Phase 2.2 — Automated tests + CI

**Status: Done** (2026-08-25).

**Scheduled immediately after 2.1, ahead of the remaining Phase 2 features.** The gap
register in `architecture.md` calls automated tests *"the single largest gap"* and
*"the strongest candidate to schedule next"*, and `backlog.md` is blunter: *"Not
deferred for a good reason… Should be scheduled as its own phase, ahead of further
architecture work."* Everything that was 2.2–2.5 shifted up one to make room, so the
phase numbers still read in build order.

## Goal

Close **A6** — *"No tests, no CI, no compose file, no health check"* — for its first two
items, using the tools the docs had already named: **xUnit + Testcontainers against
real Postgres**, and **GitHub Actions build + test on push**.

## Why this is Phase 2 work

Phase 2 chose Postgres over DynamoDB on one argument: a normalized, shared `skills`
table makes cross-posting analytics a single `GROUP BY`. Phase 2.1 then added four
slices and a second API surface on top of it. None of that had a single test.

So this is not new scope — it is verifying the thesis the phase was justified by,
before three more phases are built on it. The delete-behaviour matrix and the
find-or-create dedup are exactly the things a fake repository would have reported as
working while the SQL was wrong.

## What was built

```
tests/Jobkeep.Tests/
  Infrastructure/   PostgresFixture, JobkeepAppFactory, IntegrationTestBase,
                    GraphQLClient, ApiHelpers
  Persistence/      DedupTests, DeleteBehaviourTests, MappingTests
  Rest/             SmokeTests, ApplicationsCrudTests, SubResourceTests
  Parity/           SurfaceParityTests
.github/workflows/ci.yml
global.json
src/Jobkeep.http
```

**55 tests, all green, ~30s for a full run** including container startup.

One `PostgreSqlContainer` and one `WebApplicationFactory` per run, shared through an
xUnit collection fixture; `Respawn` truncates every table between tests. The factory
boots the real `Program.cs` in `Development`, so **EF migrations applying cleanly to an
empty database is a property of every run** rather than a separate test.

Tests are grouped by what they defend, not by class-under-test:

- **Persistence** — the assertions that only mean something against a real provider:
  find-or-create dedup on `companies.Name` and `skills.Name`, the delete-behaviour
  matrix (Restrict vs Cascade, both directions), `text[]`, `numeric(12,2)` rounding,
  `DateOnly` → `date`, enums stored as `varchar(20)`, the composite PK on
  `posting_skills`, and the 1:1 unique indexes.
- **REST** — exact status codes, because they are inconsistent by design: `POST
  /applications` is 201, `POST .../skills` is 200, every DELETE is 204, and errors are
  **bare JSON strings**, not ProblemDetails.
- **Parity** — the same bad input over both surfaces: 404 ↔ `NOT_FOUND`, 400 ↔
  `INVALID_INPUT`. This is decision 10 made executable.

## Deviations from the plan, and things learned

**1. The connection-string override did not work the obvious way — and nearly ate the
dev database.**

The plan called for `ConfigureAppConfiguration` to point the app at the container.
It silently did not take effect: `Program.cs:15` reads
`builder.Configuration.GetConnectionString("Postgres")` *before* `builder.Build()`,
while `ConfigureAppConfiguration` callbacks are applied *during* the build. So the app
resolved `localhost:5432` from `appsettings.Development.json` and **connected to the
real dev database**, while Respawn connected to the empty container and failed with
"No tables found".

Had Respawn instead succeeded against the dev connection, it would have truncated real
data. Fixed with `builder.UseSetting("ConnectionStrings:Postgres", …)`, which writes
into the `WebApplicationBuilder`'s configuration early enough for that first read.

**2. The safety guard was checking the wrong thing.**

`PostgresFixture` was written with a guard asserting the resolved connection points at
the container. It **passed while the app was talking to the dev database**, because it
read `IConfiguration` from the built host — which *did* carry the override, even though
Program.cs had already read the earlier value. It now asks the `AppDbContext` what it is
connected to, since only the DbContext knows where writes actually go.

Worth keeping as the interview story from this phase: *the guard I wrote to catch this
class of bug was itself fooled by it, and the fix was to assert on the component that
does the work rather than on the configuration that is supposed to describe it.*

**3. .NET 10 SDK broke `dotnet test` in a way that needed a `global.json`.**

xUnit v3 ships Microsoft.Testing.Platform, and on the .NET 10 SDK the old VSTest bridge
errors out: *"Testing with VSTest target is no longer supported."* Neither the
`TestingPlatformDotnetTestSupport` property nor a `dotnet.config` fixes it. The opt-in
is a repo-root `global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

Two consequences worth remembering: the project path must now be passed as
`dotnet test --project <path>` rather than positionally, and **CI has to install both
the 8.0 and 10.0 SDKs** — 8.0 to run `net8.0` tests, 10.0 because only that SDK
understands the `test` section. No SDK version is pinned in `global.json`, so nothing
about the existing build or the `dotnet-ef` 8.0.11 tool changes.

**4. A latent EF version split surfaced.**

The app pairs `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.10 (wants EF 8.0.10) with
`Microsoft.EntityFrameworkCore.Design` 8.0.11 (wants 8.0.11), and Design wins inside
`src`. Invisible until a second project referenced the app, at which point MSBuild
reported an MSB3277 unification conflict. Worked around by naming both EF packages at
8.0.11 in the test project. **`src` was deliberately not changed** — Phase 2.6 removes
the split for real when it bumps everything to net10-era versions.

> **Corrected after Phase 2.6 (2026-08-26).** It didn't. The *major-line* split
> went away — everything resolves EF 10.0.11 now — but the workaround is still
> required, because the cause was never the major version: Npgsql declares a
> **range** (`[10.0.4, 11.0.0)`) while EF Design pins an **exact** version, so a
> transitive-only reference resolves to the floor. Removing the two pins was
> tried during 2.6 and fails with CS1705. See `phase-2.6-dotnet10-upgrade.md`.

The CI note above is also superseded: since Phase 2.6 the workflow installs the
10.0.x SDK alone, which both runs the `net10.0` tests and reads `global.json`.

**5. Two known asymmetries are asserted as-is, not fixed.**

`SurfaceParityTests` contains two tests prefixed `A4_` that assert what the code does
*today*:

- `createApplication` over **GraphQL accepts a blank company/title**, while REST returns
  400 — the mutation calls the repository directly and never runs the endpoint's null
  check.
- `PATCH /applications/{id}` has **no validation at all**, so `{"title": ""}` writes an
  empty title that `POST` would have rejected.

Both are `architecture.md` **A4**. A skipped test would rot and a test asserting the
*desired* behaviour would just fail and get muted; asserting current behaviour means
that when Phase 2.3 or 2.5 centralises validation, these fail loudly and get flipped.

**6. A new finding: skill dedup is case-sensitive.**

`AddSkillToPosting` matches on `s.Name == skillName`, which Npgsql translates to a
case-sensitive comparison — so `"C#"` and `"c#"` become **two rows** in the table whose
entire purpose is deduplication, and skill-demand analytics will double-count them.
Asserted as current behaviour in `DedupTests`. The fix is a case-insensitive natural key
(`citext`, or a normalised-name column), which is a schema change and belongs in its own
phase alongside the audit-and-integrity baseline.

**7. `InternalsVisibleTo` was replaced by a `public partial class Program`.**

The plan called for `InternalsVisibleTo` so `WebApplicationFactory<Program>` could see
the internal generated `Program`. That grants the test project every internal in the
assembly. A three-line `public partial class Program { }` marker at the foot of
`Program.cs` exposes exactly one type instead, and is what the ASP.NET Core docs
prescribe.

**8. CI, docker-compose, and `/health`.**

CI was kept (one file, and the gap register says it pairs with tests).
`compose.yaml` and `/health` were **not** added: Testcontainers manages its own database
so compose is orthogonal to this phase, and `/health` belongs to Phase 3. Both remain in
the gap register.

## Out of scope

No production code behaviour changed. `Program.cs` gained only the `Program` marker;
`src/Jobkeep.slnx` gained the test project; nothing else under `src/` was touched. In
particular the A4 validation asymmetry, the case-sensitive skill dedup and the EF
version split were **recorded, not fixed** — each belongs to a phase that owns that area.

No schema change, so `docs/diagrams/*.svg` stay valid.

## Cost

Zero. Testcontainers runs locally against Docker, which is already installed for the
dev database, and GitHub Actions is free for public repositories. No always-on
infrastructure, nothing to tear down.

The only ongoing cost is time: a full run takes ~8s locally and ~50s in CI, most of
it container startup.

## Verify locally

```bash
cd src
dotnet build                                                   # builds app + tests
dotnet test --project ../tests/Jobkeep.Tests/Jobkeep.Tests.csproj
```

Docker must be running; the tests start their own `postgres:16-alpine` container and do
not touch the dev database. There is a guard in `PostgresFixture` that refuses to run if
they ever would.

## Interview talking points

- **Why a real database and not a fake.** Every test in `MappingTests` would pass
  vacuously or throw on EF's InMemory provider — it has no column types, so `text[]` and
  `numeric(12,2)` are just CLR objects, and it ignores unique indexes entirely. The
  find-or-create dedup that the whole Postgres-over-DynamoDB decision rests on is exactly
  the thing a fake repository would have reported as working while the SQL was wrong.
- **The near-miss.** See deviation 1 and 2. A test suite that silently points at your
  development database is worse than no test suite, and the guard that was supposed to
  catch it was fooled by the same bug.
- **Tests as documentation of known defects.** The `A4_` tests assert wrong behaviour on
  purpose, and say so. Being able to explain why that is better than skipping them is the
  point.
- **What the tests deliberately do not assert.** `GetAllAsync` orders by `DateApplied`,
  a `DateOnly` — same-day rows tie and their relative order is undefined, so no test
  asserts list order. Knowing which assertion would have been flaky is worth as much as
  the ones that are there.

## Next

Phase 2.3 — filter, sort, and page the applications list. It owns the read path, so
it is where A1 (GraphQL over-fetch) and the rest of A2 (entities as the API contract)
get fixed — and it is the first phase that gets to write its tests alongside the
feature rather than afterwards.
