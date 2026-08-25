# Phase 2.1 — Complete the write surface

**Status: Done** (2026-08-25). First phase built in the vertical-slice shape.

> **Architecture note (2026-08-25):** built as vertical slices under
> `src/Modules/Applications/`, per `docs/architecture.md`. The original Scope
> section of this doc told you to add four methods to `IJobApplicationRepository`;
> that contradicted the note at the top of the same doc and `architecture.md`
> decision 5. It has been rewritten below to match what was actually built —
> see "Deviations from the original plan".

## Goal

Finish the CRUD on the relational model so no table is a dead end. Two concrete
holes today:

1. `AddSkillToPosting` exists in GraphQL and the repository, but **REST can't
   reach it**.
2. The `job_requirements` table exists in the schema, but **nothing can create,
   list, or remove a requirement** — it's write-unreachable.

Filling these first means Phases 2.2–2.4 (querying, analytics, rules) have real
data to work against.

## Why this is Phase 2 work

The phase-2 justification for Postgres was a *rich, queryable relational model*.
A model with tables you can't populate isn't finished. This is completion, not
new scope — no new infra, no AI, still local/free.

## What was built

Four slices in `src/Modules/Applications/`, each holding its request, handler and
response in one file, each reached by **both** API surfaces:

| Slice | REST | GraphQL |
|---|---|---|
| `AddSkillToPosting.cs` | `POST /applications/{id}/skills` | `addSkillToPosting` |
| `RemoveSkillFromPosting.cs` | `DELETE /applications/{id}/skills/{skillName}` | `removeSkillFromPosting` |
| `AddRequirementToPosting.cs` | `POST /applications/{id}/requirements` | `addRequirementToPosting` |
| `RemoveRequirement.cs` | `DELETE /applications/{id}/requirements/{requirementId}` | `removeRequirement` |

Supporting pieces:

- `src/Shared/Result.cs` — `SliceResult<T>` (`Ok` / `NotFound` / `Invalid`). A
  handler reports an outcome without knowing which surface called it.
- `src/Shared/ResultHttpExtensions.cs` — the REST translation (404 / 400).
- `src/GraphQL/ResultExtensions.cs` — the GraphQL translation (a `GraphQLException`
  carrying `NOT_FOUND` / `INVALID_INPUT`).
- `src/Modules/Applications/ApplicationsModule.cs` — DI + route wiring, so
  `Program.cs` gains exactly two lines.

**No schema change**, therefore no migration and no diagram redraw. Every table
these slices touch already existed; Phase 2.1 only gave `job_requirements` and
`posting_skills` a write path.

## Deviations from the original plan

Recorded because they're the interesting part, not because they were accidents.

1. **No new repository methods.** The plan called for
   `RemoveSkillFromPostingAsync`, `AddRequirementToPostingAsync` and
   `RemoveRequirementAsync` on `IJobApplicationRepository`. All four use cases
   became slices instead (`architecture.md` decision 5). The interface is now
   **smaller** than when the phase started.
2. **`AddSkillToPostingAsync` was removed from the interface**, not just left
   alone. It was the use-case-on-a-CRUD-interface that `architecture.md` A3 names,
   and this phase had to touch it anyway — so it moved into the slice rather than
   being duplicated beside it. A5 (the stale "Phase 2 swaps in DynamoDB" comment)
   was corrected in the same edit.
3. **`InMemoryJobApplicationRepository` was deleted.** The plan said to implement
   the new methods on it too. Decision 5 retired it; it was unregistered dead code
   (only the Postgres implementation is in DI), and the no-DB dev story is Postgres
   in Docker, which is what the README already tells you to run.
4. **The slices return response DTOs, not the aggregate.** The old
   `AddSkillToPostingAsync` re-read the entire object graph — company, skills,
   requirements, AI analysis, ATS result — to report that one join row had been
   added. The slices return just the thing that changed. That's a first bite out of
   A1/A2, taken here because doing it the old way would have meant writing the
   over-fetch again.
   - **This is a breaking GraphQL schema change**: `addSkillToPosting` now returns
     `PostingSkillResponse` rather than `JobApplication`, and takes an input object
     instead of four loose arguments. Acceptable — there are no external clients.
5. **Validation moved into the handlers** (A4). Blank `skillName` / `text` is
   rejected once, so the GraphQL mutation path can no longer skip a check the REST
   path makes.
6. **`SliceResult<T>`, not `Result<T>`.** HotChocolate's DataLoader library
   (GreenDonut) publishes its own `Result<T>` via a global using; the bare name
   collides in every slice file. Renaming ours was cheaper than qualifying it
   everywhere.
7. **The requirement delete is scoped to the parent.** `RemoveRequirement` matches
   on requirement id **and** the addressed application's posting id, so a caller
   can't delete a requirement off a posting they didn't name. Free today (single
   user); the correct habit before owner scoping lands
   (`security-and-data-audit.md` F1).

## Out of scope

- AI-extracted skills/requirements (that's Phase 4 — `SkillSource.AiExtracted`
  and `AiAnalysis` stay untouched here). New links are written as
  `SkillSource.Parsed`.
- Editing a requirement in place — add/remove is enough at personal volume;
  revisit only if it bites.
- Bounding the `job_requirements.Text` column, soft delete, and audit timestamps.
  All three are schema changes and belong to the remediation plan in
  `security-and-data-audit.md` §5, not smuggled in here.

## Cost

Zero — local Postgres in Docker, no new packages.

## Verify locally

```bash
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=jobkeep postgres:16-alpine
cd src && dotnet run
```

- `POST /applications/{id}/skills` with `{"skillName":"C#","isRequired":true}`,
  then `GET /applications/{id}` shows it under `posting.postingSkills`.
- `DELETE /applications/{id}/skills/C%23` removes the link — and `GET` on another
  application that uses C# still shows it (the shared `skills` row survived).
- `POST /applications/{id}/requirements` with
  `{"text":"5+ years .NET","kind":"Qualification","isMustHave":true}`, then
  confirm it appears under `posting.requirements`.
- Same four operations via the GraphQL Nitro IDE at `/graphql`.
- A bad id on any of them returns 404 over REST and a `NOT_FOUND` error over
  GraphQL — the same handler decided both.

## Interview talking points

- **The abstraction cost, and reversing it.** The original plan for this phase was
  "add four methods to the repository interface, then implement each twice". That
  is the cost of the abstraction stated plainly — and the reason to drop it. The
  interface got *smaller* during a phase that added four features.
- **Deleting a join row vs. the shared entity it points at.** Removing a skill
  from a posting deletes the `posting_skills` row and deliberately leaves the
  `skills` row; the FK is `Restrict` so it can't be otherwise. A normalization
  consequence you have to reason about, not a default.
- **One rule, two surfaces.** `SliceResult` is the mechanism: the handler decides,
  and each edge only translates. Before this, the REST create path hand-rolled null
  checks and the GraphQL mutation path had none.
- **Idempotent add.** Re-posting a skill that's already linked returns 200, not
  400 — the composite PK already says "at most once per posting", so the client is
  asking for a state that holds.

## Next

Phase 2.2 — automated tests + CI. Scheduled ahead of the remaining Phase 2
features because the gap register calls it "the single largest gap": this phase
added four slices and a second API surface with nothing verifying either.

Then Phase 2.3 — filter, sort, and page the applications list. It owns the read
path, which is where A1 (GraphQL over-fetch) and the rest of A2 (entities as the
API contract) get fixed.
