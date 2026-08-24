# Architecture — standing record

**Last reviewed: 2026-08-25.**

This is the authority on *how JobKeep is built and why*. Phase docs
(`phase-N-*.md`) own **what** gets built and when; this doc owns the shape the
code takes and the decisions behind it. Where a phase doc and this doc disagree,
this doc wins — and the phase doc should be corrected.

---

## 1. As-built today (end of Phase 2)

One ASP.NET Core 8 project, one deployable, one database.

```
HTTP --+-- REST  (Endpoints/ApplicationEndpoints.cs, MapGroup "/applications")
       +-- /graphql (HotChocolate Query + Mutation)
                     |
                     +--> IJobApplicationRepository
                              +-- PostgresJobApplicationRepository  (EF Core, real)
                              +-- InMemoryJobApplicationRepository  (no-DB fallback)
                                       |
                                       +--> AppDbContext --> PostgreSQL
```

Domain model (8 tables, normalized): `companies`, `job_postings`, `skills`,
`posting_skills` (join, composite key), `job_requirements`, `job_applications`,
`ai_analyses`, `ats_results`. Mapping is Fluent API in one place
(`src/Data/AppDbContext.cs`), enums stored as strings, delete behaviour chosen
per relationship rather than left to convention.

**What is genuinely good here** and worth keeping:
- One storage abstraction serving *both* API surfaces — REST and GraphQL cannot
  drift apart, because there is no second code path to drift into.
- The shared-`skills` table with find-or-create dedup. This is the whole reason
  Postgres was chosen over DynamoDB, and it makes "top skills across all my
  tracked jobs" a single `GROUP BY`.
- `AsSplitQuery()` on the include graph, avoiding a cartesian blow-up.
- Config-not-code environment switching (connection string via config/env var).
- Scoped-not-singleton DI, with the captive-dependency reasoning written down.

### Known problems (recorded 2026-08-25, not yet fixed)

| # | Problem | Where |
|---|---|---|
| A1 | **GraphQL over-fetches.** Resolvers call `GetAllAsync()`, which eager-loads company + skills + requirements + AI analysis + ATS result *regardless of the fields requested*. This negates GraphQL's main advantage. Fix: HotChocolate projections/DataLoader over `IQueryable`. | `GraphQL/Query.cs:13`, `Repositories/PostgresJobApplicationRepository.cs:22` |
| A2 | **EF entities are the API contract.** Endpoints return entities directly. `ReferenceHandler.IgnoreCycles` is a band-aid for the resulting navigation-property cycles, not a serialization preference. Response DTOs remove both the cycles and the coupling. | `Program.cs:26`, `Endpoints/ApplicationEndpoints.cs:31` |
| A3 | **The repository interface is the wrong abstraction.** `AddSkillToPostingAsync` is a *use case* sitting on a CRUD interface. Phases 2.1-2.4 would add roughly 15 more methods to the same interface. | `Repositories/IJobApplicationRepository.cs:19` |
| A4 | **Validation is ad hoc and surface-specific.** Hand-rolled null checks on the REST create path; the GraphQL mutation path has none. One rule, two surfaces, one implementation needed. | `Endpoints/ApplicationEndpoints.cs:45`, `GraphQL/Mutation.cs:9` |
| A5 | **Stale comment** claiming Phase 2 swaps in a DynamoDB implementation. It did not. | `Repositories/IJobApplicationRepository.cs:5-7` |
| A6 | **No tests, no CI, no compose file, no health check.** | repo-wide |

---

## 2. Target: modular monolith with vertical slices

The direction from Phase 2.1 onward. **Adopted incrementally** — each phase
builds its new code in this shape rather than stopping to refactor everything.

```
src/
  Modules/
    Applications/          CreateApplication.cs, ListApplications.cs,
                           UpdateStatus.cs, AddSkillToPosting.cs ...
    Analytics/             SkillDemand.cs, StatusFunnel.cs
    Ai/                    (Phase 4)
    Ats/                   (Phase 5)
    Identity/              (later — see backlog)
  Shared/                  AppDbContext, cross-cutting contracts, common results
  Program.cs               wiring only (DI + middleware + Map* calls)
```

### The two rules

**Rule 1 — one slice per use case.** A slice file holds its request, its handler,
and its response together. Adding a feature means adding a file, not editing five
layers. Handlers take `AppDbContext` directly: EF's `DbContext` *is already* a
unit-of-work plus a repository, so wrapping it in a hand-written repository adds
a layer that mostly forwards calls.

**Rule 2 — modules do not reach into each other.** They share one database, but a
module only queries the tables it owns. Cross-module reads go through a public
contract exposed by the owning module. This is the rule that makes later
extraction a code-move instead of a redesign — and the rule that quietly rots if
nobody enforces it.

### Ownership

| Module | Owns | Notes |
|---|---|---|
| Applications | `job_applications`, `job_postings`, `companies`, `job_requirements` | The core aggregate. |
| Analytics | reads `skills` + `posting_skills` | Read-only; aggregates in SQL, never in C#. |
| Ai | `ai_analyses` | Phase 4. Sits behind `IChatClient`. |
| Ats | `ats_results` | Phase 5. |
| Identity | users | Not yet built; touches every module's queries when it lands. |

### What this does *not* mean

Not Clean Architecture's four projects. A `Domain`/`Application`/`Infrastructure`/
`Presentation` split for an 8-table single-user tracker costs more in ceremony than
it returns. Use the dependency rule where domain logic actually earns it — the
status lifecycle in Phase 2.4 is the first real candidate — and keep simple slices
simple.

---

## 3. Why not microservices yet

The long-term goal is microservices. **This is deliberately not that, yet.**

Today: 8 tables, one user, one deployable, a near-zero-cost budget. Splitting that
across several Lambdas with separate databases and an event bus would buy nothing
and cost real money — every service carries its own cost floor, which fights
priority #1 directly. It would also trade in-process method calls for network
calls that can fail, and a database transaction for a distributed one.

An interviewer can tell the difference between distributed-by-need and
distributed-for-the-portfolio. The stronger answer is the honest one:

> "I drew the module boundaries up front and deliberately kept one deployable.
> Here is the trigger that would make me extract a service — and here is the cost
> I would be taking on when I did."

### Extraction triggers

Extract a module into its own service when **at least one** is true:
- **Independent scaling** — one module's load profile genuinely diverges (the AI
  module is the realistic candidate: slow, bursty, and expensive per call).
- **Independent deploy cadence** — one module needs to ship without redeploying
  the rest.
- **A second consumer** — something other than this app needs the module directly.
- **Team split** — more than one person, and merge friction is real.

None currently hold. When one does, the module boundary is already drawn, and the
cost of being wrong is a code-move rather than a rewrite.

### The realistic first extraction

The **Ai** module (Phase 4). Long-running, bursty, and the one place where
independent scaling is a genuine argument rather than a hypothetical — which also
makes it the best interview example, because the reasoning is concrete.

---

## 4. Decision record

Numbered, dated, with status, so reversals stay legible.

| # | Decision | Date | Status |
|---|---|---|---|
| 1 | **PostgreSQL over DynamoDB.** A normalized relational model makes skill-demand analytics one `GROUP BY`; the same question is awkward in a denormalized document model. Cost: RDS is not serverless — free-tier for 12 months, then always-on and billable. Accepted knowingly. | Phase 2 | Accepted |
| 2 | **REST and GraphQL coexist** over one repository. GraphQL did not replace REST; both were kept so the project demonstrates each. Note this is a *portfolio* choice — no comparable product ships a public API. | Phase 2b | Accepted |
| 3 | **Serverless deploy (Lambda + API Gateway).** Both surfaces ride one Lambda. Pay-per-use, permanent compute free tier. | Phase 3 | Planned |
| 4 | **AI behind `Microsoft.Extensions.AI`'s `IChatClient`,** so Ollama (local, free) and a hosted API swap via config. | Phase 4 | Planned |
| 5 | **Vertical slices replace `IJobApplicationRepository`.** Supersedes the former CLAUDE.md rule "never bypass this interface". The interface was already carrying a use-case method, and four planned sub-phases would have pushed it past roughly 20 methods. `InMemoryJobApplicationRepository` retires with it — the no-DB dev mode is better served by Postgres in Docker, which is what the README already tells you to run. | 2026-08-25 | Accepted |
| 6 | **Modular monolith over microservices,** with the extraction triggers in section 3. | 2026-08-25 | Accepted |
| 7 | **MVC controllers — proposed for retirement.** `backlog.md` committed to adopting attribute-routed controllers as "the convention most teams use". Attribute-routed controllers organise code by *technical layer*, which cuts across vertical slices. Minimal APIs grouped per slice are equally mainstream in .NET 8+. Recommend dropping the adoption; confirm rather than silently discard. | 2026-08-25 | **Proposed** |
| 8 | **Upgrade to `net10.0`.** `net8.0` reaches end of support **10 Nov 2026**; .NET 10 is LTS through Nov 2028. Slotted as Phase 2.5, before the AWS deploy, so Phase 3 lands on a supported runtime. | 2026-08-25 | Planned (Phase 2.5) |

---

## 5. Gap register

Measured against what Melbourne .NET job ads actually ask for (see section 6).
Ordered by portfolio value per unit of effort.

| Gap | Why it matters | Home |
|---|---|---|
| **Automated tests** | The single largest gap. Named in essentially every ad. xUnit + Testcontainers (real Postgres in Docker) tests the EF mapping and the find-or-create dedup logic that unit tests with a fake would miss. | Own phase — **strongest candidate to schedule next** |
| **CI/CD** | Named in every ad. GitHub Actions: build + test on push. Cheap (free for public repos) and immediately visible on the repo page. | Own phase, pairs with tests |
| **Response DTOs** (A2) | Decouples the API contract from the EF schema and removes the `IgnoreCycles` band-aid. | Fold into Phase 2.1/2.2 |
| **GraphQL projections** (A1) | Makes GraphQL actually do the thing GraphQL is for. Good interview material: "I measured what my resolver loaded, and it was the whole graph." | Fold into Phase 2.2 |
| **docker-compose** | Replaces the manual `docker run` in the README. One file, and Docker is on every ad. | Trivial — any phase |
| **Health check endpoint** | `/health` hitting the DB. Needed before Phase 3 anyway (Lambda + RDS). | Phase 3 |
| **Auth / multi-user** | Architectural — every query becomes user-scoped. Already correctly deferred in the backlog. | Phase 3+ |
| **Structured logging / observability** | Serilog + correlation IDs. Meaningful once deployed and something can actually go wrong unobserved. | Phase 3+ |

---

## 6. Market context

Verified 2026-08-25. Recorded here so future sessions do not re-derive it, and so
nothing overconfident gets repeated in an interview.

### The comparable products

- **Huntr** — *tracker-first*, resume tools attached. Kanban board across stages,
  contact/recruiter CRM, Chrome extension autofill for Workday/Greenhouse, map
  view. This is the closest comparable to JobKeep.
- **Teal** — *resume-first*, tracker attached. Its keyword feature is the **Job
  Matcher**: link one resume to one saved job, get a Match Score plus
  matched/missing/suggested keywords, updating live as you edit.
- Both land around **$36-40/month** paid.

**The correction that matters:** Teal's keyword matching is
resume-vs-**one**-job. That is JobKeep's **Phase 5 (ATS check)**, *not* Phase 2.3
analytics. "Top in-demand skills across **all** your tracked postings" is a
different question, and neither comparable answers it — it remains a genuine
differentiator. (An earlier draft of `backlog.md` flagged this attribution as
overconfident and asked for verification before repeating it. Verified; the
caution was right.)

Also true and worth saying plainly: **neither product exposes a public API or
GraphQL.** JobKeep's dual surface is a portfolio decision, not an industry norm.
Claiming otherwise in an interview would be easy to puncture.

### What the engineering market asks for

Recurring in Melbourne .NET backend ads: RESTful API design, EF Core, PostgreSQL
schema design, AWS, Docker, CI/CD, authentication, and event-driven microservices
/ distributed systems.

JobKeep has **strong** evidence for EF Core, PostgreSQL schema design, and REST
API design — the relational model and its delete-behaviour reasoning are real,
demonstrable work. It has **no** evidence yet for tests, CI/CD, Docker, or auth.
That imbalance, not the layering, is the biggest portfolio gap. See section 5.

---

## Sources

- [Huntr vs Teal](https://huntr.co/blog/huntr-vs-teal) ·
  [Best job application trackers 2026](https://offboard.co/resources/best-job-application-trackers-2026)
- [Teal Job Matcher](https://help.tealhq.com/en/articles/12060992-using-the-job-matcher) ·
  [Teal resume/JD match](https://www.tealhq.com/tool/resume-job-description-match)
- [Vertical Slice Architecture in .NET](https://milanjovanovic.tech/blog/vertical-slice-architecture-dotnet) ·
  [Modular monolith with vertical slices](https://antondevtips.com/blog/building-a-modular-monolith-with-vertical-slice-architecture-in-dotnet) ·
  [ardalis/VerticalCleanModularMicroservices](https://github.com/ardalis/VerticalCleanModularMicroservices)
- [.NET 8 and .NET 9 end of support](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
- [Backend developer jobs, Melbourne](https://www.glassdoor.com.au/Job/melbourne-backend-developer-jobs-SRCH_IL.0,9_IM965_KO10,27.htm)
