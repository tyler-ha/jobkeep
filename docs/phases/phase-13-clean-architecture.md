# Phase 13 — Clean Architecture and controllers

**Status: Proposed — an estimate, not a commitment.** Nothing is scheduled and
nothing is built. Written 2026-09-01 in answer to *"how much effort to put in if I
changed architecture to become more industry advised."* The number is at the
bottom; the reasoning is what makes it defensible.

**This reverses decisions 5 and 7 in `architecture.md`.** That is the point, not a
side effect — see "What this costs that is not hours".

---

## Decisions taken — confirmed 2026-09-01, do not reopen

| Question | Decision | Why, in one line |
|---|---|---|
| How do the 33 handlers reach the database? | **`IApplicationDbContext`** — one interface over the 13 `DbSet`s, implemented by `AppDbContext` in Infrastructure | Per-aggregate repositories recreate the interface decision 5 killed for growing past 20 methods, and this time there would be several. The leak is real and is the *defensible* answer, not the pure one. |
| How is a request dispatched? | **MediatR** | It is what makes the shape read as Clean Architecture rather than as four projects. **Check the licence tier before adding it** — MediatR went commercial with a free band below a revenue threshold; a personal portfolio sits under it, but confirm rather than assume, and record the finding in this doc. |
| Does validation move to FluentValidation? | **No — not in this phase** | Validation in the handler is what stops REST and GraphQL enforcing different rules (A4). Moving it mid-migration changes error shapes and routes the parity guarantee through a new mechanism, for no gain a reader notices. |
| When does this start? | **After Phase 6.5 group 4 (paste text) ships** | Group 4 is the last work queued against the current shape and it is the only part of 6.5 that touches `src/`. Migrating first means writing it twice or landing it mid-restructure. |

The MediatR choice has a consequence worth stating up front, because it is the
phase's one genuine architectural gain rather than a relocation: **decision 15's
accepted cost disappears.** Documents depends on Applications *at compile time*
today, because `CommitImport` calls `CreateApplicationHandler` directly. Under a
dispatcher that becomes a message, and the coupling goes.

---

## Why this would be done

Not because the current architecture is wrong. `architecture.md` argues the
vertical-slice case and the argument still holds for a 13-table single-user
tracker. The reason is **audience**: Clean Architecture with attribute-routed
controllers is the dominant convention in Australian enterprise .NET, and a
portfolio has to be legible to a screening reader as well as correct.

The strongest version of this is not "migrate and hide the past". It is **migrate
on a branch and keep both shapes in git**, so the interview answer becomes:

> "I built it vertical-slice, migrated it to Clean Architecture, and I can tell
> you what each one cost and what it bought."

That is a better artefact than either architecture on its own, and it is the only
framing under which the cost below is worth paying.

---

## What makes this cheap (three things, all pre-existing)

Measured on 2026-09-01, not estimated:

1. **No test touches a handler.** All 239 tests reach the code through **HTTP or
   GraphQL** — `grep` for `new *Handler(` and `GetRequiredService<*Handler>` in
   `tests/` returns nothing. Seventeen files use an HTTP client. So the suite is a
   *contract test over the wire*, and a total internal restructure that preserves
   URLs and payloads is verified by it for free. This is unusual and it is the
   single biggest cost reducer here.
2. **The models are already clean.** `src/Models/*.cs` imports exactly one
   namespace: `System.Text.Json.Serialization`. No EF anywhere, because the schema
   lives in Fluent API in `Data/AppDbContext.cs`. **`Jobkeep.Domain` is close to a
   file move.**
3. **The front end only knows URLs.** `web/src/lib/api.ts` is plain `fetch`
   against paths. Preserve the routes and the front end costs **zero**, including
   its 49 tests.

## What makes it expensive

- **33 handlers** take `AppDbContext` directly — the thing decision 5 deliberately
  created. Every one is touched.
- **Two surfaces, not one.** 27 REST routes *and* 26 GraphQL fields (15 mutations,
  11 queries) call the same handlers. Every dispatch change is paid twice.
- **7,128 lines of C#** in `src/` excluding migrations, 5,219 of it in `Modules/`.
- **The decision record has to be rewritten**, not appended to.

---

## Target shape

```
src/
├── Jobkeep.Domain/          13 entities, enums, IAuditable,
│                            ApplicationStatusTransitions. No framework refs.
├── Jobkeep.Application/     33 handlers, IApplicationDbContext, SliceResult,
│                            IDocumentTextExtractor / IDocumentStructurer /
│                            IPostingContract abstractions
├── Jobkeep.Infrastructure/  AppDbContext, AuditSaveChangesInterceptor,
│                            Migrations, ModelClient (Ollama), the PdfPig and
│                            OpenXml extractors, DocumentStructurer
└── Jobkeep.Api/             ~6 controllers, GraphQL, Program.cs, appsettings
```

Only **9 files** under `Modules/` and `Shared/` touch a concrete infrastructure
package (PdfPig, OpenXml, OllamaSharp), and the two that matter —
`DocumentTextExtractor.cs`, `DocumentStructurer.cs` — **already sit behind
interfaces**. So the Application/Infrastructure line is already drawn; this phase
makes it a project boundary rather than a naming convention.

`AnalyzePosting.cs` and `CheckAts.cs` inject `IChatClient`, which is a
`Microsoft.Extensions.AI` abstraction, so they stay in Application unchanged.

---

## The work, in order

Each step ends with **239 backend tests green**, which is what makes this
splittable across sessions rather than one long unrunnable refactor.

### W1 — the project split (highest risk, do it first and alone)

Four `.csproj`, one `.slnx`, and a file move. No logic changes.

- `Models/` → Domain. Near-free (see above). One judgement call: the
  `[JsonConverter]` attributes are a serialisation concern in Domain. Leave them;
  moving them costs DTO churn for a purity point nobody will ask about.
- `Modules/` → Application, except the four infrastructure files.
- `Data/`, `Migrations/`, `Shared/ModelClient.cs`, the two extractors →
  Infrastructure.
- `Program.cs`, `GraphQL/`, `Shared/ResultHttpExtensions.cs`, `appsettings*` → Api.

**Watch for:** `Modules/Documents/CommitImport.cs` calls Applications'
`CreateApplicationHandler` (decision 15). Both land in Application, so no circular
reference — but confirm before moving, because a cycle here is a rewrite.

**Also breaks, and must be fixed in the same step:**
- `src/Dockerfile` — build context is `./src` on the stated grounds that *"the
  csproj references nothing above it."* Still true with four projects under `src/`,
  but the `COPY`/`dotnet publish` lines all name one project.
- `.github/workflows/ci.yml` builds `src/Jobkeep.slnx` — add the four projects.
- `dotnet ef` now needs `--project Jobkeep.Infrastructure --startup-project
  Jobkeep.Api`. Document it in CLAUDE.md's Commands section or the next migration
  fails for a reason that looks like a tooling bug.
- `tests/Jobkeep.Tests.csproj` names `Microsoft.EntityFrameworkCore` and
  `.Relational` explicitly to dodge CS1705 (the Npgsql range vs EF Design pin).
  **Read the csproj comment before touching it** — it repoints at Infrastructure,
  it does not get deleted.
- `JobkeepAppFactory` uses `WebApplicationFactory<Program>` → now Api's `Program`.

### W2 — `IApplicationDbContext`

One interface in Application exposing the 13 `DbSet<T>` plus `SaveChangesAsync`;
`AppDbContext` in Infrastructure implements it. 33 constructor changes, mechanical.

**The one real wart:** `CommitImport.cs:304` calls
`_db.Database.BeginTransactionAsync(ct)`, which is not on a `DbSet` interface.
Options, in order of preference: expose `BeginTransactionAsync` on the interface
(honest, slightly leaky, what most templates do); or leave that one handler on the
concrete context and say why in a comment.

Purists call `IApplicationDbContext` a leaky abstraction. It is. It is also what
makes this migration tractable instead of enormous — the alternative is
per-aggregate repositories, which recreates exactly the interface decision 5 killed
for growing past 20 methods. **Choosing the pragmatic option and being able to
explain why is the better interview answer than choosing the pure one**, and that
sentence is the deliverable as much as the code is.

### W3 — dispatch, with MediatR

33 requests become `IRequest<T>`, 33 handlers become `IRequestHandler<,>`, and 53
call sites (27 REST + 26 GraphQL) become `_mediator.Send(...)`.

**One genuine architectural win here, worth stating out loud:** decision 15
accepted a real cost — Documents depends on Applications *at compile time* because
`CommitImport` calls `CreateApplicationHandler` directly. Under a dispatcher that
becomes a message, and the compile-time coupling goes away. This phase does not
just relocate code; it removes a documented cost.

### W4 — controllers

27 routes → ~6 `[ApiController]` classes, same URLs.

Four known traps:
- **`[AsParameters] ApplicationQuery`** becomes `[FromQuery]` on the model.
- **The multipart route.** `Modules/Documents/DocumentsModule.cs` binds `IFormFile`
  **without** `[FromForm]`, because Swashbuckle 10 *throws* on an action carrying
  both and 500s the whole `swagger.json` for every endpoint. **Under
  `[ApiController]` the binding rules invert.** Re-solve it deliberately;
  `SwaggerDocumentTests.cs` is what catches it either way.
- **`[ApiController]` auto-400s on model state** and emits its own `ProblemDetails`.
  That changes error *bodies*, which `Rest/` and `Parity/` tests assert on. Expect
  a handful of test edits — and decide whether the auto-400 or the slice's
  validation is authoritative, because two answers is finding A4 coming back.
- **Antiforgery.** The form route disables it explicitly; the controller needs the
  equivalent.

### W5 — FluentValidation: **recommend skipping in this phase**

Validation lives in the handler today, which is what makes REST and GraphQL
enforce the same rule (A4). Moving it to a pipeline behaviour changes error shapes
and puts the parity guarantee through a new mechanism, for no gain a reader will
notice. Add it later if wanted; do not bundle it into a migration whose failures
must stay diagnosable.

### W6 — docs and diagrams

Not optional and not small. `architecture.md` decisions **5, 7, 12, 15 and 17** all
describe the architecture this phase replaces, and section 2 ("Target: modular
monolith with vertical slices") is the document's spine. `CLAUDE.md`'s "Where new
code goes" and "Migration state" become wrong in full. Redraw `architecture.svg`
with the `schema-diagram` skill (the ERD is untouched — no schema change).

**Do not delete the old reasoning.** Supersede it in place, the way decision 17
supersedes rule 2. The reversal *is* the artefact.

---

## The estimate

| Step | Files touched | Session |
|---|---|---|
| W1 project split + build/CI/Docker/test-host fixes | ~60 moved, 6 config | 1 |
| W2 `IApplicationDbContext` | 34 | 1–2 |
| W3 dispatch | 33 handlers + 53 call sites | 2–3 |
| W4 controllers | 27 routes → ~6 classes, ~4 test files | 3–4 |
| W6 docs + `architecture.svg` | 6 docs | 4–5 |

**4–5 sessions**, each ending runnable. In tokens, the honest comparison is Phase
2.3 (the repository retirement, 260 turns / **52.4M**) — this is broader but more
mechanical, and better protected by tests. Budget **40–70M**, and note the ledger's
own warning: a figure logged mid-phase has understated the total four phases
running.

**Front end: zero.** Back end features: zero — this changes no behaviour, adds no
endpoint, and is invisible to every one of the 288 tests except by staying green.

## What this costs that is not hours

- **Phase 6.5 group 4 (paste text) is unbuilt and touches `src/`.** Settled above:
  it ships first. Anything else queued against the old shape gets the same
  treatment — finish it or drop it, do not carry it across the migration.
- **Phases 7–12 assume the current layout** in their plans, most visibly Phase 11
  (auth touches every slice) and Phase 9 (projections).
- **The `agent-log`/`tool-usage` file-and-line findings go stale in one commit.**
  They carry dates for this reason, but a migration invalidates most of them at once.
- **Decisions 5 and 7 get reversed.** Decision 5 is the repository retirement,
  argued at length and carried out over two phases; decision 7 is the controller
  retirement, still marked *Proposed*. Reversing a decision you argued well is
  fine — reversing it without saying why is what makes a record worthless.

## The alternative that was not chosen

Keeping the architecture and writing the comparison instead — decision 7 closed as
Accepted, plus a small standalone Clean Architecture sample so the answer to *"do
you know CA?"* is a link rather than a claim. ~1 session. Recorded here because the
estimate above is only worth paying if the migration itself is the story.
