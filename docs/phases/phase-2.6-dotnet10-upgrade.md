# Phase 2.6 — Upgrade to .NET 10 (LTS)

**Status: Done** (2026-08-26)

## Goal

Move the project from `net8.0` to `net10.0` **before** the AWS deploy, so
Phase 3 lands on a runtime that is actually supported.

## Why now

.NET 8 reaches **end of support on 10 November 2026** — no more servicing
updates, security fixes, or technical support. .NET 9 hits the same date.
.NET 10 is the current LTS, supported through **November 2028**.

Doing this before Phase 3 rather than after is the whole point:

- Deploying to Lambda on .NET 8 means migrating a *live* function later —
  a bigger, riskier job than changing a line in a `.csproj` today.
- AWS Lambda managed runtimes follow .NET's support lifecycle. An
  unsupported runtime eventually stops being an option.
- It's cheap right now. The project is small, has no dependents, and
  nothing is deployed yet.

The cost priority is unaffected — this is a compile-target change, $0.

## Plan

1. Install the .NET 10 SDK; confirm with `dotnet --list-sdks`.
2. Change `<TargetFramework>net8.0</TargetFramework>` to `net10.0` in
   `src/Jobkeep.csproj`.
3. Bump the packages to their .NET 10 lines:
   - `Microsoft.EntityFrameworkCore.Design` 8.0.11 → 10.x
   - `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.10 → 10.x
   - `HotChocolate.AspNetCore` 14.3.0 → latest stable
   - `Swashbuckle.AspNetCore` 10.2.3 → latest stable
4. `dotnet restore && dotnet build`, and read the warnings rather than
   suppressing them — nullability analysis gets stricter across major
   versions, and the project has nullable enabled.
5. Run the app against local Postgres and verify **both** surfaces:
   REST `GET /applications`, and a GraphQL query through the Nitro IDE.
6. Confirm existing EF migrations still apply cleanly to a **fresh**
   database (drop the container's volume and let Development auto-migrate).

## Watch for

- **Npgsql major versions track EF Core major versions.** Mismatched
  majors are the most likely source of restore failures here.
- **Swashbuckle vs. built-in OpenAPI.** .NET 9+ ships
  `Microsoft.AspNetCore.OpenApi` with `AddOpenApi()`/`MapOpenApi()`.
  Swapping is optional — worth a look, but out of scope unless
  Swashbuckle actually causes trouble. Don't let this phase sprawl.
- **EF Core 10 behaviour changes.** Check the breaking-changes page before
  assuming a query behaves identically. *(This bullet originally named
  `PostgresJobApplicationRepository.WithGraph()`; Phase 2.3 deleted the
  repository. The equivalent thing to re-verify is the list projection in
  `Modules/Applications/ListApplications.cs` and the detail projection in
  `ApplicationDetail.cs`.)*

## Rollback

Git revert. The change is confined to `src/Jobkeep.csproj` unless a build
error forces a code fix — no schema, migration, or data change, so there is
nothing to undo on the database side.

## Out of scope

Any restructuring toward the module/slice layout in `docs/architecture.md`.
This phase changes the target framework and nothing else, so that if
something breaks, the cause is unambiguous.

## Verify

- `dotnet build` clean (or with only warnings you've read and accepted).
- `dotnet run`, then `GET /applications` returns existing data.
- A GraphQL query returns the same nested shape as before.
- A fresh database migrates from empty without error.

## Interview talking points

- Reading a framework support lifecycle and acting on it *before* the
  deadline, not after — sequencing the upgrade ahead of the deploy so the
  migration was a one-line change rather than a live-service operation.
- Knowing the difference between LTS and STS releases, and why a project
  you intend to leave running should sit on LTS.


---

## What actually happened

**No C# changed.** Not one file under `src/*.cs` or `tests/*.cs` was touched.
The whole upgrade is four project/config files, and the build came out at
**0 warnings, 0 errors** — including the stricter nullability analysis the plan
warned about.

| File | Change |
|---|---|
| `src/Jobkeep.csproj` | `net8.0` → `net10.0`; EF Design 8.0.11 → 10.0.11; Npgsql 8.0.10 → 10.0.3; HotChocolate 14.3.0 → **14.3.1** |
| `tests/Jobkeep.Tests/Jobkeep.Tests.csproj` | `net8.0` → `net10.0`; Mvc.Testing 8.0.30 → 10.0.11; EF + EF.Relational 8.0.11 → 10.0.11 |
| `src/dotnet-tools.json` | `dotnet-ef` 8.0.11 → 10.0.11 |
| `.github/workflows/ci.yml` | two SDKs → one (`10.0.x`) |

### Deviations from the plan

**1. HotChocolate was a security fix, not a version bump.** The plan listed it as
"14.3.0 → latest stable", i.e. discretionary. The `net10.0` restore emitted
**NU1904: HotChocolate.Language 14.3.0 has a known critical severity
vulnerability** — [GHSA-qr3m-xw4c-jqw3](https://github.com/advisories/GHSA-qr3m-xw4c-jqw3),
a stack-overflow DoS in `Utf8GraphQLParser`. A ~40 KB GraphQL document with
deeply nested selection sets kills the worker process, and it is **uncatchable
and unpreventable from inside the app**: `StackOverflowException` cannot be
caught in .NET, and the parser runs *before* validation, so `MaxExecutionDepth`,
complexity analysers and persisted-query allow-lists cannot intercept it.

That matters here specifically: `/graphql` is unauthenticated and Phase 3 puts it
on the public internet. The fix is **14.3.1**, the patch release for the 14 line
— one patch version, no API surface change.

The **14 → 16** jump the plan's wording implied was *refused*: two majors of
breaking changes, for no benefit this phase needs, in a phase whose whole point
is that a failure has an unambiguous cause. Recorded in `backlog.md` instead.

**2. Swashbuckle needed no change.** 10.2.3 was already the latest stable. The
plan assumed it was behind; it wasn't. `Microsoft.AspNetCore.OpenApi` stays out
of scope as planned.

**3. `dotnet-ef` had to move, and the plan didn't mention it.** The tool is
pinned in `src/dotnet-tools.json` with `rollForward: false`, so the 8.0.11 tool
stays on the .NET 8 runtime and cannot load an EF **10** design-time assembly.
Bumped to 10.0.11 and re-restored.

**4. The provider/design version split is only *half* closed — the "Known gaps"
entry overstated what this phase would do.** The *major-line* split is gone: the
app now resolves EF 10.0.11 throughout. But the asymmetry that made the test
project's explicit EF pins necessary **survives the upgrade**, because it was
never about the major version:

- `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 declares a **range**:
  `Microsoft.EntityFrameworkCore [10.0.4, 11.0.0)`.
- `Microsoft.EntityFrameworkCore.Design` 10.0.11 pins an **exact** 10.0.11.

So `src` unifies at 10.0.11, while a transitive-only reference in the test
project resolves to the *floor*, 10.0.4. Removing the two pins was tried, and
fails: **CS1705** — the test assembly would reference EF 10.0.4 while
`Jobkeep.dll` is compiled against 10.0.11. The pins stay, with the comment in
`Jobkeep.Tests.csproj` rewritten to give this reason rather than the old
8.0.10/8.0.11 one. They must be bumped in lockstep with EF Design in `src`.

**5. Migrations were left alone, deliberately.** `AppDbContextModelSnapshot.cs`
and the initial migration's designer file still carry
`HasAnnotation("ProductVersion", "8.0.11")`. That string is metadata, not
behaviour, and rewriting it means regenerating migrations for cosmetics. EF 10
reads the EF 8 snapshot fine — `dotnet ef migrations has-pending-model-changes`
reports **"No changes have been made to the model since the last migration"**,
which is the real evidence that the model maps identically across the major
version. The annotation updates itself the next time a migration is added.

### One warning, read and kept

The app logs `Microsoft.EntityFrameworkCore.Query[20504]`
(`MultipleCollectionIncludeWarning`) twice at startup — the list query projects
two collection navigations with no `QuerySplittingBehavior` configured.

This is **not** new in EF 10. Verified rather than assumed: the pre-upgrade
commit was checked out into a throwaway git worktree and run on `net8.0`
against the same database, and it emits the identical warning the same number
of times. It is the known A1 include-graph shape (`architecture.md` A1 and
decision 11), which is deliberately parked. Out of scope for a framework bump.

### Verification

All of the plan's "Verify" list, plus the two surfaces' behaviour:

| Check | Result |
|---|---|
| `dotnet build src/Jobkeep.slnx` | **0 warnings, 0 errors** |
| Full test suite | **132 passed, 0 failed** — same count as on `net8.0` |
| Fresh database migrates from empty | Yes — new container, Development auto-migrate |
| `dotnet ef migrations script` / `has-pending-model-changes` | Both clean under EF 10 |
| REST `POST /applications` | 201, DTO shape unchanged |
| REST `GET /applications` | 200, SQL translates (verified in the log) |
| REST `/stats/skill-demand`, `/stats/funnel`, `/stats/companies` | 200, real `GROUP BY` |
| GraphQL `applications` + all three analytics queries | Same shapes as before |
| Enum serialization | Still `"Interviewing"` over REST, `INTERVIEWING` over GraphQL |
| Phase 2.5 status rule | `Applied → Interviewing` 200; `Interviewing → Applied` 400 |

### Interview note the plan didn't anticipate

The upgrade's real payoff wasn't the framework — it was that **moving off a
stale dependency graph surfaced a critical CVE in a transitive package that
nobody would have gone looking for.** The vulnerable code path was reachable,
unauthenticated, and about to be deployed. Staying on a supported runtime is
partly a story about *support windows*, and partly about the fact that upgrading
regularly is how you find out you were exposed.

## Next

Phase 3 — deploy to AWS Lambda + API Gateway.
