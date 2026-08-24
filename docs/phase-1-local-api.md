# Phase 1 — Local API, zero AWS, zero cost

**Status: Done**

## Goal

A working ASP.NET Core Web API on your own machine with in-memory storage.
No AWS, no account, no cost. Prove the core CRUD shape works before adding
any cloud complexity.

## What was built

- `Models/JobApplication.cs` — the data shape: company, role, status,
  dates, notes, job description, and a slot for AI-extracted skills
  (used in Phase 4).
- `Repositories/IJobApplicationRepository.cs` — a storage interface.
  This is the key design decision of this phase: endpoints talk to the
  interface, never to a concrete storage type. That's what makes Phase 2
  a one-line swap instead of a rewrite.
- `Repositories/InMemoryJobApplicationRepository.cs` — Phase 1's storage,
  a `ConcurrentDictionary` in memory. Data resets on restart — expected.
- `Program.cs` — minimal API endpoints: `GET/POST/PATCH/DELETE /applications`.

## Run it

```bash
cd src
dotnet restore
dotnet run
```

Listens on `http://localhost:5080`. See root `README.md` for `curl` examples.

## Interview talking points from this phase

- Why the repository sits behind an interface (dependency injection,
  testability, swappable storage) — this is a real system design habit,
  not just AWS trivia.
- Why in-memory first: fastest possible feedback loop while the API shape
  is still being figured out, before adding cloud latency and cost to
  the mix.

## Next

Phase 2 — swap storage to PostgreSQL via EF Core, developed locally first
for free (Postgres in Docker). *This originally read "swap storage to
DynamoDB"; the relational model won out — see `phase-2-postgres.md`.*
