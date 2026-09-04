# Jobkeep

A personal job-application tracker with AI-powered job description
analysis and CV-to-ad match checking. Built as a portfolio project:
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

**The whole stack, one command** — Postgres, the API and the front end, with
nothing installed but Docker:

```bash
docker compose up --build   # down to stop; down -v to drop the database too
docker compose logs -f api  # follow one service
```

The API answers on `http://localhost:5080` (Swagger at `/swagger`, the GraphQL
IDE at `/graphql`) and the front end on `http://localhost:5173`. The front end is
the real Vite dev server with `./web` bind-mounted, so hot reload works; the API
is a published build, so **a C# edit costs `docker compose up --build api`**.

There used to be a second launcher — a `run.cmd` that started the same three
layers as native Windows processes, which was a faster inner loop for C#. It was
removed on 2026-09-01 so there is one way to start the app rather than two that
bind the same three ports. One thing went with it: it used to kill the stray
`Jobkeep.exe` that makes the next build fail with MSB3027, so if you run
`dotnet run` by hand, stop it yourself before rebuilding.

**Ollama is not in the compose stack, on purpose** — it is a multi-GB model
server with no business being rebuilt and restarted with the app, so the API
container reaches it on the host at `host.docker.internal:11434`. Everything
works without it except the three features that call a model; see
[Where the model runs](#where-the-model-runs).

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

## Where the model runs

**Not in Docker.** The compose stack is three containers — Postgres, the API, Vite
— and Ollama is not one of them. It is a multi-GB model server that has no
business being rebuilt or restarted with the app, so it runs natively on the host
and the API container reaches it through `host.docker.internal:11434`
(`compose.yaml`, `Ai__Endpoint`). On a bare `docker compose up` with no Ollama
installed, every model call fails with a connection refused that reads like a
model problem and isn't one.

```bash
ollama serve                 # on the host, not in a container
ollama pull llama3.2:3b      # the model the app asks for by name
```

One model does all of it: **`llama3.2:3b`**, configured in `appsettings.json`
under `Ai` and bound to `ModelOptions` in `src/Shared/ModelClient.cs`. It is
registered once as an `IChatClient` (OllamaSharp implements the interface
directly) and injected wherever a module wants it — `Ai` does not own the
technology, it owns the `ai_analyses` table.

Three callers, and **they are not equally dependent on it**:

| Caller | What it asks the model for | What happens with Ollama down |
|---|---|---|
| `Modules/Ai/AnalyzePosting.cs` — "Analyse the ad" | Seniority, a 2–3 sentence summary, and **every technology named in the ad**, from `job_postings.Description` | 500, with a message naming the endpoint. Nothing is stored. |
| `Modules/Documents/DocumentStructurer.cs` — the upload pipeline | Structure for an uploaded PDF/DOCX/text: a résumé's roles and education, or a job ad's title, company, skills and requirements | The import blocks for up to 180s and then fails. Text extraction itself never touches the model. |
| `Modules.Match/RunMatchCheck.cs` — the match check | **Only free-text requirement coverage.** | **Degrades, does not fail** — three of the four stages need no model. The warning is stored, so a later read cannot mistake an empty `UnmetRequirements` for "every requirement met". |

The match row is the one worth reading twice. **The skill gap is a SQL set
difference, not a model call** — a posting's skills and a résumé's skills are rows
in the same `skills` table joined on the same `SkillId`, so "what does this ad ask
for that this CV does not have" is a join. Exact, instant, free, and it cannot
hallucinate. The plan for Phase 5 said to prompt the model for it; the
implementation refused, and that is the decision in the phase doc.

Timeouts are generous on purpose: `TimeoutSeconds = 180`, because a 3B model on
CPU is slow and the first request after boot also pays for loading the weights.
The `Ai` config section is committed rather than kept in a secret store, which is
only acceptable because none of it is a credential — Ollama is local and has no
API key. A hosted provider's key would come from an environment variable, the way
the connection string does.

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
└── src/                   # The actual .NET solution — nine projects since Phase 13.1
    ├── Jobkeep.slnx                    # names all nine + the test project
    ├── Directory.Build.props           # the TFM, shared by all of them
    ├── Jobkeep.SharedKernel/           # SliceResult, NaturalKey, IAuditable, ModelOptions
    ├── Jobkeep.Contracts/              # how one module talks to another: interfaces + DTOs
    ├── Jobkeep.Infrastructure.Data/    # TEMPORARY — the pre-split entities, DbContext
    │                                   #   and migrations; deleted in Phase 13.3
    ├── Jobkeep.Modules.Applications/   # a module = Domain/ + Application/ + Infrastructure/
    ├── Jobkeep.Modules.Analytics/      #   read-only reporting
    ├── Jobkeep.Modules.Ai/             #   owns ai_analyses
    ├── Jobkeep.Modules.Match/          #   owns match_results
    ├── Jobkeep.Modules.Documents/      #   owns document_imports + the résumé tables
    └── Jobkeep.Api/                    # the only project that knows about HTTP
        ├── Program.cs                  #   wiring only: DI, middleware, Map* calls
        ├── Endpoints/                  #   REST routes (controllers replace these at 13.5)
        ├── GraphQL/                    #   HotChocolate Query + Mutation
        ├── appsettings.json            #   empty Postgres conn (set in deploy)
        └── appsettings.Development.json
```

**A module never references another module** — it goes through `Jobkeep.Contracts`.
That rule is what makes extracting one into its own service a directory move rather
than a redesign, and `tests/Jobkeep.Tests/Architecture/` fails the build if it slips.
See `docs/phases/phase-13-clean-architecture.md`.

## STAR log

Keep a running log (separate from this repo — a spreadsheet or notes
app is fine) of specific decisions and moments from building this, with
a number attached wherever possible. This is raw material for behavioral
interviews later — capture it close to when it happens, not months
after. See phase docs for "interview talking points" sections as
starting prompts for entries.
