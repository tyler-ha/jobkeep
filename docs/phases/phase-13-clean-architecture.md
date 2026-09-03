# Phase 13 — module-owned Clean Architecture, on the road to services

**Status: In progress. Steps 13.1, 13.2 (a–e), 13.3 (a–c) and 13.4 done, 2026-09-01
to 2026-09-03** (branches `phase-13/module-boundaries` then `phase-13/dispatch`,
suite 239 → … → 254 → 256 → 266 → 268 green).
**13.2 is complete: no module names another module's table. 13.3 is complete: no
module SHARES a table, a context or a schema with another, and the six foreign keys
that used to cross a boundary are replaced in application code. 13.4 is complete:
neither API surface names a handler — both send a message.** 13.5–13.6 remain.

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

### The FKs that get dropped (13.3)

Measured on 2026-09-01 — and **the count was wrong.** This table said five; the
schema split dropped **six**. `posting_skills.SkillId → skills` is the one it
missed, and it was missed for a legible reason: the four contract-based crossings
were derived by reading module code, and this one is a link table's second key,
which nothing in Applications' *code* mentions. It was caught at 13.3c by counting
foreign keys out of `pg_dump --schema-only` — 13 before, 7 after — which is the
whole argument for deriving the diagram from the applied schema rather than from
what anyone believes is in it.

The **Replacement** column is what 13.3c actually shipped, not what was planned;
where the two differ the plan is noted.

| FK | Direction | Was | Replacement |
|---|---|---|---|
| `ai_analyses.PostingId` → `job_postings` | Ai → Applications | CASCADE | `PostingDeleted` notification, `Ai/Application/OnPostingDeleted.cs` |
| `ats_results.ApplicationId` → `job_applications` | Ats → Applications | CASCADE | `ApplicationDeleted` notification, `Ats/Application/OnApplicationDeleted.cs` |
| `ats_results.ResumeId` → `resumes` | Ats → Documents | RESTRICT | `IAtsContract.CountResultsForResumeAsync`, asked at résumé delete |
| `job_applications.ResumeId` → `resumes` | Applications → Documents | RESTRICT | `IApplicationContract.CountApplicationsForResumeAsync`, asked at résumé delete |
| `resume_skills.SkillId` → `skills` | Documents → Skills | RESTRICT | `ISkillCatalog.FindOrCreateAsync` ordering (shipped in 13.2; the plan called it `EnsureAsync`) |
| `posting_skills.SkillId` → `skills` | Applications → Skills | RESTRICT | the same `FindOrCreateAsync` ordering, from `AddSkillToPosting` |

**The plan said "contract check at write" for the two RESTRICTs and that was half an
answer.** The write-side check already existed before the key was dropped —
`CreateApplication` and `UpdateApplication` resolve a résumé id through
`IResumeContract.GetAsync` — which is exactly why dropping the key changed no
behaviour going in, and exactly why it was not the replacement. A check at write
cannot stop a row disappearing afterwards. The delete side is the two thirds of
RESTRICT a contract has to replace, and it is what 13.3c built.

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
it is several sessions' work at the context budget this project runs to. **All five
landed 2026-09-01. 13.2 is DONE.**

| | Module | State |
|---|---|---|
| **13.2a** | the seam: six `I<X>DbContext`, DI, `Jobkeep.Modules.Skills` | **Done** |
| **13.2b** | Ai, Analytics | **Done** |
| **13.2c** | Documents | **Done** |
| **13.2d** | Applications | **Done** |
| **13.2e** | Ats | **Done** |

**No module names another module's table any more.** `AppDbContext` is resolved in
exactly one file in `src/` — `Program.cs`, where the six interfaces are bound to it —
and nothing in Postgres moved, which is what 13.3 now gets to change on its own.

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

#### What landed in 13.2c

The largest behavioural change in 13.2, and the only sub-step that touches the
front end.

- **Documents no longer references Applications.** The `ProjectReference` that
  architecture.md decision 15 accepted openly as temporary is gone, and with it the
  `AllowedEdges` entry and the `The_recorded_exception_is_actually_visible_to_this_test`
  canary written to fail at exactly this moment. It did, on schedule. `AllowedEdges`
  is now empty and kept empty: an empty allowlist is a stronger statement than no
  allowlist.
- **`ISkillCatalog` grew to its final three verbs** — `GetAsync`, `FindByNameAsync`,
  `FindOrCreateAsync` — which are the three its own comment named before either of
  the last two had a caller. `NaturalKey.Of` is now called in exactly one file in
  `src/`. Phase 7 shipped the natural key as "every writer must remember"; this makes
  it "no writer can forget", which is the difference between a convention and a design.
- **`IApplicationContract.CommitPostingAsync` is ONE method, not three.** The obvious
  translation of two direct handler calls was one contract method per handler, which
  is the shape that killed `IJobApplicationRepository`. The shape that survives a
  service split is one method per *thing the caller wants to have happened* — and it
  is also the only shape whose failure the caller can reason about, because one call
  leaves one half-state instead of three.
- **`Jobkeep.Contracts` got its own copy of `RequirementKind`.** It may reference no
  Jobkeep assembly, so it cannot name the entity enum. The mapping is an explicit
  switch at each end rather than a cast, so adding a value to one without the other
  fails to compile.

##### `CommitImport` stopped being a transaction, and what replaced it

The transaction spanned Documents' writes and calls into Applications' handlers, and
worked only because both sides happened to share a connection. It is replaced by a
three-step protocol plus one new `ImportStatus`:

1. **Claim** — mark the import committed before calling out, so a second request
   during the call is refused by the existing double-click guard. `CommittedEntityId`
   is still null, which is what "started, outcome unknown" looks like in this table.
2. **Call** — one contract call.
3. **Record** — write the application id back. From here the import is a receipt.

`CommittedEntityId` is the idempotency guard, and it is a field the table already
had. A retry that finds it set knows the rows exist and only closes the import out;
a retry that finds it null knows nothing was logged and starts over.

Two things this needed that the plan did not anticipate:

- **The contract reports a partial commit rather than throwing.**
  `PostingCommitResult.Incomplete` carries the ids *with* the error, because the one
  thing the caller needs after a partial write is the thing an exception cannot
  carry: what got created. Without it, a crash between creating the application and
  the skills would leave `CommittedEntityId` null and a retry would duplicate the job
  — reintroducing the exact failure the transaction existed to prevent. A refusal
  (validation) is distinguished from an incomplete commit by `ApplicationId ==
  Guid.Empty`, and it *rewinds* the claim to `AwaitingReview` because a refusal is a
  clean no-op.
- **`ImportStatus.CommitFailed`, which is the front-end touch.** No migration — the
  column is `varchar(20)` with no CHECK — but the TypeScript union is closed, so
  `web/src/lib/api.ts` and the Upload screen were widened: a fourth queue tab, a
  banner telling the user to confirm again, and `editable` extended so the draft is
  not locked in the one state whose whole meaning is "try again". Every URL is
  unchanged; this is a new value on an existing field, not a new shape.

**The accepted cost, stated because it is a real regression from the transaction:**
the window between the contract call returning and the id being saved is not covered.
`CancellationToken.None` closes the dominant cause (a cancelled request); what remains
is one UPDATE on an already-loaded row and is unavoidable without a distributed
transaction.

##### Two smaller behaviour changes, both deliberate

- **`RemoveSkillFromResume` became case-insensitive**, and it is a fix. The old
  comment argued for exact matching on the grounds that a loose match could delete a
  row the caller did not name — true while `C#` and `c#` could both exist, and untrue
  since Phase 7's unique index on `lower("Name")`. Routing through the catalog fixed
  it as a side effect; a test now pins it as a decision.
- **`GetResume` sorts skills in memory**, because the names are not in the
  database's hands any more. Tens of rows, already materialised. A skill id with no
  row is dropped rather than rendered blank — impossible today with the FK, a gap to
  report at 13.3 when it is gone.

##### The one thing to carry into 13.2d and 13.2e

**`ISkillCatalog.FindOrCreateAsync` SAVES, so call it before adding anything of your
own to the change tracker.** All six interfaces still resolve the same scoped
`AppDbContext`, so a save in the catalog flushes the caller's pending changes too.
`CommitImport` resolves skills *before* it builds the resume for exactly this reason:
the other order commits a half-built résumé in its own transaction, and a failure
just after leaves one the user cannot re-import, because the label uniqueness check
refuses the retry. The accepted cost that remains is an **orphan taxonomy row** — a
skill created, its link not — which is harmless because every count in Analytics is
over link rows, and find-or-create reuses the orphan next time.

#### What landed in 13.2d

The module with the most crossings, and the one where a crossing was hardest to
see: four of the six were navigation properties rather than `DbSet` references.

- **`IResumeContract`, owned by Documents, with ONE method.** `GetAsync(id)`
  returns `ResumeRef(Id, Label)` or null. Two callers wanted different things —
  create/update want existence, `ApplicationDetail` wants the label — and both are
  the same primary-key lookup, so splitting them would have been one method per
  caller's question, which is what killed `IJobApplicationRepository`. The DTO's
  omissions are the point: no `SourceText`, no email, no phone. A contract handing
  over the entity would have shipped a person's CV to satisfy a foreign-key check.
- **`ApplicationDetail` split into a row plus a hydration step.** Two of its
  fields live elsewhere — `a.Resume.Label` and `ps.Skill.Name`/`.Category` — and
  both arrived through navigation properties, so the projection named no foreign
  `DbSet` and the boundary test passed while the query crossed. The public record
  is unchanged; `ApplicationDetailProjection.HydrateAsync` finishes it from the two
  contracts, and the three slices that share the projection each call it. At most
  two extra round trips, both skipped when they would be empty.
- **The skill filter in `ListApplications` is now a lookup then an EXISTS on the
  id.** Semantics did not move — it was already exact and case-insensitive, and the
  natural key is the same comparison, so the ILIKE escaping simply stopped being
  necessary. The branch that only exists because of the change is **a skill nobody
  has recorded**: it must mean "no results", not "no filter", and returning every
  application would look like a working page. Written as an impossible predicate
  rather than an early return, so `TotalCount` and the empty page come out of the
  same code path as every other filter.
- **`PostingContract.AddExtractedSkillsAsync` lost half its body.** The natural-key
  batch resolve went into the catalog; what stayed is the part that is genuinely
  its business — which `IsRequired` wins when a model names a skill twice — because
  that is a fact about a posting, not about a skill.
- **`RemoveSkillFromPosting` became case-insensitive**, the same fix
  `RemoveSkillFromResume` got in 13.2c and for the same expired reason.
- **`ListApplications` cards now sort their skill chips.** The SQL version did not,
  so a card's chips could reshuffle between requests; the names are resolved in
  memory now, so ordering them costs nothing and a list of cards should be stable.

**`ISkillCatalog` did not grow.** The pattern filter looked like a fourth verb and
was not: the filter was always an exact case-insensitive match, which is what
`FindByNameAsync` already does. Three verbs, unchanged.

#### What landed in 13.2e

The module with the most crossings — five tables across two owners, plus two
navigation traversals — and the only one that was a pure *reader*.

- **Six reads became six contract calls**, and Ats now names one table.
  `IApplicationContract.GetRefAsync`, `IPostingContract.GetSkillsAsync` /
  `GetRequirementsAsync`, `IResumeContract.GetContentAsync` / `GetSkillIdsAsync`,
  `ISkillCatalog.GetAsync`. `CheckAtsHandler`'s context field went from
  `AppDbContext` to `IAtsDbContext`, which holds exactly `AtsResults`.
- **`IPostingContract`'s two-method cap was lifted and its reasoning rewritten in
  place**, along with `AtsModule.cs`'s "this is why it stays at two" paragraph.
  Both argued from decision 17 — cross-module *reads* are ordinary, so the only
  methods a contract needs are its writes — and both were correct under it. Phase 13
  reverses decision 17, so the number stopped bounding anything. The replacement is
  the test `ISkillCatalog` already carried: **does a method name a fact about a
  posting, or a question the caller has about its own feature?** "Which of this ad's
  skills is my CV missing" is the second kind, and it stayed in Ats — which is why
  the skill gap did not become a fifth method on `IPostingContract`.
- **`IApplicationContract.GetPostingIdAsync` widened to `GetRefAsync`**, returning
  `ApplicationRef(PostingId, ResumeId)`. Ai wants the posting; Ats wants the posting
  *and* the résumé the user actually sent. Same row, same primary-key lookup, so a
  second method would have been one method per caller's question — the shape
  `IJobApplicationRepository` died of, and the one `IResumeContract.GetAsync` had
  already refused. Two nullable ids are not the over-fetch A1 is about; a whole job
  ad would have been.
- **`IResumeContract` grew from one method to three, and the "a third method would
  be worth stopping over" comment was the thing that had to be rewritten.** That
  comment counted METHODS when what it meant was that a contract must not grow one
  method per caller's question. `ResumeContent` is a second DTO beside `ResumeRef`
  rather than a widening of it, and the split is the point: Applications asks whether
  an id exists and still gets two fields, while Ats — whose entire feature is reading
  the CV — gets the text. **Nobody gets `SourceText` by accident.** Recorded with it:
  at 13.3 this DTO becomes a network payload, which is a real change in exposure and
  the security audit's business at that point.
- **The `SourceFormat` enum got a Contracts-side copy**, `ResumeSourceFormat`, with
  an explicit switch in `ResumeContract` — the same call `PostingRequirementKind`
  made in 13.2c, for the same reason: Contracts may reference no Jobkeep assembly, so
  it cannot name the entity enum, and a cast would keep compiling after someone
  reordered either list.

##### The skill gap left SQL, which breaks a standing rule on purpose

It was one query: `posting_skills` with a correlated `EXISTS` over `resume_skills`
and `skills` joined in for the name. Three tables, none owned by Ats. It is now two
contract calls returning ids, a `HashSet` lookup, and a third call to resolve names.

That breaks CLAUDE.md's **"aggregate in SQL, not in memory"**, and the justification
has to be exact rather than a shrug. The rule exists to stop a *table* being loaded
so C# can count it — an unbounded scan standing in for a `GROUP BY`. Neither set here
is unbounded: an ad lists tens of skills and a CV lists tens of skills, both capped by
what a human wrote. Nothing is aggregated; a `HashSet` lookup replaces an `EXISTS`.
And the alternative is not "keep the fast version", it is "keep a join that will not
exist" — trading a real future rewrite for microseconds.

Three consequences, written down rather than discovered later:

- **The reads are no longer one snapshot.** A concurrent edit between calls can
  produce a check that judged a state no single moment had. Accepted: an ATS result
  is already a stored judgement about a moment, which is `GetAtsResult.cs`'s whole
  argument for storing it.
- **A posting skill whose catalog row is missing is dropped, not rendered blank.**
  Impossible today — the FK guarantees it — and a gap to report at 13.3. Same call
  `GetResume.cs` made in 13.2c.
- **The name sort moved out of the database collation**, so the comparer is now a
  decision this code makes. It is `OrdinalIgnoreCase`, matching `GetResume` and
  `ListApplications`, and a new test pins it with names that ordinal ordering would
  answer differently. That is the only test 13.2e added: everything else it changed
  was meant to be invisible, and the existing fifteen say whether it was.

##### The partial-write question does not arise here, and the ordering is why

13.2c's rule was that a contract which writes must report a **partial** write rather
than throw. Ats writes `ats_results` and nothing else, and every contract call in the
slice is a **read** that happens before the first row reaches the change tracker — so
a contract failure leaves nothing half-written and the previously stored result
untouched. `ISkillCatalog.FindOrCreateAsync`, the one contract method that saves, is
never called: **Ats does not invent skills, it resolves ids a link row already
guarantees exist.** The comment above the store block says to keep it that way, since
moving a call below that line would put a foreign `SaveChanges` between building the
row and committing it.

##### The allowlist emptied and was deleted, canary included

`ModulesStillOnAppDbContext` held one entry. Removing Ats emptied it, so the list, the
conditional in `No_module_takes_the_shared_context` that read it, and the canary
`The_shared_context_allowlist_still_names_real_work` all went — which is what the
list's own comment instructed. Suite 253 → 252 on the deletion, then back to **253**
with the sort test. Green.

##### What Ats cost that it did not before

Stated because it is the other side of the trade the old `AtsModule.cs` comment
described:

| | Before | After |
|---|---|---|
| Round trips per check | 3 queries | 6 calls |
| Skill gap | one `EXISTS` in Postgres | two reads + in-memory `Except` |
| Tables named | 6 | 1 |

In-process the round trips are negligible. At 13.3 they are six network hops for one
check, and that is when batching becomes a real question rather than a premature one.

##### Everything that did NOT change

No migration, no wire contract, no front-end file. `AtsCheckResponse` is byte-for-byte
what it was, both surfaces included, which is why fifteen existing tests passed
unedited. `ResumeSourceFormat` is never serialized — it exists only between two
in-process modules.

- ~~Dropping the Documents-to-Applications project reference at 13.2c~~ — done. The
  `AllowedEdges` entry and the canary went with it, as designed.

### 13.3 — the physical split

Six contexts, five schemas, five migration histories, **six** FKs dropped, `Skills`
promoted to its own module, `Jobkeep.Infrastructure.Data` deleted.

(Six schemas was the plan; Analytics turned out to need none, because it owns no
tables. And the FK count was wrong here until 13.3c counted them out of
`pg_dump` — see the table below.)

**Split into three sub-steps**, decided with the user on 2026-09-01 after the scope
corrections below. **13.3a landed the same day.**

| | What | State |
|---|---|---|
| **13.3a** | the configuration seam: per-entity configs, `Jobkeep.Persistence` | **Done** |
| **13.3b** | entities into modules, six contexts, six schemas, migration reset | **Done 2026-09-02** |
| **13.3c** | integrity replacements, delete notifications, the diagrams | **Done 2026-09-02** |

#### Scope correction taken 2026-09-01, before any code moved

**Three things this section did not account for, all measured rather than guessed.**

1. **The suite is NOT free this time, and "What makes this cheap" #1 overstates it.**
   That claim — no test touches a handler, so a restructure is verified for free — is
   true of *behaviour*, and it held in 13.1, which moved 60 files with zero test edits.
   But **122 call sites across 15 test files** reach `AppDbContext` directly to
   *arrange* rows, touching all 13 `DbSet`s, and several mix modules in one block
   (`AtsTests.SeedResumeAsync` reads `db.Skills` while writing `db.Resumes`). Deleting
   `AppDbContext` breaks every one of them.
2. **`PostgresFixture` is hard-wired to `public`** — `SchemasToInclude = ["public"]`
   and a single `__EFMigrationsHistory` ignored. After the split there are six schemas
   and six history tables, so Respawn would truncate **nothing** and every test would
   leak state into the next. That fails as cross-test flakiness, not as a compile
   error, which is the expensive kind.
3. **Two model-wide conventions have nowhere to live.** The Phase 7 F11 defaults loop
   and the `UseXmin` helper run over the whole model, so all six contexts need them,
   and `SharedKernel` has zero package references on purpose.

**Decisions taken with the user:** drop the dev database (`compose down -v`, no
`pg_dump` carry-over); keep the 122 call sites compiling with per-module
`IEntityTypeConfiguration<T>` plus a **test-only** aggregate context; split into three.

#### What landed in 13.3c — 2026-09-02

**The integrity replacements, and the two routes that had to exist for two of them
to be reachable.** Suite **256 → 266**, build clean and warning-free, and
`has-pending-model-changes` clean on all five contexts — **13.3c adds no migration.
It is application code and diagrams only.** That is the check that it replaced the
dropped keys rather than quietly re-adding any of them.

**The mechanism: a publisher, not a fifth contract method.** Two of the six dropped
keys were CASCADEs, and a cascade is not a question — it is a consequence. The
obvious translation was `IAtsContract.DeleteForApplication`, called by
`DeleteApplication`. It works today and it is the wrong direction: it makes the
deleter hold the list of everyone who cares, so a sixth module means editing
Applications, and at service scale it means N synchronous calls on the delete path,
any of which can fail and none of which the caller can usefully retry. So
Applications *announces* and the interested modules subscribe.
`SharedKernel/DomainEvents.cs` was 3 types and ~30 lines and argued all of it.
**13.4 replaced those three types with `INotification`, `INotificationHandler<>`
and `IPublisher`, deleted the file, and did not touch either call site** — which is
the whole return on writing it a step early. The argument moved to
`Jobkeep.Contracts/Applications/ApplicationEvents.cs`, beside the events.

Three details worth keeping:

- ~~**There is no `IDomainEvent` marker interface**~~ — **RESOLVED at 13.4.** The
  reasoning held exactly as written: event types are module vocabulary, so they live
  in `Jobkeep.Contracts`, and Contracts may reference no other Jobkeep assembly
  (`Foundation_projects_depend_on_nothing_of_ours`), so a marker in SharedKernel
  would have forced that reference. What changed is where the marker comes from: a
  PACKAGE is not a Jobkeep assembly, so `Mediator.Abstractions` supplies
  `INotification` and the constraint that could not be written by hand now exists.
  `where TEvent : class` is gone with the publisher that needed it.
- **SharedKernel still has zero package references, and now has one file fewer.**
  The hand-rolled publisher resolved handlers through
  `System.IServiceProvider.GetService(typeof(IEnumerable<...>))` rather than the
  `GetServices<T>` extension, precisely to keep that promise. 13.4 deleted the file
  instead, so the constraint is kept by having nothing there rather than by working
  around it.
- **Publish AFTER the commit, and the ordering is the decision.** Publishing first
  would delete the ATS result of an application that then failed to delete: a live
  row loses a stored judgement, and re-earning it costs a model call the user waits
  three minutes for. Publishing after leaves, on failure, an orphan `ats_results` row
  that nothing can read. Two failure modes — *invisible orphan* and *destroyed work
  on a live row* — and this picks the first, which is the same call
  `ISkillCatalog.FindOrCreateAsync` already makes about its own save ordering. The
  honest gap: no outbox, so a crash between commit and publish loses the event.
  Phase 14's problem, because an outbox is only worth its cost once the subscriber
  is a separate process.

**Two new routes, and both are the phase forcing a gap into the open.**

- **`DELETE /postings/{id}`** — *nothing in the application had ever deleted a
  posting.* Postings are created implicitly by logging an application, so the
  `ai_analyses` cascade had been unreachable for the whole life of the table, and
  deleting your last application for an ad left the ad behind forever with its
  skills, requirements and analysis, reachable by nothing. Writing `OnPostingDeleted`
  without this route would have shipped a subscriber nothing could trigger, verified
  by a test that reached into the database — which is how a replacement gets believed
  rather than checked.
- **`DELETE /resumes/{id}`** — `DiscardImport` has been telling users *"Delete the
  resume or application it created instead"* since Phase 4.5, against an endpoint
  that did not exist. The application half was true; the résumé half was not.

Both refuse rather than cascade, and the refusals are *different in kind* from the
notifications, which is the shape worth carrying forward: **a CASCADE becomes an
announcement made after the fact; a RESTRICT becomes a question asked before it.**

**The cost, stated where it will be asked about.** A delete-side contract check is
strictly weaker than the RESTRICT it replaces, and `DeleteResume.cs` names it rather
than implying parity: a foreign key refuses inside the transaction that attempts the
delete, while two counts and a delete are three statements with gaps between them, so
an application created against a résumé after the count survives, pointing at a row
that no longer exists. **That is a time-of-check-to-time-of-use race and it is the
actual price of moving integrity out of the database.** Accepted, because the window
is microseconds on a single-user local app, the residue is the case the read path
already handles (`ApplicationDetail` leaves `ResumeLabel` null and says so;
`GetAtsResult` does the same), and the real answer at service scale is a saga or a
soft delete — which is Phase 8, and rewrites this path anyway.

`DELETE /postings/{id}` is the contrast that makes the point: **its** refusal is
still enforced by a live foreign key, because both tables are in Applications' own
schema. The check there buys a 400 with a count in it instead of an unhandled
`DbUpdateException` surfacing as a 500 — a status code, not an invariant.

**Tests: 256 → 266.** The two inverted tests in `DeleteBehaviourTests` are flipped
back, and the posting one now deletes **through the route** rather than through the
test context — a context delete raises no event, so the old arrangement would still
leave the analysis behind and the test would be asserting the absence of a route
rather than the presence of a replacement. `Persistence/IntegrityReplacementTests.cs`
is new (10 tests) and covers the machinery the row counts cannot see: the 400 that
would otherwise be a 500, the counts inside the refusal messages, the refusal lifting
once the blocking row is gone, and the GraphQL half of both new mutations.

**Two gaps the 13.3b handoff flagged as "wanting reporting" are instead CLOSED, and
that is the cheaper answer.** A skill id with no catalog row and an
`ats_results.ResumeId` pointing at a deleted résumé were both reachable after 13.3b.
Nothing in the application deletes a skill — there is no such route — and a résumé
delete is now refused while either table points at one, so both states are
unreachable through the app. The existing tolerant handling (drop the skill, leave
the label null) stays as defence in depth against the TOCTOU race and against direct
database edits. Building a warning field for a state no request can produce would
have been wire contract nobody could exercise.

**Both diagrams redrawn**, from `pg_dump --schema-only` against the migrated
database. `schema-erd.svg` gained a third edge style — dotted grey for a
relationship Postgres no longer knows about — which is the single most useful thing
the picture now says. `architecture.svg` was redrawn for the ten-project shape: six
modules with their own contexts and schemas, Contracts as the only path between them,
and the event lane. **`src/` is TEN projects, not the nine `CLAUDE.md` claimed** —
six modules plus SharedKernel, Contracts, Persistence and Api; the Skills promotion
was never added to that count.

**What 13.3c did NOT do:** no migration, no schema change, no `web/` change. The
front-end suite is untouched at 49.

#### What landed in 13.3b — 2026-09-02

**The physical split. Suite 254 -> 256, build clean, five schemas in Postgres, the
compose stack up from a dropped volume.**

- **Ten projects became nine.** `Jobkeep.Infrastructure.Data` is deleted, as its own
  csproj said it would be. Its thirteen entities went to the five modules that own
  their tables, its sixteen configurations went with them, its six `I<X>DbContext`
  interfaces were replaced by six real contexts, and its five migrations were replaced
  by five initial ones.
- **Five schemas, five migration histories.** `applications`, `skills`, `documents`,
  `ai`, `ats`. Analytics has a context and no schema, because it owns no tables — it
  reads three views that live in `applications`. `Program.cs` migrates five contexts
  and not the sixth, which turns "Analytics owns nothing" from a comment into a fact
  about the deployment.
- **Six foreign keys dropped** — *this bullet said five, and five was the number this
  doc had carried since 13.3a.* 13.3c counted them out of `pg_dump --schema-only`
  (13 before, 7 after) and found `posting_skills.SkillId` in the list nobody had
  written down. Verified end to end
  afterwards: create an application, add a skill (Applications and Skills, two
  contexts, two transactions), read `/stats/skill-demand` (Analytics reads a published
  view in `applications`, then resolves ids through `ISkillCatalog` in `skills`).
- **The dev database was dropped** (`compose down -v`), as agreed. No `pg_dump`
  carry-over.

**Four design questions the plan did not answer, resolved before any code moved:**

1. **Two enums could not follow their entity.** `ApplicationStatus` and `SkillSource`
   each appear in *two* modules' response DTOs, so both reach the HotChocolate schema.
   The copy-with-a-mapping-switch pattern that `PostingRequirementKind` and
   `ResumeSourceFormat` use would put two CLR enums of one name in one GraphQL schema,
   which is a schema-**build** failure — every request 500s and nothing in the C# build
   says why. Both moved to `Jobkeep.Contracts` instead, as genuinely shared vocabulary.
   `src/Jobkeep.Contracts/Shared/SharedEnums.cs` carries the argument.
2. **Entities keep `namespace Jobkeep.Models`.** This is a move, not a rename: the
   boundary is the project reference graph, not the namespace, and 13.6 renames
   everything in one pass. Renaming now would do 13.6's job early and badly, and bury
   the real change in churn.
3. **`RequirementKind` crossed, and had an existing answer.** Documents carried
   Applications' entity enum in its draft DTOs and mapped it at commit. The drafts are
   typed on `PostingRequirementKind` now and the mapping switch is deleted — the better
   shape, which the split forced. Member names are identical, so the REST payload and
   the stored `DraftJson` are byte-identical; only the GraphQL *type name* for a draft
   requirement changed.
4. **The three published views split three ways.** 13.3a's comment said the mapping
   "belongs to APPLICATIONS, because Applications publishes it", and that could not
   survive: `AnalyticsDbContext` reads them, can only apply its own assembly's
   configurations, and may not reference Applications. The rule that replaced it is
   publisher-owns-the-definition, consumer-owns-the-read — payload shape in Contracts,
   SQL in Applications' migration, `HasNoKey().ToView(..., "applications")` in
   Analytics. Analytics naming that schema is an **address**, and at extraction it
   becomes a URL.

**Five things found by doing it that the plan and both handoffs missed:**

- **Dropping a foreign key silently drops its INDEX, and in two places that index was
  load-bearing.** EF indexes an FK column automatically. `posting_skills` and
  `resume_skills` both have a composite PK leading with the *other* column, so
  `SkillId` lost its only index — and `ListApplications`' skill filter is exactly a
  lookup by `SkillId`. Both restated explicitly.
- **Dropping a one-to-one FK silently drops the UNIQUE index that made it "one".**
  `ai_analyses.PostingId` and `ats_results.ApplicationId` were unique only as a side
  effect of `HasForeignKey<T>`. `AnalyzePosting` and `CheckAts` both update-or-insert on
  the assumption that a second row cannot exist. Both indexes restated.
- **Every table-owning module needs the Npgsql PROVIDER, not just Applications.** The
  expectation was that only Applications would (`EF.Functions.ILike`). But a migration
  is provider-specific: the generated snapshot and designer files name
  `NpgsqlValueGenerationStrategy` directly and do not compile without it. That is a real
  statement about the boundary rather than a build detail — a module that can be lifted
  out creates its own schema, and creating a schema means knowing which database it is.
- **The suite's raw SQL was a bigger surface than the navigation properties.** The
  handoff budgeted for the assertions traversing the five cut navigations, and those
  were right. It missed that **eight tests use raw SQL naming unqualified tables**,
  which resolve through `search_path` to `public` — the one schema that no longer holds
  anything. Including `'posting_skills'::regclass`, which fails the same way and looks
  nothing like a table name.
- **`tests/Jobkeep.Tests.csproj` never referenced `Jobkeep.Modules.Skills`.** It had
  been working since 13.2 only because `Jobkeep.Api` pulled it in transitively —
  precisely the confusing failure that ItemGroup's own comment warns about. Found by
  `TestDbContext` needing to name all six module assemblies.

**The two scope corrections held.** The test-only aggregate context worked as designed:
**all 122 arrange call sites compile unchanged**, because every configuration names its
schema in `ToTable`'s second argument rather than through `HasDefaultSchema`, so
applying all six assemblies' configurations to one model reproduces the six real
contexts exactly. And `PostgresFixture`'s Respawn config was fixed in the same step —
`SchemasToInclude` is the five schemas, `TablesToIgnore` is five *schema-qualified*
history tables — with `ResetIsolationTests` asserting that a reset actually empties a
seeded table in every schema, because the failure mode there is silence rather than an
error.

**Two delete tests were INVERTED rather than deleted.** `DeleteBehaviourTests` now
asserts the orphans the split creates: an `ats_result` outliving its application, an
`ai_analysis` outliving its posting. That is the same thing this suite did with the
case-sensitive skill dedup — a defect written down as a passing test is visible, and it
breaks loudly on the change that fixes it. 13.3c flips both back.

**One architecture test was rewritten rather than moved.**
`No_module_takes_the_shared_context` looked for a constructor parameter typed
`AppDbContext`. Deleting that type would have left it passing forever while proving
nothing. It is now `No_module_takes_a_context_it_does_not_own`: a module may take a
`DbContext` declared in its own assembly and no other, which catches another module's
context, a future re-introduced shared one, and `TestDbContext` itself.

**What 13.3c inherits, concretely.** Four replacements for the dropped keys:
delete notifications for `ai_analyses.PostingId` and `ats_results.ApplicationId`, and
the delete-side check for `job_applications.ResumeId` and `ats_results.ResumeId` (the
write-side check already exists, through `IResumeContract.GetAsync` — a check at write
cannot stop a row disappearing afterwards, which is the two thirds of RESTRICT that a
contract does not replace). Plus both diagrams, which are now wrong.

#### What landed in 13.3a

The safe half, done alone so that 13.3b is a file move rather than a rewrite. **No
migration, no schema change, no existing test edited.**

- **`AppDbContext.OnModelCreating` went from 400 lines to three**, split into 16
  `IEntityTypeConfiguration<T>` classes in `Configurations/` — 13 entities plus the
  three published views. Every comment moved verbatim; they are the argument for each
  mapping decision and that is the part not recoverable from the code.
- **The split is not tidying, and that is the whole justification.** Each configuration
  is now a self-contained statement about ONE table, so 13.3b moves it into the owning
  module by moving a file. Doing it the other way — splitting a 400-line method while
  also changing the schema — is the mistake 13.1 already paid for once, and its
  deviation note records it.
- **`src/Jobkeep.Persistence`**, a new foundation project: the two model-wide
  conventions plus `AuditSaveChangesInterceptor`, which depends on `IAuditable` and
  nothing else of ours. It holds **no entities** and must not start to. A new
  architecture test, `Jobkeep_Persistence_references_only_SharedKernel`, pins that —
  a weaker rule than `Foundation_projects_depend_on_nothing_of_ours`, because this one
  legitimately needs `IAuditable`, but it is the rule that stops an upstream-of-
  everything project becoming the "Common" assembly nobody can split.
- **`ApplyDatabaseDefaults` must be called LAST** and its comment now says so. It reads
  the finished model, so an entity configured after it silently misses the defaults.
  `UseXmin` stayed opt-in per entity rather than becoming a sweep: the three tables
  that want it are the three a user edits twice, and a link row has no lost update
  to lose.

**The plan's step 4 was wrong and the fallback it named was taken.** It proposed
putting each table's target schema into `ToTable` at 13.3a, ahead of the move. That
cannot work: the tables are in `public`, so EF would immediately generate SQL against
`documents.resumes` and every test would fail. The schema is a second argument to
`ToTable` in 13.3b, one line per configuration. `dotnet ef migrations
has-pending-model-changes` reports **no changes since the last migration**, which is
the check that 13.3a is schema-identical rather than merely believed to be.

Suite **253 → 254** (the new architecture test), build clean, `has-pending-model-changes`
clean.

**One trap re-paid:** MSB4025, an XML comment cannot contain `--`. It is in
`docs/tool-usage.md` from 13.1 and cost a build anyway.

- Each context: `HasDefaultSchema("<module>")` +
  `MigrationsHistoryTable("__EFMigrationsHistory", "<module>")`, same connection
  string.
- **Migration reset.** The four existing migrations describe one schema and cannot
  be split. Nothing is deployed (Phase 10 is parked), so squash to one initial
  migration per module. **This drops the local dev database** — if `pgdata` holds
  real applications worth keeping, say so *before* this step and it gets a
  `pg_dump` + `ALTER TABLE … SET SCHEMA` carry-over instead of `down -v`.
- ~~Redraw `docs/diagrams/schema-erd.svg`~~ **— done at 13.3c, and it is the LAST
  redraw before 1.0.** Diagrams are frozen from 2026-09-02 (CLAUDE.md, "Frozen until
  1.0"): 13.4-13.6 must not redraw either SVG, and should note the debt in a line
  here instead. Kept for the method, which still applies whenever they are next
  drawn — derive
  from `pg_dump --schema-only` against the migrated database (the Phase 7 note: an
  idempotent migrations script is a sequence, and reading final state out of it is
  guesswork).

### 13.4 — dispatch

33 requests → `IRequest<T>`, 53 call sites (27 REST + 26 GraphQL) → `Send(...)`, and
the cross-module writes become `INotification` — the seam that later becomes a queue.

**Decided 2026-09-03, with the user: `martinothamar/Mediator`.** The finding this
step was told to confirm and record:

- **MediatR went commercial in mid-2025**, under Lucky Penny Software. The Community
  edition is free below **$5M USD annual gross revenue**, with a second condition —
  the entity must never have taken more than **$10M** in outside capital. A personal
  portfolio sits far under both. **But the free tier still requires registering a
  license key**, which is the part that decided it: a portfolio project would then
  depend on someone else's revenue band and a key registration, and "why does your
  side project need a license key" is a worse interview answer than the alternative.
- **`martinothamar/Mediator` is MIT**, no key, no threshold. It is **source-generated**,
  so `Send(...)` compiles to a direct call rather than a reflection lookup — which also
  keeps the Lambda trimming/AOT option open at Phase 10. The API is near-identical, so
  the thing being demonstrated (a mediator, and why one) is unchanged.

The third option — hand-rolling the sender the way 13.3c hand-rolled the publisher —
was on the table and refused: ~20 lines is cheap, but this seam is the one a reader
of the repo is meant to recognise, and a bespoke `ISender` makes them read it instead.

**DONE 2026-09-03**, on branch `phase-13/dispatch`. Suite 266 → 268, no migration, no
`web/` change, `has-pending-model-changes` clean on all five contexts. Six deviations
from the plan above, all of them worth keeping:

- **The counts were wrong, and the shape was wronger.** 29 request handlers, not 33;
  57 call sites, not 53; plus 2 notification handlers. But the real gap is that the
  plan said "33 requests → `IRequest<T>`", implying a rename. **There were no request
  objects.** Handlers took scalars — `(Guid id, XRequest request, CancellationToken)`,
  and `ImportDocument` took six parameters — so every slice needed a request record
  *created*. That, not the `Send(...)` sweep, was the cost of this step.
- **The wire DTOs are wrapped, not marked.** `UpdateApplicationRequest` and friends
  stay plain records and the command wraps them (`new UpdateApplication(id, request)`).
  Making them `IRequest<T>` directly would have been fewer types and would have
  stamped the mediator's marker onto the public API contract — the same mistake as
  returning an EF entity, one layer up.
- **Naming: the record takes the use case's name, the handler keeps `...Handler`.**
  So a call site reads `Send(new GetApplication(id), ct)`. The `Handle` parameter is
  called `message` in all 29, uniformly, so that the five slices whose wire DTO is
  already named `request` do not need a different shape from the other 24.
- **GraphQL needed namespace aliases.** Every resolver is named for the field it
  publishes, 13.4 gave the request record the same name, and inside `Query`/`Mutation`
  a bare `new CheckAts(...)` binds to the METHOD and does not compile. Five aliases
  (`Apps`, `Stats`, `Ai`, `Docs`, `Ats`) at the top of each file; the alternatives
  were fully-qualified names on 28 fields, or renaming resolvers and changing the
  published schema to suit a C# lookup rule.
- **`Mediator.Abstractions` is pinned in `Jobkeep.Contracts`, not in the six module
  csprojs**, so one reference reaches all of them and there is one version to move
  instead of seven. That makes `Jobkeep.Contracts.csproj`'s "also has no package
  references, on purpose" false, and the comment was rewritten rather than left: the
  rule it protects (anything here must survive a network hop) is satisfied, because a
  record that implements `INotification` serialises exactly as it did before.
  `Jobkeep.SharedKernel` still has zero, which is the promise that was load-bearing.
- **`ApplicationContract` stopped naming handlers.** It injected
  `CreateApplicationHandler` and `AddRequirementToPostingHandler` directly since
  13.2c; it takes `ISender` now. Same delegation, but a contract whose whole purpose
  is to survive the handler being renamed or split should not name it.

**One thing a mediator costs, and it is bought back rather than assumed.**
`ISender.Send` takes `IRequest<T>`, so the compiler checks the response type and
nothing else: a request whose handler is missing, misnamed, or in a project the
composition root does not reference compiles at every call site and throws
`MissingMessageHandlerException` at runtime, on whichever route nobody clicked. That
is exactly the coupling the old `XHandler handler` parameter made the compiler
enforce. `tests/Jobkeep.Tests/Architecture/DispatchTests.cs` is the two tests that
replace it — every `IRequest<>` has exactly one handler, every `INotification` has at
least one — by reflection over the same assembly graph the source generator walks. No
container, no database.

**Diagrams deliberately not redrawn** — nothing in the schema moved, and they are
frozen until 1.0 ships on master either way.

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
currently carry a pointer to this doc instead); **`architecture.svg` NOT redrawn —
diagrams are frozen until 1.0 ships on master, so 13.6 records what moved and leaves
the picture stale**; the
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
