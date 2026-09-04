# Architecture — standing record

**Last reviewed: 2026-09-03 (Phase 13.6).**

This is the authority on *how JobKeep is built and why*. Phase docs
(`phase-N-*.md`) own **what** gets built and when; this doc owns the shape the
code takes and the decisions behind it. Where a phase doc and this doc disagree,
this doc wins — and the phase doc should be corrected.

---

## 1. As-built today (end of Phase 13)

**Ten projects on `net10.0`, one deployable, one database with five schemas.** Six
modules (`Applications`, `Analytics`, `Ai`, `Match`, `Documents`, `Skills`), plus
`SharedKernel`, `Contracts`, `Persistence` and `Api`. A module references
`SharedKernel`, `Contracts` and `Persistence` and **nothing else of ours** — never
another module, never `Api` — and `tests/Jobkeep.Tests/Architecture/` enforces it by
walking the assembly graph.

Since Phase 13.6 a namespace begins with the name of the project that holds it, so
a using block says which assembly a type comes from. Entities sit in
`<Module>/Domain/`, their EF configuration and `DbContext` in `<Module>/Persistence/`,
their migrations in `<Module>/Migrations/`, and the use cases in
`<Module>/Application/` — one file per slice, still.

![Architecture: HTTP arrives at two API surfaces, REST and GraphQL, which both
call vertical-slice handlers in the Applications module; the handlers use
AppDbContext directly over a single PostgreSQL database, and return response DTOs
that each surface renders its own way.](diagrams/architecture.svg)

**THE DIAGRAM ABOVE IS STALE AND DELIBERATELY NOT REDRAWN** — diagrams are frozen
until 1.0 ships on master (`CLAUDE.md`, "Frozen until 1.0"). What it still gets
right is the shape that matters: both surfaces reach the data through the *same*
slice handler, so REST and GraphQL cannot enforce different rules. What it gets
wrong is everything below that: there is no `AppDbContext`, there are six contexts;
and since 13.4 neither surface names a handler — a controller action or a resolver
sends a message and the mediator finds the handler.

There used to be two lanes here, and one was drawn dashed because it was retiring:
the Phase 2 routes that went through `IJobApplicationRepository`. **Phase 2.3
deleted it**, so there is one lane.

Domain model: **13 tables in five Postgres schemas**, one per table-owning module —
`applications` (`companies`, `job_postings`, `job_requirements`, `posting_skills`,
`job_applications`), `skills` (`skills`, `skill_aliases`), `documents`
(`resumes`, `resume_skills`, `resume_experiences`, `resume_educations`,
`document_imports`), `ai` (`ai_analyses`), `ats` (`match_results` — the schema keeps the old name, the table does not). Analytics owns no
table and reads three views that live in `applications`. Each schema has its own
`__EFMigrationsHistory`; mapping is Fluent API in `<Module>/Persistence/`, enums
stored as strings, delete behaviour chosen per relationship rather than left to
convention.

**Seven foreign keys, not thirteen.** Phase 13.3b dropped the six that crossed a
schema boundary; 13.3c replaced them in application code, and the asymmetry is the
rule worth carrying: **a cross-module CASCADE became a notification** (announced
after the publisher commits) and **a cross-module RESTRICT became a contract check**
(asked before). A cascade is a consequence; a restrict is a question. The delete-side
check is knowingly weaker than the key it replaced — two counts and a delete are not
a transaction — and the read paths tolerate the residue on purpose.

![Entity relationship diagram of the thirteen-table JobKeep schema, with
job_postings at the centre. Solid edges are ON DELETE RESTRICT, dashed edges
are ON DELETE CASCADE.](diagrams/schema-erd.svg)

**Both diagrams are frozen until 1.0 ships on master** (2026-09-02, at the user's
instruction). The per-change redraw trigger that used to sit here is suspended:
a migration or a module-boundary move is no longer a reason to regenerate them,
and the accumulated debt is recorded in the phase docs instead, one line each,
so the eventual redraw is a list rather than an investigation. When it happens,
use the `schema-diagram` skill (`.claude/skills/schema-diagram/`) and keep its one
method rule: derive the schema from **`pg_dump --schema-only`** against the migrated
database — not from `dotnet ef migrations script`, which is a sequence of migrations
whose later `ALTER`s silently correct its earlier `CREATE`s, and never from reading
the entity classes, because column types, precision, delete behaviour and index
uniqueness live in Fluent API config and the Npgsql provider.

**What is genuinely good here** and worth keeping:
- One storage abstraction serving *both* API surfaces — REST and GraphQL cannot
  drift apart, because there is no second code path to drift into.
- The shared-`skills` table with find-or-create dedup. This is the whole reason
  Postgres was chosen over DynamoDB, and it makes "top skills across all my
  tracked jobs" a single `GROUP BY`.
- Delete behaviour chosen per relationship rather than left to convention — and,
  since Phase 13.3c, the six that used to cross a module boundary re-stated as
  notifications and contract checks rather than quietly dropped.
- Config-not-code environment switching (connection string via config/env var).
- Scoped-not-singleton DI, with the captive-dependency reasoning written down.

### Known problems (recorded 2026-08-25, not yet fixed)

**The `Where` column predates Phase 13 and its paths have moved** — `Modules/X/` is
now `src/Jobkeep.Modules.X/Application/`, and `Data/AppDbContext.cs` is six contexts
under `<Module>/Persistence/`. Deliberately not swept: re-reading nine findings a
change never touched is the recurring cost decision 12 exists to stop, and these
rows are dated records of what was found when. Read the row, then find the file.

| # | Problem | Where |
|---|---|---|
| A1 | **GraphQL over-fetches — narrowed, not closed.** The eager-loading include graph is gone: every handler ends in a flat `.Select(...)` into a DTO, so a list request is two statements (a `count(*)` and one paged `SELECT` of named columns) where it used to be five behind `AsSplitQuery`, and nothing loads `Description`, the résumé, `AiAnalysis` or `MatchResult` unasked (the `ResumeText` column named here until Phase 4.5 no longer exists — an application carries a `ResumeId` and the text lives in `resumes`). **What remains is per-field granularity:** a GraphQL query selecting only `title` still loads every column in `ApplicationListItem`. The fix for that is `HotChocolate.Data`'s `[UseProjection]`, which only works over `IQueryable<JobApplication>` — putting EF entities back in the published schema and **reopening A7**. Deliberately not taken; revisit if a DTO ever gets expensive enough to matter. | `Modules/Applications/ListApplications.cs`, `ApplicationDetail.cs` |
| A2 | ~~**EF entities are the API contract.**~~ **Fixed in Phase 2.3.** Every REST route and GraphQL field returns a response DTO, so `ReferenceHandler.IgnoreCycles` came out of `Program.cs` — the flag was a band-aid for navigation-property cycles, and DTOs have none. The line was replaced with a comment saying what it was for, so that if it ever needs to come back, that is a signal something is returning an entity again. | `Program.cs`, `Modules/Applications/ApplicationDetail.cs` |
| A3 | ~~**The repository interface is the wrong abstraction.**~~ **Closed in Phase 2.3.** Phase 2.1 moved `AddSkillToPostingAsync` out and built three more use cases as slices, leaving the interface smaller than it started. Phase 2.3 deleted it, along with `PostgresJobApplicationRepository` and `Endpoints/ApplicationEndpoints.cs`. Worth keeping the arc visible: "never bypass this interface" (Phase 1-2) → "retiring, not growing" (2.1) → gone (2.3), each reversal recorded rather than quietly applied. | *(deleted)* |
| A4 | ~~**Validation is ad hoc and surface-specific.**~~ **Fixed in Phase 2.3.** Phase 2.1 put validation inside the slice handlers and gave both surfaces one outcome type (`Shared/Result.cs`); 2.3 moved the last two offenders in. `createApplication` over GraphQL now rejects the blank title REST always rejected, and `PATCH` — which applied every non-null field with no checks, so an empty string wrote through — validates what it is sent. The two `SurfaceParityTests` that asserted the *broken* behaviour failed the day it was fixed, which is what they were written to do. | `Modules/Applications/CreateApplication.cs`, `UpdateApplication.cs` |
| A5 | ~~**Stale comment** claiming Phase 2 swaps in a DynamoDB implementation.~~ **Fixed in Phase 2.1.** | `Repositories/IJobApplicationRepository.cs` |
| A6 | ~~No tests, no CI~~ **Addressed in Phase 2.2** — 55 integration tests (xUnit v3 + Testcontainers) and GitHub Actions build+test on push. Still missing: **no compose file, no health check.** | repo-wide |
| A7 | ~~**`[JsonIgnore]` does not defend GraphQL.**~~ **Fixed in Phase 2.3, as a side effect.** HotChocolate honours `[GraphQLIgnore]`, not `[JsonIgnore]`, so while resolvers returned `JobApplication` the back-references hidden from REST were *in the published schema* — a client could walk `application → posting → company → postings → applications → resumeText` and reach every résumé in the database. Once every root field returns a DTO, the entity types are not in the schema at all, so the walk is closed by construction rather than by annotating each new navigation property. Asserted against the emitted SDL, the same way the finding was made. | `GraphQL/Query.cs`, `SurfaceParityTests.NoEfEntityIsReachableFromTheGraphQLSchema` |
| A8 | **The audit columns are partial, and one already lies.** `job_postings` has no `UpdatedAtUtc` despite PATCH mutating it; four tables have none at all. `job_applications.UpdatedAtUtc` is hand-set in exactly one place (`UpdateAsync`), and every other write path saves without touching it. **Phase 2.1 made this worse, as predicted:** the four new slices each `SaveChangesAsync` and none of them maintains a timestamp — the original evidence for this finding, `AddSkillToPostingAsync`, is gone, but it was replaced by four write paths with the same gap. That is the argument for a `SaveChangesInterceptor` rather than more hand-maintenance, restated by a phase that wasn't trying to make the point. | `Modules/Applications/UpdateApplication.cs`, `Modules/Applications/*.cs` |
| A9 | **The schema enforces nothing the application does not.** No DB-side default (`gen_random_uuid()`, `now()`), no CHECK constraint (`SalaryMin <= SalaryMax` is unenforced), no concurrency token, and eleven unbounded `text` columns on an unauthenticated write surface. | `Data/AppDbContext.cs`, `Migrations/…InitialCreate.cs` |

Security, PII, secrets and retention are audited in full — severity, evidence and a
phased remediation plan — in **[`security-and-data-audit.md`](security-and-data-audit.md)**.
A7-A9 are the subset that belongs in *this* table because they are structural rather
than operational.

---

## 2. Module-owned Clean Architecture, with vertical slices inside

**Rewritten at Phase 13.6.** From Phase 2.1 to Phase 12 this section described a
*modular monolith with vertical slices*: modules were folders under one project,
over one `AppDbContext`, over one schema. Phase 13 made each module a **project**
with its own entities, its own `DbContext`, its own Postgres schema and its own
migrations. The slice did not change; everything around it did.

The reason for the change is one sentence from the user, on 2026-09-01: *"long term
goal is the microservice."* Against that question — **can this module be lifted
out?** — folders buy nothing, because nothing stops a `using` and a join. Projects
do, because the compiler refuses.

```
src/
  Jobkeep.Api/                     the composition root: Program.cs, Controllers/,
                                   GraphQL/, and nothing that knows a table
  Jobkeep.Contracts/               how modules talk to each other, and only that
  Jobkeep.SharedKernel/            SliceResult, NaturalKey, IAuditable, ModelOptions
  Jobkeep.Persistence/             the two model-wide EF rules + the audit interceptor
  Jobkeep.Modules.<X>/
    Domain/                        entities, enums, pure rules. No EF, no handlers.
    Application/                   one file per use case: request + handler + response
    Infrastructure/                this module's implementation of its own contract
    Persistence/                   <X>DbContext + the Fluent API configuration
    Migrations/                    this module's schema, its own history table
    <X>Module.cs                   DI registration
```

`Program.cs` calls `Add*Module()` and nothing else module-shaped. Two files in
`Applications/Application/` are shared by several slices, and the distinction is the
one the repository failed: `ApplicationDetail.cs` holds a response shape and a
projection, `CompanyLookup.cs` holds one find-or-create. **Neither owns *access*** —
every slice still writes its own query, and a slice needing something different
writes it rather than growing these. A repository owns the queries themselves, which
is why every use case had to become one of its methods, and why it kept growing.

### The rules

**Rule 1 — one slice per use case, and it is unchanged.** A slice file holds its
request, its handler and its response together; adding a feature is adding a file,
not editing five layers. A handler takes **its own module's** `DbContext` directly:
EF's `DbContext` is already a unit-of-work plus a repository, so wrapping it adds a
layer that mostly forwards calls. Note what this deliberately is *not* — Clean
Architecture would forbid `Application` referencing `Persistence`. Here it is
allowed, because the alternative is the repository this project already deleted
once. The dependency rule is applied where it pays (`Domain/`) and refused where it
does not.

**Rule 2 — every crossing between modules goes through `Jobkeep.Contracts`, reads
included.** This **reverses decision 17**, which said a module may read another
module's tables freely and only a write needs a contract. Decision 17 answered *"is
this safe?"* and answered it correctly; Phase 13 asks *"can this be lifted out?"*,
and against that one a cross-boundary `SELECT` is precisely what stops working when
the boundary becomes a network. Since 13.3b it is not a rule but a fact: another
module's tables are not in your context's model.

**Rule 3 — `Domain/` knows nothing of EF or of the rest of its module.** The one
place the Clean Architecture dependency rule is enforced rather than discussed,
because entities are the layer that survives an extraction unchanged. Pinned by
`Architecture/LayeringTests.cs`, which reads signatures only — a method *body*
calling EF would slip past, accepted because these are POCOs and the alternative is
an IL reader.

**Rule 4 — a namespace begins with the name of the project that holds it**
(Phase 13.6). Not cosmetic: `Jobkeep.Models` spanned seven projects and
`Jobkeep.Modules.Skills` named both the module and Contracts, which is how
`DispatchTests` came to load the Contracts assembly twice, never load the Skills
module at all, and check none of its handlers behind a line that compiled and
passed. Pinned by the same test file.

### What is enforced by what

| Rule | Enforced by | If it breaks you find out |
|---|---|---|
| A module references no other module | `ModuleBoundaryTests`, walking assembly references | test run |
| A module takes only its own `DbContext` | `ModuleBoundaryTests`, constructor parameters | test run |
| `SharedKernel` / `Contracts` reference nothing of ours | `ModuleBoundaryTests` | test run |
| Every crossing is a contract call | the compiler — there is no project reference | **build** |
| No cross-schema foreign key | the migrations; six were dropped at 13.3b | build of the DB |
| `Domain/` is free of EF | `LayeringTests` | test run |
| Namespace names its project | `LayeringTests` | test run |
| Every published notification has a subscriber | `DispatchTests` | test run |

### Ownership

| Module | Owns | Notes |
|---|---|---|
| Applications | `job_applications`, `job_postings`, `companies`, `job_requirements` | The core aggregate. |
| Analytics | nothing — reads `posting_skills`, `skills`, `job_applications`, `job_postings`, `companies` | Read-only; aggregates in SQL, never in C#. The last three belong to Applications. This was rule 2's first deliberate exception (decision 13); since decision 17 it is simply a read, and needs no exception. |
| Ai | `ai_analyses` | Phase 4, built. Sits behind `IChatClient` (Ollama, local only). Reaches Applications-owned tables through `IPostingContract` rather than directly, because it **writes** to them -- the read-only exception in decision 13 does not cover a writer. See decision 14. |
| Documents | `document_imports`, `resumes`, `resume_skills`, `resume_experiences`, `resume_educations` | Phase 4.5, built. Turns an uploaded PDF/DOCX/text file into a *draft*, and into real rows only once a human confirms. **Since Phase 13.2c it names no other module:** it creates applications through `IApplicationContract.CommitPostingAsync` rather than by calling `CreateApplicationHandler` directly (which is what decision 15 accepted as temporary), and reaches `skills` through `ISkillCatalog`. It now *exposes* one contract of its own, `IResumeContract` — three methods since 13.2e, when Match needed the CV text for the match check. |
| Skills | `skills`, `skill_aliases` | **Promoted to a real module in Phase 13.2**, having been "owned by nobody, deliberately" for ten phases. That worked while it was a folder; it could not survive the split, because a table with no owner has no context and no schema. Nothing about the *shared vocabulary* argument changed — Applications links postings to it, Documents links résumés to it, Analytics aggregates over it, and that is what makes "skills these jobs ask for that my CV never mentions" a join rather than a comparison across two vocabularies. What changed is that access is now three verbs on `ISkillCatalog` (`GetAsync`, `FindByNameAsync`, `FindOrCreateAsync`) instead of four modules writing the table. The **link** tables (`posting_skills`, `resume_skills`) stay with their own modules; neither writes the other's. `skill_aliases` arrived in Phase 14 and is resolved **inside** `SkillCatalog` — skills first, aliases only on a miss — so no call site changed and none should start. |
| Match | `match_results` | Phase 5, built (renamed from `Ats` / `ats_results` on 2026-09-04). **Since Phase 13.2e it names no other table.** It used to read `posting_skills`, `skills`, `job_requirements`, `resumes` and `resume_skills` — five tables across three owners — as the module decision 17 was written for; all five are now contract calls (`IApplicationContract`, `IPostingContract`, `IResumeContract`, `ISkillCatalog`). Phase 13 asks whether a module can be *lifted out*, and against that question read-only buys nothing: a `SELECT` across a boundary is exactly what stops working when the boundary becomes a network. |
| Identity | users | Not yet built; touches every module's queries when it lands. |

### What this does *not* mean

**Not Clean Architecture's four projects.** This paragraph used to say so as a
refusal; Phase 13 makes it a distinction, and it is the more interesting version.
The layers exist — `Domain/ Application/ Infrastructure/ Persistence/` — but they
are **folders inside a module**, not projects across the app. The layer-first shape
(one `Domain`, one `Application`, one `Infrastructure` for everything) was planned
first and then thrown away *before any code moved*, on 2026-09-01, because it spreads
each module across four projects and leaves one context over every table: a later
extraction becomes a redesign rather than a code-move. **The module is the unit that
becomes a service; the layer is not.** So the compiler enforces the boundary that
will move, and a test enforces the one that will not.

The dependency rule is applied where it earns its keep and refused where it does
not. It earns it in `Domain/` — the status lifecycle was the first case, and still
sits in `Applications/Domain/ApplicationStatusTransitions.cs`, called by the update
slice rather than owning a layer of its own. It does not earn it between
`Application/` and `Persistence/`: a handler takes its `DbContext` directly, and the
interface that would "invert" that dependency is `IJobApplicationRepository` under a
new name.

---

## 3. Why not microservices yet

The long-term goal is microservices, and since 2026-09-01 that is a stated goal
rather than an inferred one. **This is deliberately not that, yet — but Phase 13
changed what "yet" costs.**

Today: 13 tables, one user, one deployable, a near-zero-cost budget. Splitting that
across several Lambdas with separate databases and an event bus would buy nothing
and cost real money — every service carries its own cost floor, which fights
priority #1 directly. It would also trade in-process method calls for network
calls that can fail, and a database transaction for a distributed one.

**What Phase 13 bought is that the remaining work is a move, not a redesign.** Each
module is a project with its own schema, its own `DbContext`, its own migration
history and no foreign key leaving it; every crossing is already an interface in
`Jobkeep.Contracts`, which is the wire schema when one of them becomes a network
call; and neither API surface names a handler, so the dispatcher is the seam a
transport swap goes through. The honest remaining cost is written down rather than
implied: **there is no outbox**, so a crash between a publisher's commit and its
notification loses the event, and the delete-side contract checks that replaced six
foreign keys are weaker than the keys were — a TOCTOU race where Postgres used to
hold a transaction. Those two are the price of the boundary, and they are Phase 15's
problem, not a surprise.

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
| 2 | **REST and GraphQL coexist** over one data layer (one repository until Phase 2.3; one set of slice handlers since). GraphQL did not replace REST; both were kept so the project demonstrates each. Note this is a *portfolio* choice — no comparable product ships a public API. | Phase 2b | Accepted |
| 3 | **Serverless deploy (Lambda + Function URL, no API Gateway).** Both surfaces ride one Lambda on the permanent compute free tier. Postgres is Neon's free tier rather than RDS/Aurora, which keeps the Lambda out of a VPC and therefore off a NAT Gateway — the network boundary, not the database, is what sets the cost floor. Reasoning and the rejected alternatives are in the deploy phase doc. **SUPERSEDED by decision 22 (2026-09-04): AWS is not the deployment target and Phase 10 is dropped.** What survives is the rule this decision was really made by — *nothing in the deployed architecture may bill per hour* — and Neon, which was chosen on its own merits and is not AWS-specific. | Phase 3, now **Phase 10** | **Superseded** |
| 4 | **AI behind `Microsoft.Extensions.AI`'s `IChatClient`,** so Ollama (local, free) and a hosted API swap via config. | Phase 4 | Planned |
| 5 | **Vertical slices replace `IJobApplicationRepository`.** Supersedes the former CLAUDE.md rule "never bypass this interface". The interface was already carrying a use-case method, and four planned sub-phases would have pushed it past roughly 20 methods. `InMemoryJobApplicationRepository` retires with it — the no-DB dev mode is better served by Postgres in Docker, which is what the README already tells you to run. **Carried out in full by Phase 2.3**, one phase earlier than planned: the read half could not be migrated on its own without leaving `IgnoreCycles` in place. **Phase 13 did not reopen this, and it is worth saying so, because a phase that adds `Domain/ Application/ Infrastructure/` folders looks like a phase that brings the repository back.** It does not. A handler still takes a `DbContext` directly — its own module's, since 13.3b — and the interface that would invert that dependency is `IJobApplicationRepository` under a new name. What 13.5 changed is only the edge: the routes moved out of `<Module>Module.cs` into five `[ApiController]` classes, so `Program.cs` now calls `MapControllers()` rather than a `Map*Module()` per module. The slice underneath is untouched. | 2026-08-25 | Accepted — **done** |
| 6 | **Modular monolith over microservices,** with the extraction triggers in section 3. **Restated at Phase 13, not reversed: the destination is now stated rather than hedged.** On 2026-09-01 the user said *"long term goal is the microservice"*, which turned this from "modular monolith, extract if a trigger fires" into "modular monolith **on the way to** services, and every phase should make the eventual move cheaper." One deployable is still the right answer today and the extraction triggers below still gate it. What changed is the standard the shape is held to: not *is this safe?* but *can this module be lifted out?* — which is what reversed decision 17 and produced the project-per-module split. | 2026-08-25, restated **2026-09-01** | Accepted — destination restated |
| 7 | **MVC controllers — proposed for retirement.** `backlog.md` committed to adopting attribute-routed controllers as "the convention most teams use". Attribute-routed controllers organise code by *technical layer*, which cuts across vertical slices. Minimal APIs grouped per slice are equally mainstream in .NET 8+. Recommend dropping the adoption; confirm rather than silently discard. **REVERSED AND ADOPTED AT PHASE 13.5 (2026-09-03).** The recommendation was to drop controllers and keep minimal APIs grouped per slice, and it stood for over a year of phases. Phase 13 overturned it for a reason the original argument did not have: the objection was that controllers *organise code by technical layer, cutting across vertical slices*, and that is only true when the controller holds logic. Here it holds none — since 13.4 an action sends a message and the mediator finds the handler, so the controller is a routing table with a Swagger tag on it, and the slice is exactly as vertical as it was. What is bought is the shape a reviewer expects and a single `MapControllers()` in place of six `Map*Module()` calls. All 29 routes, status codes and response bodies are byte-identical; the mechanics that keep them so (`Task<IResult>` + `.ToHttpResult()`, and JSON configured in **two** places because MVC does not read `ConfigureHttpJsonOptions`) are in the phase doc. | 2026-08-25, reversed **2026-09-03** | **Reversed — controllers adopted, Phase 13.5** |
| 8 | **Upgrade to `net10.0`.** `net8.0` reaches end of support **10 Nov 2026**; .NET 10 is LTS through Nov 2028. Slotted as Phase 2.6, before the AWS deploy, so Phase 3 lands on a supported runtime. | 2026-08-25 | **Adopted — Phase 2.6** (2026-08-26). No C# changed; four project/config files. Also patched a critical advisory on `HotChocolate.Language` 14.3.0 that the restore surfaced. |
| 9 | **When user scoping lands, `skills` stays global.** Every other table gets an `OwnerUserId`; the shared `skills` table does not. Per-user skill rows would destroy the single `GROUP BY` that decision 1 and the Phase 2.4 analytics both rest on — which is the entire reason Postgres was chosen. The accepted cost is real and should be said out loud: one user's skill taxonomy is visible in aggregate to another. Revisit if JobKeep ever stops being a personal tool. Proposed by [`security-and-data-audit.md`](security-and-data-audit.md) §5 step 3. **CONFIRMED by the user 2026-09-04**, when the Phase 11 gates were put to them directly; the accepted cost was restated at the point of confirming rather than assumed away. The scoping root is settled and this does not need re-asking when Phase 11 is finally built. | 2026-08-25, confirmed 2026-09-04 | Accepted |
| 10 | **A slice handler returns `SliceResult<T>`; each surface translates it at its own edge.** The handler decides Ok/NotFound/Invalid without knowing its caller; `ToHttpResult` maps that to 404/400 and `ValueOrThrow` to a `GraphQLException` carrying `NOT_FOUND`/`INVALID_INPUT`. This is the mechanism behind "one rule, one implementation" — before it, the REST create path hand-rolled null checks and the GraphQL mutation path had none (A4). Named `SliceResult` because HotChocolate's GreenDonut publishes a `Result<T>` through a global using. | Phase 2.1 | Accepted |
| 11 | **No `HotChocolate.Data`; A1 gets the partial fix instead.** The obvious way to close the GraphQL over-fetch is `[UseProjection]`, which resolves per requested field. It only works over an `IQueryable` of the **entity**, so resolvers would return `IQueryable<JobApplication>` — putting EF entities back in the published schema and reopening A7, which the same phase had just closed. Trading a confidentiality finding for a performance one is a bad trade at this size. Taken instead: flat `.Select(...)` projections into DTOs, which remove the include graph but not per-field granularity, with the remainder written into A1 rather than described as done. Revisit if a DTO grows expensive enough that the difference is measurable. | Phase 2.3 | Accepted |
| 12 | **Documentation becomes change-triggered, not continuous.** Standing docs were being refreshed every phase, and the recurring cost was not the writing — it was sweeps over files the change never touched. Phase 2.2 spent **13.5M tokens** on two "audit my docs" prompts, as much as the whole security audit cost to write, and both ran late in a 297-turn session at ~286k/turn. Now: in-code comments and phase-doc deviations always (near-free, and they are the interview record); standing docs only when a change made them *factually wrong*; audits only at phase-group boundaries and only in a fresh session. Accepted cost: the standing docs lag between sweeps. Rules in `CLAUDE.md`, "Documenting as you go". **Extended on 2026-09-02, at the user's instruction, after Phase 13.3c spent a meaningful slice of a session redrawing two SVGs: generated artefacts are FROZEN until 1.0 ships on master.** No diagram, chart or rendering is produced mid-phase, even when a change makes one wrong and even when a phase doc or a skill says to. The trigger is a merge to master, not a phase number. When a change *would* have triggered a redraw, one line goes in the phase doc saying so — which keeps the debt visible and makes the eventual redraw a list rather than an investigation. Everything in the always-tier is unchanged: in-code comments on tradeoffs, phase-doc Status and deviations, tests, and a one-line fix to a doc sentence a change made factually wrong. | 2026-08-26, extended **2026-09-02** | Accepted — **extended** |
| 13 | **Analytics reads across the module boundary, and that is the accepted trade.** Rule 2 says a module queries only its own tables. Analytics owns none, and the funnel and company rollup read `job_applications` / `job_postings` / `companies`, all owned by Applications. The alternative was a contract on Applications with one method per analytics question — which is `IJobApplicationRepository` returning under a new name, one phase after it was deleted for growing exactly that way. Reporting is the ordinary exception to a module boundary, because a report is by definition a question about several modules at once, and the **read-only** constraint is what keeps it honest: Analytics can never leave another module's data in a state that module did not choose, so the coupling is to a shape, not to a lifecycle. Cost, stated rather than hidden: extracting Analytics later stops being a pure code-move and needs those tables reachable — a read replica, a view, or an event feed rebuilding a local read model. Bounded migration, not a redesign. Reasoning also sits in `Modules/Analytics/AnalyticsModule.cs`, where someone adding a slice will actually read it. **Generalised by decision 17** (Phase 5): the read-only constraint turned out to be the whole argument, so cross-module *reads* are now ordinary and this row stopped being an exception. **And then un-generalised by Phase 13.** Decision 17 made this row unnecessary; Phase 13 makes it wrong again, in the other direction. Analytics no longer reads another module's tables at all — it owns no table, has its own `AnalyticsDbContext`, and reads three views that live in the `applications` schema, reached the same way every other crossing is. The cost this row honestly predicted (*"extracting Analytics later stops being a pure code-move and needs those tables reachable — a read replica, a view, or an event feed"*) is exactly the bill Phase 13 chose to pay up front rather than at extraction time. Read this row for the argument, not for the rule. | Phase 2.4 | Superseded by 17, then by **Phase 13 rule 2** |
| 14 | **A module that *writes* across the boundary gets a contract; decision 13 does not stretch to cover it.** Phase 4's Ai module owns `ai_analyses`, but the analyze use case reads `job_postings.Description` and writes `posting_skills` rows, both owned by Applications. Decision 13 permits Analytics to read across the same boundary, and every load-bearing word of that argument is about it being **read-only** -- the coupling is to a shape, not a lifecycle, because Analytics can never leave another module's data in a state that module did not choose. An analyzer that stamps rows `Source = AiExtracted` does exactly that, so reusing the exception would have quietly retired the constraint that justified it. Resolution: `IPostingContract` on Applications (`Modules/Applications/PostingContract.cs`), **capped at two methods** -- read the text, write the extracted skills. The obvious objection is that this is the contract `AnalyticsModule.cs` rejected as `IJobApplicationRepository` under a new name, and the answer is about what bounds each one: a reporting contract needs a method per *question* and questions are unbounded, whereas this needs a method per *side effect on someone else's tables* and there are two. The cap is a comment, not a compiler -- a third method means the boundary is in the wrong place, and the fix then is to move the use case into Applications or give Ai the tables outright, not to add the method. Cost accepted: one interface of indirection on a call that would otherwise be two lines of EF, bought to keep a *write* boundary real. | Phase 4 | Accepted |
| 15 | **Calling another module's use case is not the boundary crossing rule 2 forbids; reading its tables is.** Phase 4.5's Documents module commits a confirmed job-ad draft by invoking Applications' `CreateApplicationHandler`, then its `AddRequirementToPostingHandler`, rather than inserting `job_applications` / `job_postings` / `job_requirements` rows itself. That looked at first like the same crossing decision 14 built `IPostingContract` for, and it is not — the distinction is worth writing down because it decides how much contract this architecture needs. Rule 2 protects a module's **invariants**: what forbids reaching into `job_postings` is that Applications decides what a valid posting is (company and title required, company resolved against a unique index rather than inserted blind), and a second writer re-deciding that is how one rule ends up with two implementations (A4). Calling the handler is the opposite — the rule runs, once, where it lives. A vertical slice *is* the module's public API in this architecture; the contract exists for the cases a slice does not cover. So `IPostingContract` stayed at its two methods with a second consumer added, which is evidence the cap was drawn in the right place rather than pressure to move it. Cost accepted, and it is real: Documents now depends on Applications at compile time, so extracting either into its own service turns these two calls into network calls. That is a bounded, visible cost on two call sites, and much cheaper than a contract that grows a method per use case — which is exactly how `IJobApplicationRepository` died. The narrower rule that falls out and should be applied next time: **cross a module boundary through its use cases; use a contract only for the side effects its use cases do not express.** **Superseded at Phase 13.2c.** `CommitImport` no longer calls `CreateApplicationHandler` and `AddRequirementToPostingHandler`; it calls `IApplicationContract.CommitPostingAsync`. The distinction this row drew is still right — calling a use case runs the owner's rules once, where they live — and the reason it had to go is not that the argument failed but that the *conclusion* did: a compile-time call from Documents into Applications is a project reference, and Phase 13's whole claim is that a module references no other module. So the rule the contract had to learn was the one this row's cost paragraph named: **a contract that writes must report a PARTIAL write rather than throw.** `PostingCommitResult.Incomplete` carries the ids alongside the error, because what a caller needs after a half-finished write is the one thing an exception cannot carry — what got created. | Phase 4.5 | Superseded by **Phase 13.2c** |
| 16 | **`IChatClient` is a shared dependency, not the Ai module's property.** Phase 4 registered the model client inside `AddAiModule`, which was right while one module called a model. Phase 4.5 added a second — Documents structures an uploaded resume — and the registration moved to `src/Shared/ModelClient.cs`. The rule: **the Ai module owns the `ai_analyses` table, not a technology.** Getting this backwards has a specific failure mode, and it is one this repository has already lived through: Ai would grow a slice every time any feature anywhere wanted a model, accumulating use cases belonging to other parts of the app, and would end up as `IJobApplicationRepository` wearing a different hat. Leaving it also made Documents silently depend on `AddAiModule` having been called, a coupling nothing in `Program.cs` showed. Each module now owns its own prompts, its own schemas and its own tables, and injects the client the way it injects `AppDbContext`. Cost: none identified — the move was mechanical, and `AiOptions` kept the one setting (`MaxDescriptionChars`) that really is the analyzer's. | Phase 4.5 | Accepted |
| 17 | **The boundary rule is about writes, not reads. A module may read another module's tables; it may not write them without a contract.** Phase 5's Ats module reads `posting_skills`, `skills` and `job_requirements` (Applications), `resumes` and `resume_skills` (Documents), and writes only `match_results` (`ats_results` at the time), which it owns. Under rule 2 as originally written that is five violations needing a contract; under this decision it is none. The case for narrowing the rule is that the narrower version is what the three existing exceptions were already saying. Decision 13 let Analytics read across, and **every load-bearing word of that argument is about being read-only** — *"can never leave another module's data in a state that module did not choose, so the coupling is to a shape, not to a lifecycle."* Decision 14 built `IPostingContract` for Ai specifically because Ai **writes** `posting_skills`, and said so: *"the read-only exception in decision 13 does not cover a writer."* Decision 15 let Documents call Applications' handlers because rule 2 protects **invariants**, and an invariant is a constraint on what gets written. Three phases, three exceptions, one distinction underneath all of them. Stating it once is cheaper than granting a fourth exception, and it is what stops `IPostingContract` growing a third method for posting skills — the cap's own comment says a third method means the boundary is in the wrong place, and this decision says the boundary was never in the wrong place: the read simply did not need a contract. **What is given up, stated plainly:** a reader still couples to another module's *schema*, so renaming a column breaks a module that did not change, and a compiler will say so — loudly, at build time, which is the cheap failure. The extraction cost is real and unchanged from decision 13: Ats reading five tables it does not own means extracting Ats (or Applications, or Documents) needs those reads served another way — a view, a read replica, or an API call — rather than being a pure code-move. That cost is bounded and visible; a contract per read question is `IJobApplicationRepository` returning for the third time, which is neither. **The rule to apply next time:** cross a module boundary by reading freely, by calling the owner's use cases when its rules must run, and by contract only for a side effect those use cases do not express. **REVERSED BY PHASE 13, deliberately, and this is the most consequential reversal in the record.** Decision 17 asked *"is this safe?"* and answered it correctly — a reader cannot leave anyone's data in a state they did not choose, so a cross-module `SELECT` needs no contract. Phase 13 asks a different question, the one the user's stated destination forces: *"can this module be lifted out?"* Against that, read-only buys nothing, because a `SELECT` across a boundary is precisely what stops working when the boundary becomes a network. So every crossing needs a contract now, reads included; 13.2e duly added the `GetPostingSkills` method this decision argued was unnecessary, and since 13.3b it is not a rule at all but a fact — another module's tables are not in your context's model. **What replaced the two-method cap on `IPostingContract` is not a bigger number but a test:** does a method name a *fact about the thing*, or a *question the caller has about its own feature*? The second kind stays with the caller, which is why the ATS skill gap never became a fifth method. And the bill this row itemised came due exactly as written: five reads became five contract calls, and the reader-couples-to-schema failure it called "the cheap failure" is now a compile error instead of a silent one. | Phase 5 | **Reversed by Phase 13** |
| 18 | **The roadmap is ordered by compounding cost, and the remaining phases were renumbered to match.** Until now a phase number meant *when it was written down*, and that had drifted from *when it would be built*: Phase 3 sat parked while 4, 4.5, 5 and 6 were built past it, and "Phase 2.7" was cited in five documents and three source comments for four phases without ever being written — an unowned number is how work goes missing. The remaining work was inventoried (27 items: backlog candidates, the audit's F1-F18, the gap register, and the three API gaps step 6.3 found) and ranked **P1-P4 by one test: does deferring this make the later work bigger?** Most of the list fails that test — reminders, contacts, export, interview rounds, a target profile and the HotChocolate major all cost the same in six months as today, so they go last regardless of appeal. Three items pass it, and they are now Phases 7, 8 and 11. **Phase 7 (integrity + the case-insensitive natural key) is first** because it compounds along two axes that are both moving — one per *write path*, where the audit already caught F8 turning one stale path into four in a single phase, and one per *row of real data*, since every duplicate `C#`/`c#` filed today is a row the eventual merge migration must reconcile. It is also the only roadmap item already producing wrong output, in two shipped places: a split `/stats/skill-demand` count, and the Phase 5 ATS check reporting `PostgreSQL` missing from a CV that names it. **Phase 8 (soft delete) is second** because its cost is overwhelmingly front-end and grows per screen — five list routes today with no shared list component, by deliberate design — and because it needs the filtered unique indexes Phase 7's key work already rebuilds, so splitting them migrates the same three indexes twice. **Phase 11 (auth) is the largest compounder and is nonetheless fourth**, which is the one placement that needed measuring rather than asserting: its backend growth is linear in slice count (~20% over phases 7-10, not a multiple), its front-end growth is near zero because every call goes through one `request()` function in `web/src/lib/api.ts`, and it is gated on a threat model the deploy creates and on decision 9 still being *Proposed*. **Renumbering rule, and the limit on it:** phases 1-6 keep the numbers they shipped under — numbers are history for built work and build order for unbuilt work. Forward-looking references were updated; references inside *done* phase docs were not, because they are dated records of what was decided then and rewriting them would make a past document claim something it did not say. `phase-10-aws-deploy.md` and `phase-12-feature-expansion.md` each carry a "formerly" note so the reversal stays legible. Cost accepted: for a while, `grep "Phase 3"` returns both the old name and the new one. | 2026-09-01 | Accepted |
| 19 | **One project per module, with Clean Architecture's layers as folders inside it.** Phase 13's founding decision, and it replaced a plan written the same day. The discarded plan was layer-first — one `Domain`, one `Application`, one `Infrastructure`, one `Api`, with a single `IApplicationDbContext` over all 13 `DbSet`s — which is textbook Clean Architecture and reads well to a screening reviewer. It was thrown away **before any code moved**, when the user restated the goal as microservices, because it spreads each module across four projects and leaves one context over every table: extraction then becomes a redesign rather than a code-move. The rule underneath: **the module is the unit that becomes a service; the layer is not.** So the compiler enforces the boundary that will move (a module references no other module — there is no project reference to make one with) and a test enforces the one that will not (`Domain/` touches no EF). Carried out over six sub-steps, each ending runnable: 13.1 the projects, 13.2 the logical boundary, 13.3 the physical one (five schemas, six cross-schema foreign keys dropped and replaced in application code), 13.4 the mediator, 13.5 the controllers, 13.6 namespaces and this record. **Cost accepted, and it is not small:** ten projects instead of one, six migration histories instead of one, no cross-schema referential integrity, and no outbox behind the notifications that replaced the cascades. Every one of those is a real regression in local correctness bought for a specific future move — which is the trade to defend out loud, not to hide. | Phase 13 (2026-09-01 → 09-03) | Accepted — **13.1-13.6 done** |
| 20 | **`martinothamar/Mediator` over MediatR, and a dispatcher at all.** 33 requests became `IRequest<T>` and 53 call sites became `Send(...)`, so neither API surface names a handler. The library choice needed the user's approval because of a finding worth recording: **MediatR went commercial in mid-2025** under Lucky Penny Software — free below a revenue threshold, which a personal portfolio sits under, but a licence key and a threshold are a dependency on someone else's business model. `martinothamar/Mediator` is MIT with no key and no threshold, and is **source-generated** rather than reflection-based, so registration is compile-time and the handler-to-request mapping is checkable. `Mediator.Abstractions` is pinned in `Jobkeep.Contracts` rather than in each of the six modules, because a package is not a Jobkeep assembly and the boundary tests only see ours. **The notification half was hand-written first, at 13.3c** — three types, ~30 lines in `SharedKernel/DomainEvents.cs` — deliberately, so that adopting the library swapped the types and left every call site alone. `DispatchTests` pins the property that matters: every published notification has a subscriber, because both events exist to replace a dropped CASCADE and an unsubscribed one means the row that cascade used to delete is surviving in silence. | Phase 13.4 (2026-09-03) | Accepted |
| 21 | **A namespace begins with the name of the project that holds it.** The last step of Phase 13, done last on purpose — a rename while things are still moving is a rename done twice. Four namespaces spanned more than one project: `Jobkeep.Models` held entities from five modules *plus* Contracts *plus* SharedKernel, `Jobkeep.Shared` held SharedKernel and Api, `Jobkeep.GraphQL` named no project at all, and the contract interfaces sat in `Jobkeep.Modules.<X>` beside the modules they describe. The whole of Phase 13 is about making the reference graph real, and a namespace that spans projects hides that graph at exactly the place a person reads it: the using block. **It was not cosmetic, and there is a scar.** `DispatchTests` loaded its six module assemblies through one type each, and the Skills line read `typeof(Jobkeep.Modules.Skills.ISkillCatalog)` — a type in the **Contracts** assembly. The list named Contracts twice, never loaded Skills, and left every handler in that module unchecked behind a line that compiled and passed. The rename is what surfaced it, because the reference stopped resolving. Now pinned by `LayeringTests`, together with the `Domain/` layering rule, by reflection rather than by adding an architecture-test package — both rules came to a dozen lines, and a dependency is a cost every future session pays. | Phase 13.6 (2026-09-03) | Accepted |
| 22 | **The AWS deploy is dropped, and auth moves to last while keeping its number.** Two calls by the user on 2026-09-04, made together. **(a) AWS is not the deployment target** — *"we are going to drop the AWS deploy, we gonna use different free tools later on"* — so Phase 10 goes from *Parked* to *Dropped* and decision 3 is superseded. **The drop costs zero source changes, and that is the payoff of having parked it:** the Lambda entry point was never written, so four phases of work accumulated no AWS coupling, and `src/Dockerfile` plus `compose.yaml` already build the API for any host that takes a container. Three things survive the target: the rule the phase actually produced (**nothing in the deployed architecture may bill per hour** — it rejected RDS, Aurora and the NAT Gateway, and it is the test any replacement must pass), **Neon**, chosen on its own merits and never AWS-specific, and the research itself as a dated answer to *why not X*. **The one real hazard is a trigger hung off a cancelled event:** the doc/security-audit sweep and the audit's transport & secrets hardening were both due "when the deploy unparks", which will now never happen. Both are re-hung on *before whatever deploy replaces Phase 10*. That is the same failure mode decision 18 named — an unowned number is how work goes missing, and so is an unowned trigger. **(b) Phase 11 (auth) builds last**, after 8, 9 and 12. Dropping the deploy did not close its gate, it removed its date: there is still nothing on `localhost` to authenticate against. Deferring it is affordable because its compounding was measured rather than asserted — ~20% linear growth in slices to re-scope, near zero on the front end. **Its number stays 11, which knowingly breaks decision 18's "numbers are build order for unbuilt work".** Swapping 11 and 12 would touch 15 references across five documents to correctly sort two rows, one of which (12) is a placeholder rather than a plan. Cost accepted and stated where it is read: the roadmap table no longer sorts top-to-bottom, so the table carries a line saying 11 runs after 12. Revisit if a third phase ever lands out of order — at that point the number is lying often enough to be worth the churn. | 2026-09-04 | Accepted |


---

## 5. Gap register

Measured against what Melbourne .NET job ads actually ask for (see section 6).
Ordered by portfolio value per unit of effort.

| Gap | Why it matters | Home |
|---|---|---|
| ~~**Automated tests**~~ | **Done — Phase 2.2.** 55 tests: EF mapping, delete-behaviour matrix, find-or-create dedup, REST status codes, REST/GraphQL parity. Confirmed the premise: every assertion in `MappingTests` would pass vacuously or throw on EF InMemory. | `phases/phase-2.2-tests-and-ci.md` |
| ~~**CI/CD**~~ | **Done — Phase 2.2.** `.github/workflows/ci.yml`: restore, build, test on every push and PR. Installs one SDK (10.0.x) since Phase 2.6 — it runs the `net10.0` tests *and* reads the `test` section of `global.json`. Before 2.6 it needed 8.0.x alongside. | `phases/phase-2.2-tests-and-ci.md` |
| ~~**Response DTOs**~~ (A2) | **Done — Phase 2.3.** Every route and field returns a DTO; `IgnoreCycles` is gone. The API contract no longer moves when the schema does. | `phases/phase-2.3-list-queries.md` |
| **GraphQL projections** (A1) | **Partly done — Phase 2.3.** The include graph is gone and a list request is two statements instead of five; per-field projection is not, and buying it would reopen A7 (decision 11). Still good interview material, and better for being unfinished: "I measured what my resolver loaded, it was the whole graph, and the clean fix would have undone a security fix from the same phase." | **P4** — revisit post-deploy |
| ~~**docker-compose**~~ | **Done — 2026-09-01.** `compose.yaml` + `src/Dockerfile` + `web/Dockerfile`: Postgres, the API and Vite in three containers, one command, nothing installed but Docker. It did not replace `run.cmd` at the time — that stayed the fast inner loop for C#, because the API image is a published build rather than a bind mount. **`run.cmd` and `scripts/run.ps1` were then deleted on 2026-09-01 at the user's instruction**, making compose the single launcher: two stacks binding the same three ports is a footgun that pays for itself only while someone is editing C# every few minutes, and the cost of the rebuild was judged worth one obvious way in. The front end *is* the real dev server with `./web` mounted, on the grounds that a container which took hot reload away would simply go unused. | root `compose.yaml` |
| **Health check endpoint** | `/health` hitting the DB. Needed before the deploy anyway. | **The deploy that replaces Phase 10** (dropped 2026-09-04, decision 22) |
| **Auth / multi-user** | Architectural — every query becomes user-scoped. Already correctly deferred in the backlog. Note the cost compounds: every phase built before it lands adds queries to re-scope — **linear in slice count, and near-flat on the front end**, because every call goes through one `request()` function. That measurement is what let it sit behind the deploy, and it is what makes moving it to **last** affordable (decision 22). See [`security-and-data-audit.md`](security-and-data-audit.md) F1. | **Phase 11 — last on the roadmap** |
| **Structured logging / observability** | Serilog + correlation IDs. Meaningful once deployed and something can actually go wrong unobserved. | **The deploy that replaces Phase 10** (dropped 2026-09-04, decision 22) |
| **Audit & integrity baseline** (A8, A9) | Interceptor-maintained timestamps, DB-side defaults, CHECK constraints, `xmin` concurrency token, bounded text, the two missing indexes. One migration, no auth needed — the cheapest real fix on this list, and it corrects a column that is already wrong. **The only item here whose cost grows while it waits: per write path (F8) and per row of accumulated duplicate data.** | **Phase 7 — next** |
| **PII, retention & transport** | `ResumeText` is an unbounded plaintext résumé; the DB connection does not require TLS; nothing is classified or has a retention rule, and APP 11.2 applies. Unlike auth, this was **never recorded as a tradeoff** — it was simply absent. | **The deploy that replaces Phase 10** (transport + retention; the Ollama-to-hosted swap is the trigger) |
| **Secrets management** | `appsettings.Development.json` is tracked and holds a plaintext password; `.gitignore` covers only `*.local.json`. Harmless today, but it is where the Neon string will land. | **The deploy that replaces Phase 10** (dropped 2026-09-04, decision 22) |

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
resume-vs-**one**-job. That is JobKeep's **Phase 5 (ATS check)**, *not* Phase 2.4
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
demonstrable work. Since Phase 2.2 it also has evidence for **tests and CI/CD**. It
has **no** evidence yet for Docker or auth.
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
