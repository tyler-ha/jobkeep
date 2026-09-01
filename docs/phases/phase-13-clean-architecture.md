# Phase 13 — module-owned Clean Architecture, on the road to services

**Status: In progress. Steps 13.1 and 13.2a–b done 2026-09-01** (branch
`phase-13/module-boundaries`, suite 239 → 244 → 246 green). 13.2c–e and 13.3–13.6 remain.

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
├── Jobkeep.Modules.Skills/        schema `skills`  — the shared taxonomy (promoted early, 13.2a)
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

### 13.2 — contracts and per-module context interfaces

The hard logical decoupling, done while the database is still safe. After this step
no module can name another module's tables, but nothing has moved in Postgres — so
it is fully reversible and every test still passes.

**Split into five sub-steps**, each ending green and runnable, because the whole of
it is several sessions' work at the context budget this project runs to. **13.2a and
13.2b landed 2026-09-01; 13.2c–e remain.**

| | Module | State |
|---|---|---|
| **13.2a** | the seam: six `I<X>DbContext`, DI, `Jobkeep.Modules.Skills` | **Done** |
| **13.2b** | Ai, Analytics | **Done** |
| 13.2c | Documents | Not started |
| 13.2d | Applications | Not started |
| 13.2e | Ats | Not started |

#### Scope correction taken 2026-09-01, before any code moved

**This section's original count was low, and the gap is the kind that only shows up
at 13.3.** It listed 15 cross-module `_db.<DbSet>` reads. There are ~10 more crossings
the compiler hides behind navigation properties: `ps.Skill.Name` in seven places,
`a.Resume.Label` in `ApplicationDetail`, `r.Resume.Label` in `GetAtsResult`, and
`ListApplications`'s `EF.Functions.ILike(ps.Skill.Name, …)` filter. Every one of them
is a join across a future schema boundary, and every one of them compiles perfectly
today and would keep compiling after 13.2 as originally scoped.

They are **in scope**, decided with the user. Leaving them makes 13.3 a schema move
*and* a rewrite of ten projections — which is exactly the failure mode 13.1's own
deviation note records, one step later and with a migration attached.

#### What landed in 13.2a

- **Six `I<X>DbContext` interfaces**, each exposing only its module's own `DbSet`s,
  in `src/Jobkeep.Infrastructure.Data/Contexts/`. `AppDbContext` implements all six.
  The location is forced rather than chosen, and the file says so at length: they
  cannot go in `Contracts` (a `DbSet<T>` will not survive a network hop, and the
  foundation-projects test forbids it taking dependencies), and they cannot go in the
  module projects (`AppDbContext` implements them, so Infrastructure.Data would have
  to reference all six modules while they reference it — a cycle that does not
  compile). They die with that project at 13.3.
- **All six resolve the same scoped `AppDbContext`**, registered in `Program.cs`.
  Deliberate: a slice holding two interfaces holds one change tracker and one
  transaction, exactly as before. Separate contexts would have made `SaveChanges` mean
  different things depending on which interface was asked — a behaviour change
  smuggled into the one step whose whole value is that it has none.
- **`IAnalyticsDbContext` has no `SaveChangesAsync`.** Decision 13's entire
  justification was that Analytics is read-only. That was asserted in a comment for
  four phases; it is now a fact about the type.
- **`Jobkeep.Modules.Skills` was promoted early** — the plan created it at 13.3.
  `ISkillCatalog` needed an owner the moment four modules were find-or-creating
  against `skills`, and parking the implementation in Applications would have meant
  moving the file again one step later for no gain in between. The `Skill` entity and
  its Fluent config stay in Infrastructure.Data until 13.3 like every other entity.
  The module has no routes, which its csproj argues is a legitimate shape rather than
  a missing feature.
- **Two architecture tests**, `No_module_takes_the_shared_context` and its canary
  `The_shared_context_allowlist_still_names_real_work`. The existing suite checks
  *assembly* references; this checks the *type* that made them necessary, because
  every module still references Infrastructure.Data and could name `AppDbContext`
  while the boundary test passed. The allowlist names Applications, Ats and Documents
  and empties at 13.2e — it is the work item, not a policy, and the canary fails if an
  entry outlives the work.

#### What landed in 13.2b

- **Ai.** `IApplicationContract.GetPostingIdAsync` — narrower than
  `IPostingContract.GetContentAsync`, which would have pulled a 20,000-character job
  ad over to discard it. `GetAnalysis` costs one extra round trip and now
  distinguishes "no such application" from "no analysis yet", which the old
  single-query shape could not; both are still 404. The comment that argued *for* the
  join is rewritten rather than deleted — its reasoning was correct under decision 17,
  which this phase reverses.
- **Analytics reads three published views**, in one additive migration,
  `AnalyticsViews`. Hand-written SQL: `ToView` keeps keyless types out of the migration
  model, so `migrations add` produced an empty `Up`/`Down` by design. Every `COUNT(*)`
  is cast to `::int` — Postgres counts in bigint, the CLR properties are `int`, and
  without the cast Npgsql refuses at read time rather than at build time.
- **`v_posting_skill_demand` stops at `SkillId`.** A view joining `skills` would not
  have removed the cross-module read, only moved it from C# where a compiler sees it
  into SQL where nothing does. `SkillDemand` resolves ids through `ISkillCatalog`.
- **The one accepted behaviour change in 13.2, and it is in `SkillDemand`:** the
  alphabetical tiebreak is now *within the page*. Before, `ORDER BY count DESC, name`
  ran before the `LIMIT`, so among skills tied on count the alphabetically-first
  survived. Now the database ties on `SkillId` and the alphabetical sort happens after
  the names arrive. Same rows in the common case; a different subset of a tied group at
  the limit boundary. `AnalyticsTests` asserts the top item only, so it did not move.
  Accepted because the alternative is the join this step exists to remove, and because
  the tiebreak was always a determinism device rather than a promise.
- **`AnalyticsModule.cs`'s long boundary argument was rewritten, not left.** It was
  answering *"is this safe?"* — and it is: a read-only module can never leave another
  module's data in a state that module did not choose. Phase 13 asks *"can this be
  lifted out?"*, where read-only buys nothing, because a `SELECT` across a boundary is
  precisely what stops working when the boundary becomes a network. The third option it
  never considered is the one that shipped.

#### What remains, and what each sub-step has to answer

The cross-module reads still to convert, counted:

- **Ats (13.2e)** — `Resumes`, `ResumeSkills` (Documents) plus `PostingSkills`,
  `JobRequirements`, `JobApplications` (Applications), and the `ps.Skill.Name` and
  `r.Resume.Label` traversals. The largest job.
- **Applications (13.2d)** — `Resumes` ×2 (Documents), `Skills` ×4, plus the
  traversals in `ApplicationDetail`, `ListApplications` and `RemoveSkillFromPosting`.
  The `ILike` skill filter becomes `ISkillCatalog` resolving the pattern to ids.
- **Documents (13.2c)** — `Skills` ×4, the `GetResume` and `RemoveSkillFromResume`
  traversals, and the `CommitImport` work below.

Then:

- **Ats's skill gap changes shape, and it must be written down.** Today it is a SQL set
  difference over `posting_skills` vs `resume_skills`. Those land in two schemas Ats
  owns neither of, so it becomes two contract calls returning `Guid[]` and an in-memory
  `Except`. That knowingly breaks CLAUDE.md's *"aggregate in SQL, not in memory"* —
  justified because the sets are tens of items and bounded, and because the alternative
  is a join that will not exist across a service boundary.
- **`IPostingContract`'s two-method cap is lifted at 13.2e, deliberately.** Its cap
  comment and `AtsModule.cs`'s "this is why it stays at two" paragraph both argue from
  decision 17, which this phase reverses. Rewrite both rather than growing past them
  quietly. `ISkillCatalog` already carries the replacement test: does a proposed method
  name a *skill operation*, or a question the caller has about its own feature?
- **`CommitImport` stops being a transaction.** It currently opens one spanning
  Documents writes and calls into Applications' handlers. It becomes: commit locally,
  then call Applications through a contract; on failure mark the import `CommitFailed`
  and leave it re-runnable — the idempotency guard it already has
  (`CommittedEntityId`) is what makes that safe. **Accepted cost:** a partial commit
  becomes possible, and is recovered by re-running rather than rolled back. Read the
  25-line comment above the `BeginTransactionAsync` call before deleting it: it names
  the duplicate-application failure the transaction was protecting against, and the
  replacement has to answer it.
- Dropping the Documents-to-Applications project reference at 13.2c also means deleting
  the `AllowedEdges` entry and the `The_recorded_exception_is_actually_visible_to_this_test`
  canary, which is written to fail at exactly that moment.

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
