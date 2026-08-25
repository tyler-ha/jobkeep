# Phase 2.6 — Upgrade to .NET 10 (LTS)

**Status: Not started**

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
  assuming a query behaves identically; the include graph in
  `PostgresJobApplicationRepository.WithGraph()` is the thing to re-verify.

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

## Next

Phase 3 — deploy to AWS Lambda + API Gateway.
