# Jobkeep

A personal job-application tracker with AI-powered job description
analysis and ATS compatibility checking. Built as a portfolio project:
C# / ASP.NET Core backend (REST + GraphQL), PostgreSQL via EF Core,
deployed serverless on AWS, AI features via Ollama (local) or a hosted
API (deployed).

## Why this project

Built while job hunting in the Melbourne market — solves a real problem
(tracking applications, understanding fit against job descriptions)
while building demonstrable C# + AWS + AI integration experience.

## Status

| Phase | What | Status |
|---|---|---|
| 1 | Local API, in-memory storage | Done — see `src/` |
| 2 | Relational model on PostgreSQL + GraphQL | Done — local via Postgres in Docker |
| 2.1 | Complete the write surface (skills + requirements CRUD) | Done — first phase built as vertical slices |
| 2.2 | Automated tests + CI | Done — integration tests against real Postgres, plus GitHub Actions |
| 2.3 | Query, filter, sort & page the list | Done — and the repository layer retired with it |
| 2.4 | Analytics endpoints (skill demand, status funnel, company rollup) | Done — three `GROUP BY`s, on both surfaces |
| 2.5 | Enforce the application status lifecycle | Done — one rule, both surfaces, no schema change |
| 2.6 | Upgrade to .NET 10 (LTS) — .NET 8 EOL 10 Nov 2026 | Done — no C# changed; caught a critical CVE in a transitive package |
| 4 | AI job-description analyzer (local Ollama) | Done — behind `IChatClient`, local model only |
| 4.5 | Document import (PDF/DOCX/text) with a confirm step | Done — upload → parse → review → confirm → rows |
| 5 | ATS compatibility check | Done — deterministic skill gap + one model call; no score, on purpose |
| 6 | Front end (React + Vite, eight screens) | In progress — screens built and tested; visual pass + README remain |
| 7 | Data integrity, audit baseline & the case-insensitive dedup key | **Done** — one migration; F7/F8/F11/F12/F13/F14 closed, 239 tests |
| 8 | Soft delete / archive | Planned — rides Phase 7's index migration |
| 9 | The three reads the front end could not get | Planned — found by building the screens |
| 10 | Deploy to AWS Lambda (Function URL) + Neon Postgres | Parked (plan done, $0/month) |
| 11 | Authentication & owner scoping | Planned — tied to the deploy |
| 12 | Feature expansion | Placeholder — pulls from `docs/backlog.md` |

Phases 1–6 keep the numbers they shipped under; 7 onward were renumbered on
2026-09-01 into build order, ranked by which work gets *more expensive the longer
it waits*. Phase 10 was formerly "Phase 3" and Phase 12 formerly "Phase 7".

Full detail for each phase, including cost notes and interview talking
points, is in [`docs/`](docs/README.md) — start with the index there.

**How the code is shaped and why — plus the decision record, the gap
register, and the verified market comparison — is in
[`docs/architecture.md`](docs/architecture.md).** Read that before making
structural changes. In short: a modular monolith with vertical slices, one
deployable, module boundaries drawn now so services can be extracted later
if a real trigger appears.

![Architecture: HTTP arrives at two API surfaces, REST and GraphQL, which both
call vertical-slice handlers in the Applications module; the handlers use
AppDbContext directly over a single PostgreSQL database, and return response DTOs
that each surface renders its own way.](docs/diagrams/architecture.svg)

REST and GraphQL are two surfaces over **one** data layer, so a rule can't be
enforced on one and missed on the other. There used to be a second path — the
Phase 2 repository, drawn dashed here because it was retiring. Phase 2.3 deleted
it; every use case is now a slice under `Modules/`.

## Quick start

**The whole stack, one command** — Postgres, the API and the front end:

```powershell
.\run.cmd                 # -NoFrontend for backend only, -NoBrowser, -Stop to tear down
```

It brings each layer up only once the one below it answers, so a failure is
reported against the thing that failed rather than as a fetch error in the
browser. Ctrl+C stops what it started; the Postgres container is deliberately
left running, because it holds your data and costs nothing idle. If a launcher
is ever killed from outside, `.\run.cmd -Stop` cleans up after it — including
the stray `Jobkeep.exe` that otherwise makes the next build fail with MSB3027.

By hand, if you'd rather. Storage is **PostgreSQL via EF Core**; in Development
the app talks to a local Postgres container and auto-applies EF migrations on
startup, so start the container first:

```bash
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=jobkeep postgres:16-alpine

cd src
dotnet restore
dotnet run

cd web            # the front end (Phase 6)
npm run dev
```

Runs on `http://localhost:5080`. Data survives app restarts (it lives in
Postgres, not in memory). Two API surfaces share one backend:

### Tests

```bash
cd src
dotnet test --project ../tests/Jobkeep.Tests/Jobkeep.Tests.csproj
```

86 integration tests against a **real Postgres**, started automatically by
Testcontainers — so Docker needs to be running, but you do not need to start a
database yourself. They use a throwaway container and never touch your dev data;
there is a guard that refuses to run if they ever would. CI runs the same suite on
every push (`.github/workflows/ci.yml`).

Every route is also in [`src/Jobkeep.http`](src/Jobkeep.http), which runs in Visual
Studio, VS Code and Rider with no account.

**REST**
```bash
curl -X POST http://localhost:5080/applications \
  -H "Content-Type: application/json" \
  -d '{"company":"Canva","title":"Backend Engineer","notes":"Applied via referral"}'

# The list is filtered, sorted and paged (Phase 2.3). Everything is optional.
curl http://localhost:5080/applications
curl "http://localhost:5080/applications?status=Interviewing&sort=Company&direction=Asc"

# Company and title match anywhere, case-insensitively (ILIKE); skill matches the
# whole name, because C is a prefix of both C# and C++.
curl "http://localhost:5080/applications?company=canva&skill=C%23&page=1&pageSize=20"

# Sub-resources (Phase 2.1). Skills dedup into a shared `skills` table;
# removing one unlinks the join row and leaves that shared row alone.
curl -X POST http://localhost:5080/applications/{id}/skills   -H "Content-Type: application/json"   -d '{"skillName":"C#","category":"Language","isRequired":true}'

curl -X POST http://localhost:5080/applications/{id}/requirements   -H "Content-Type: application/json"   -d '{"text":"5+ years .NET","kind":"Qualification","isMustHave":true}'

# Skill names go in the path, so percent-encode them: C# is C%23
curl -X DELETE http://localhost:5080/applications/{id}/skills/C%23
```

**GraphQL** — open the Nitro IDE at `http://localhost:5080/graphql`, or:
```bash
curl -X POST http://localhost:5080/graphql -H "Content-Type: application/json" \
  -d '{"query":"{ applications(query: { skill: \"C#\" }) { totalCount items { company title skills } } }"}'
```

The same filter, the same validation and the same defaults as the REST call above —
both surfaces call one handler, so neither can offer a rule the other does not have.

Note: `status` (and other enums) now serialize by name — `"Interviewing"` over
REST, `INTERVIEWING` over GraphQL — not as an int.

### The schema those migrations build

![Entity relationship diagram of the thirteen-table JobKeep schema, with
job_postings at the centre. Solid edges are ON DELETE RESTRICT, dashed edges
are ON DELETE CASCADE.](docs/diagrams/schema-erd.svg)

Thirteen normalized tables with `job_postings` — the job ad — at the centre. Your
record of applying is a separate row, so one posting can carry several
applications. Delete behaviour is chosen per relationship: derived data
cascades, shared rows (a company, a skill) refuse to disappear underneath you.

Redraw both diagrams after a schema change with the `schema-diagram` skill; it
generates the DDL from EF rather than reading the model classes.

## Project structure

```
Jobkeep/
├── CLAUDE.md              # Context file for Claude Code
├── README.md              # This file
├── docs/                  # see docs/README.md for the index
│   ├── README.md           # what each doc is for, and which one wins
│   ├── architecture.md     # HOW the code is shaped + decision record
│   ├── security-and-data-audit.md  # schema/config exposure + remediation plan
│   ├── backlog.md          # considered-but-not-committed feature candidates
│   ├── token-log.md        # what each phase cost to build, in tokens
│   ├── phases/             # one doc per build phase, in order
│   │   ├── phase-1-local-api.md
│   │   ├── phase-2-postgres.md
│   │   ├── phase-2.1-write-surface.md
│   │   └── ...             # 2.2-2.5, 3, 4, 5, 6
│   └── diagrams/           # committed schema ERD + architecture SVGs
├── scripts/
│   └── token-usage.py     # totals Claude Code session tokens for docs/token-log.md
└── src/                   # The actual .NET project
    ├── Jobkeep.csproj
    ├── Program.cs                   # wiring only: DI, middleware, Map* calls
    ├── Modules/                     # vertical slices — one file per use case
    │   └── Applications/            #   ListApplications, CreateApplication, ...
    ├── Shared/                      # SliceResult + the two edge translations
    ├── appsettings.json             # empty Postgres conn (set in deploy)
    ├── appsettings.Development.json # points at local Postgres
    ├── Models/                      # relational domain model + enums
    ├── Data/                        # AppDbContext (EF Core mapping)
    ├── Migrations/                  # EF migrations
    ├── GraphQL/                     # HotChocolate Query + Mutation
    └── Properties/
```

## STAR log

Keep a running log (separate from this repo — a spreadsheet or notes
app is fine) of specific decisions and moments from building this, with
a number attached wherever possible. This is raw material for behavioral
interviews later — capture it close to when it happens, not months
after. See phase docs for "interview talking points" sections as
starting prompts for entries.
