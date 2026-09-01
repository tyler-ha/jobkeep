# Phase 13 — module-owned Clean Architecture, on the road to services

**Status: In progress. Step 13.1 done 2026-09-01** (branch
`phase-13/module-boundaries`, suite 239 → 244 green). 13.2–13.6 remain.

**This doc was rewritten on 2026-09-01.** The version before it (written the same
day, commit `bf968a0`) planned a **layer-first** migration: one `Domain`, one
`Application`, one `Infrastructure`, one `Api`, with a single
`IApplicationDbContext` over all 13 `DbSet`s. That plan was replaced before any code
moved, when the user restated the goal:

> *"long term goal is the microservice. We should make it right and not costing long
> term."*

The layer-first shape does not serve that. It is textbook Clean Architecture and it
reads well to a screening reviewer, but it spreads each module across four projects
and leaves one context over every table — so a later service extraction is a
redesign, not a code-move. The old plan is preserved in git rather than deleted; the
reversal is part of the record.

**This reverses `architecture.md` decisions 6, 7, 13 and 17.** That is the point,
not a side effect. Supersede them in place at 13.6, the way decision 17 supersedes
rule 2.

---

## Decisions taken — confirmed 2026-09-01, do not reopen

| Question | Decision | Why, in one line |
|---|---|---|
| Layer-first or module-first? | **Module-first.** One project per module, `Domain/ Application/ Infrastructure/` as folders inside it. | The module is the unit that becomes a service; the layer is not. Enforce with the compiler the boundary that will move, and with a test the one that will not. |
| How far does the data split go? | **A `DbContext` and a Postgres schema per module.** One database, one schema each, **zero cross-schema FKs**. | This is the single biggest thing standing between the app and a service split. Extraction then costs a connection string. |
| Separate databases now? | **No.** One database, several schemas. | Neon's free tier caps projects and local dev would need five containers, for isolation the schema split already gives. |
| How does a module reach another? | **`Jobkeep.Contracts`** — interfaces and DTOs only. A module references it; a module never references another module. | It is also what the wire schema is generated from when a module is extracted, so anything that could not survive a network hop does not belong in it. |
| Validation → FluentValidation? | **No, not in this phase.** | Validation in the handler is what stops REST and GraphQL enforcing different rules (A4). Moving it mid-migration changes error shapes for no gain a reader notices. |
| Phase 6.5 group 4 (paste text) | **Parked** until the 13.3 boundary. | It is the last work queued against the old shape and it touches `src/`. |

---

## Target shape

```
src/
├── Jobkeep.SharedKernel/          SliceResult, NaturalKey, IAuditable, ModelOptions.
│                                  Zero package references, on purpose.
├── Jobkeep.Contracts/             public interfaces + DTOs, one folder per module.
│                                  Zero package references, on purpose.
├── Jobkeep.Modules.Skills/        schema `skills`  — the shared taxonomy (13.3)
├── Jobkeep.Modules.Applications/  schema `applications`
├── Jobkeep.Modules.Documents/     schema `documents`
├── Jobkeep.Modules.Ai/            schema `ai`
├── Jobkeep.Modules.Ats/           schema `ats`
├── Jobkeep.Modules.Analytics/     no tables — reads published views
└── Jobkeep.Api/                   Program.cs, controllers, GraphQL, appsettings
```

**The reference rule, and it is the whole phase:** a module may reference
`SharedKernel` and `Contracts`. It may **not** reference another module, and it may
not reference `Api`. `Api` references everything; nothing references `Api`.
Enforced by `tests/Jobkeep.Tests/Architecture/ModuleBoundaryTests.cs`.

### Schema ownership

| Schema | Tables |
|---|---|
| `applications` | companies, job_postings, job_applications, posting_skills, job_requirements |
| `skills` | skills |
| `documents` | document_imports, resumes, resume_skills, resume_experiences, resume_educations |
| `ai` | ai_analyses |
| `ats` | ats_results |

`skills` is promoted out of Applications because it is **co-owned today** —
`posting_skills` (Applications) and `resume_skills` (Documents) both point at the
same row, and that shared row is what the Phase 7 natural-key work and the Phase 5
skill gap both turn on. The two link tables stay with their owners and hold a bare
`SkillId`.

### The five FKs that get dropped (13.3)

Measured on 2026-09-01, not estimated. Five of the thirteen already cross a module
boundary:

| FK | Direction | Today | Replacement |
|---|---|---|---|
| `ai_analyses.PostingId` → `job_postings` | Ai → Applications | CASCADE | notification on posting delete |
| `ats_results.ApplicationId` → `job_applications` | Ats → Applications | CASCADE | notification on application delete |
| `ats_results.ResumeId` → `resumes` | Ats → Documents | RESTRICT | contract check at write |
| `job_applications.ResumeId` → `resumes` | Applications → Documents | RESTRICT | contract check at write |
| `resume_skills.SkillId` → `skills` | Documents → Skills | RESTRICT | `ISkillCatalog.EnsureAsync` |

**Cost, stated plainly:** referential integrity for these five moves out of the
database and into application code, so a bug can now orphan a row Postgres used to
refuse. That is the actual price of a service boundary. Paying it here, where a test
suite catches it, is the point — and `Persistence/DeleteBehaviourTests.cs`, which
currently pins the old behaviour, is rewritten to pin the new.

---

## What makes this cheap (three things, all pre-existing)

1. **No test touches a handler.** All 239 tests reach the code through **HTTP or
   GraphQL** — no `new *Handler(` and no `GetRequiredService<*Handler>` anywhere in
   `tests/`. The suite is a contract test over the wire, so a total internal
   restructure that preserves URLs and payloads is verified by it for free. This is
   unusual and it is the single biggest cost reducer here. **Confirmed in practice:
   13.1 moved 60 files and needed zero test edits.**
2. **The front end only knows URLs.** `web/src/lib/api.ts` is plain `fetch` against
   paths. Preserve the routes and the front end costs **zero**, including its 49 tests.
3. **The handlers never knew about HTTP.** Only the routing half of each
   `*Module.cs` did — which is why the module projects could become plain class
   libraries in one step.

---

## The work, in order

Each step ends with the suite green and the app runnable under
`docker compose up --build`. That is what makes this splittable across sessions.

### 13.1 — the project split — **DONE 2026-09-01**

Nine projects, files moved by **module**, zero behaviour change. 239 → 244 tests.

**Deviation from plan, and it is the interesting one.** The plan said
`Models/*.cs → each module's Domain/`. That is wrong and would have failed:
the entity graph has ~11 navigation properties crossing module lines
(`JobPosting.AiAnalysis`, `AtsResult.Resume`, `PostingSkill.Skill` … in both
directions). Splitting the classes before splitting the foreign keys means either
**circular project references** or rewriting every projection that traverses one —
a behaviour change smuggled into the one step whose entire value is that it has
none.

So the entities, `AppDbContext`, the interceptor and the migrations were quarantined
into a tenth project, **`Jobkeep.Infrastructure.Data`**, which every module
references. Its csproj says at length that it is scheduled for deletion in 13.3.
The coupling is unchanged and visible in one place, rather than half-removed in nine.

Other deviations worth recording:

- **`ModelOptions` moved to SharedKernel; `ModelClientRegistration` stayed in Api.**
  Three modules inject the settings, only the composition root may name OllamaSharp.
  The separation `ModelClient.cs` already argued for in prose is now enforced by the
  compiler — a module physically cannot reach the provider.
- **`NetArchTest` was not added.** The plan called for it. The rule that matters at
  this step is about *assembly references*, which plain reflection tests directly and
  more honestly, with no dependency. NetArchTest works on namespaces and types, which
  is the right tool for the layering rules *inside* a module — it can arrive at 13.6
  with them. The boundary suite carries a **canary test** recording the one thing
  reflection cannot see: the compiler omits an assembly reference nothing uses, so
  the suite proves a module does not *use* another module, which is not the same
  claim as "has no reference".
- **One module-to-module edge survives**, named explicitly in the allowlist and in
  `Jobkeep.Modules.Documents.csproj`: Documents → Applications, because
  `CommitImport` calls `CreateApplicationHandler` directly (decision 15). 13.2
  removes it, and the canary fails when it does — which is the reminder to delete
  the allowlist entry too.
- **XML comments cannot contain `--`.** Nine csproj files were written with the
  repo's prose style and all nine failed `restore` with MSB4025 before a character
  of C# was compiled. Em dash, or don't.
- **`Microsoft.Extensions.Configuration.Abstractions` is deliberately not pinned.**
  Pinning it at 10.0.0 produced NU1605 against EF Relational's 10.0.11. It arrives
  transitively at the right version; a second pin is a second thing to bump, and
  this repo has been bitten twice by EF version drift already.
- Build/CI/tooling fixed in the same commit: `Directory.Build.props` for the shared
  TFM, `Jobkeep.slnx` listing all ten projects, the `Dockerfile` copying nine csproj
  with their directory structure intact (a `ProjectReference` is a relative path —
  flattening them restores fine and then fails to build) and publishing
  `Jobkeep.Api`, and `dotnet ef` now needing `--project Jobkeep.Infrastructure.Data
  --startup-project Jobkeep.Api`.

### 13.2 — contracts and per-module context interfaces — **NEXT**

The hard logical decoupling, done while the database is still safe. After this step
no module can name another module's tables, but nothing has moved in Postgres — so
it is fully reversible and every test still passes.

- `Jobkeep.Contracts` gains `ISkillCatalog`, `IResumeContract`,
  `IApplicationContract` beside the `IPostingContract` already there.
- Each module gets an `I<X>DbContext` exposing **only its own** `DbSet`s;
  `AppDbContext` implements all of them. This is the seam 13.3 cuts.
- The cross-module reads to convert, counted:
  - **Ats** — `Resumes`, `ResumeSkills` (Documents) + `PostingSkills`,
    `JobRequirements`, `JobApplications` (Applications). Five; the largest job.
  - **Applications** — `Resumes` ×2 (Documents), `Skills` ×4 (Skills).
  - **Documents** — `Skills` ×4 (Skills).
  - **Ai** — `JobApplications` ×1 (Applications).
  - **Analytics** — `JobApplications` ×2, `PostingSkills` ×1 (Applications).
- **Ats's skill gap changes shape, and it must be written down.** Today it is a SQL
  set difference over `posting_skills` vs `resume_skills`. Those land in two schemas
  Ats owns neither of, so it becomes two contract calls returning `Guid[]` and an
  in-memory `Except`. That knowingly breaks CLAUDE.md's *"aggregate in SQL, not in
  memory"* — justified because the sets are tens of items and bounded, and because
  the alternative is a join that will not exist across a service boundary.
- **Analytics reads published views, not tables.** Applications ships three
  read-only views in its own schema; Analytics maps them in a read-only context.
  This keeps the `GROUP BY` in SQL and makes the boundary a *published* interface
  rather than a peek — and it avoids the trap decision 13 named, because a contract
  with a method per question is `IJobApplicationRepository` returning for the fourth
  time.
- **`CommitImport` stops being a transaction.** It currently opens one spanning
  Documents writes and calls into Applications' handlers. It becomes: commit
  locally, then call Applications through a contract; on failure mark the import
  `CommitFailed` and leave it re-runnable — the idempotency guard it already has
  (`CommittedEntityId`) is what makes that safe. **Accepted cost:** a partial commit
  becomes possible, and is recovered by re-running rather than rolled back.

### 13.3 — the physical split

Six contexts, six schemas, six migration histories, five FKs dropped, `Skills`
promoted to its own module, `Jobkeep.Infrastructure.Data` deleted.

- Each context: `HasDefaultSchema("<module>")` +
  `MigrationsHistoryTable("__EFMigrationsHistory", "<module>")`, same connection
  string.
- **Migration reset.** The four existing migrations describe one schema and cannot
  be split. Nothing is deployed (Phase 10 is parked), so squash to one initial
  migration per module. **This drops the local dev database** — if `pgdata` holds
  real applications worth keeping, say so *before* this step and it gets a
  `pg_dump` + `ALTER TABLE … SET SCHEMA` carry-over instead of `down -v`.
- Redraw `docs/diagrams/schema-erd.svg` with the `schema-diagram` skill, deriving
  from `pg_dump --schema-only` against the migrated database (the Phase 7 note: an
  idempotent migrations script is a sequence, and reading final state out of it is
  guesswork).

### 13.4 — dispatch

33 requests → `IRequest<T>`, 53 call sites (27 REST + 26 GraphQL) → `Send(...)`, and
the cross-module writes become `INotification` — the seam that later becomes a queue.

**Decide the library at this step, not before.** MediatR went commercial (free band
below a revenue threshold; a personal portfolio sits under it, but *confirm and
record the finding here*). `martinothamar/Mediator` is MIT and source-generated.
Either needs approval before it is added.

### 13.5 — controllers

27 routes → ~6 `[ApiController]` classes, **same URLs**, and `Api/Endpoints/` — a
deliberate one-step reinstatement of a shape CLAUDE.md forbids — is deleted. Four
known traps:

- **`[AsParameters] ApplicationQuery`** becomes `[FromQuery]` on the model.
- **The multipart route.** `IFormFile` is bound **without** `[FromForm]`, because
  Swashbuckle 10 *throws* on an action carrying both and 500s the whole
  `swagger.json` for **every** endpoint. **Under `[ApiController]` the binding rules
  invert.** Re-solve it deliberately; `SwaggerDocumentTests.cs` catches it either way.
- **`[ApiController]` auto-400s on model state** and emits its own `ProblemDetails`,
  changing error *bodies* that `Rest/` and `Parity/` assert on. Decide whether the
  auto-400 or the slice's validation is authoritative — two answers is finding A4
  coming back.
- **Antiforgery.** The form route disables it explicitly; the controller needs the
  equivalent.

### 13.6 — namespaces, docs, decision record

Namespaces renamed to match projects, **last**, when nothing else is moving. Then
`architecture.md` sections 2 and 3 and decisions 5, 6, 7, 12, 13, 15, 17 superseded
in place; CLAUDE.md's "Where new code goes" and "Migration state" rewritten (both
currently carry a pointer to this doc instead); `architecture.svg` redrawn; the
inside-a-module layering rules added to the architecture suite.

---

## What is deliberately NOT in this phase

**Actually extracting a service.** The destination is a second deployable, and `Ai`
is the right first one — bursty, slow, and the only place independent scaling is a
real argument rather than a portfolio one (`architecture.md` §3). That is **Phase
14**. This phase is what makes it a directory move.

---

## Cost

**7–9 sessions**, each runnable; **70–120M tokens**. The honest comparison is Phase
2.3 (the repository retirement, 260 turns / 52.4M) plus a schema split. Note the
ledger's standing warning: a figure logged mid-phase has understated the final total
four phases running — write the row as **provisional**.

**Front end: zero.** Every URL is preserved.

## The alternative that was not chosen

Keeping the vertical-slice modular monolith and writing the comparison instead —
decision 7 closed as Accepted, plus a small standalone Clean Architecture sample so
the answer to *"do you know CA?"* is a link rather than a claim. ~1 session.
Recorded because the estimate above is only worth paying if the migration itself is
the story — and, since 2026-09-01, because the destination is services rather than
legibility, which the cheap option does not reach at all.
