# Phase 8 — soft delete / archive

**Status: DONE (2026-09-04).** Two migrations (`SoftDelete`, in the `applications`
and `documents` schemas), suite **303 → 314**, web **50 → 52**. Ran immediately
after [Phase 7](phase-7-data-integrity.md), as planned, and could not have run
before it.

The plan below is unedited. What actually happened, and where it differs, is in
["What shipped"](#what-shipped) at the bottom — including the one estimate that
was wrong by an order of magnitude and why.

## Why here, and not later

Two reasons, and the second is the one that decided the position.

**It is coupled to Phase 7's migration.** `companies.Name` and `skills.Name` are
UNIQUE, and under soft delete they must become **filtered** unique indexes
(`.HasFilter("\"IsDeleted\" = false")`) or soft-deleting a company permanently
blocks ever re-adding that name — and the find-or-create dedup depends on those
exact indexes. Phase 7 is already rebuilding those indexes for the natural key.
Doing this separately means migrating the same three indexes twice.

**Its cost is in the front end, and that cost grows per screen.** This is the item
with the **highest front-end blast radius on the entire roadmap** — see below.
There are eight screens today. Phase 12's feature list adds more. Every screen
built before this lands is another screen to retrofit, so the compounding is real
even though the backend half is trivial.

`backlog.md` calls it "the strongest candidate to pull in" on cost grounds alone.
The front end existing is what turns that from *cheap* into *cheap now, and not
later*.

## Scope

Closes **F10**. `security-and-data-audit.md` §5 step 2.

- `IsDeleted` + `DeletedAtUtc`, with `HasQueryFilter` so every existing query
  excludes archived rows without being rewritten.
- Filtered unique indexes on `companies.Name`, `skills.Name`, `resumes.Label`.
- `DELETE` slices become archive slices; a restore slice is the new use case.
- An `includeArchived` flag on the list reads — one parameter, both surfaces.

Deliberately **not** in scope: a purge job, or a retention rule. That is F18 and
it lives with the PII work in [Phase 10](phase-10-aws-deploy.md).

## Frontend impact: **HIGH — the highest on the roadmap**

The backend half is one migration and a query filter. The front-end half is the
phase. Stated plainly because the Phase 12 checklist exists to stop exactly this
being under-estimated:

- **Every list route changes**, and there is no shared list component to change
  once. `Applications.tsx`, `Pipeline.tsx`, `Resumes.tsx`, `Import.tsx` and
  `Today.tsx` each own their own fetch and their own render — that is the
  deliberate "a screen owns its use case end to end" rule from Phase 6, and this
  is the first time it bills.
- **Every empty state needs a second meaning.** "No applications" and "no *active*
  applications" are different sentences, and the current copy only writes one.
- **Every delete affordance becomes an archive affordance**, with an undo. `.btn-danger`
  currently means "gone"; it would come to mean "archived", and the alert-red tone
  rule (`PRODUCT.md`: a missing or absent thing never takes the alert red) says an
  archive is not an error — so the visual treatment changes too, not just the copy.
- **An archive filter** on Applications, which already reads `?company=`,
  `?title=` and `?status=` from the query string — so the mechanism exists and
  this is one more parameter, not a new pattern.

Nothing here needs a new token or a new colour. That is the constraint to hold.

## Verification

- Delete-behaviour matrix tests from Phase 2.2 extended: an archived parent must
  not cascade, and its children must still be reachable on restore.
- A test that find-or-create still dedups a company whose name was soft-deleted —
  this is the filtered-index gotcha, and it is the one that fails silently.
- All 35 front-end tests green, plus new ones for the two empty states.
- No ERD change (`IsDeleted` is two columns, but the index *filters* change) —
  redraw `schema-erd.svg` anyway; the index predicate is the kind of thing an
  interviewer probes.

## Next

[Phase 9](phase-9-api-gaps.md) — the three reads the front end asked for and could
not get.

---

## What shipped

### The estimate that was wrong, and the reason it was wrong

This doc says the front-end half **is** the phase, and calls it "the highest
front-end blast radius on the entire roadmap". **That was wrong, and it was wrong
for an instructive reason: it described a UI that did not exist.**

The line *"every delete affordance becomes an archive affordance"* assumed there
were delete affordances. There were none. `deleteApplication` was exported from
`web/src/lib/api.ts` and **called by nothing**; the single `.btn-danger` in the
whole front end is on Upload's discard-import button, which is not a delete slice
at all. So there was nothing to retrofit — the archive UI is **new**, not
converted, and it landed on **one screen**.

The other four list routes needed **no change whatever**, and that is the query
filter earning its place rather than an omission: `Today`, `Pipeline`, `Resumes`
and `Upload` each own their own fetch, and every one of those fetches now excludes
archived rows without a line being written. The plan's "five list routes, five
empty states" was costed against a world where each screen had to learn about a
new concept. They did not have to; that is what a global filter buys.

**The rule worth carrying forward:** a blast-radius estimate written against a
*planned* UI decays as fast as the plan does. This one was written before Phase 6
shipped and was never re-checked against the eight screens that actually got
built.

### Where the cost actually was

Not in the front end. In three places the plan did not mention:

1. **The three published views.** `HasQueryFilter` is an EF construct; a view is
   SQL Postgres runs on its own, so all three of Analytics' read models kept
   counting archived rows. The migration hand-writes `CREATE OR REPLACE` for each,
   and `v_posting_skill_demand` **gained a JOIN it never had**, because
   `posting_skills` carries no `IsDeleted` of its own. This is the phase's one
   genuinely silent failure mode — an Insights page counting archived
   applications looks exactly like one counting live ones — and it is why
   `SoftDeleteTests` is weighted towards the views rather than towards the routes.

2. **The 13.3c delete notifications, which are now unpublished.** This was the
   real design decision. `ApplicationDeleted` and `PostingDeleted` existed so that
   Match and Ai would delete their derived rows when a subject was deleted.
   Archiving is not deleting: the subject still exists and is one click from
   coming back, so publishing would destroy a stored match check — a model call
   the user waits minutes for — about a row that survived. `DeleteApplication.cs`
   made exactly that weighing in 13.3c ("prefer the residue nobody can see") and
   soft delete moves the row to the other side of it. **Neither event is published
   any more, and both handlers are unreachable through the app.** They are kept,
   with a `ponytail:` comment, because a purge (F18) is the caller they were
   written for and it is a named backlog item; delete them if purge is ever
   refused outright.

3. **`resumes.LabelNormalized` had to become a *filtered* unique index**, which
   this doc did predict — and the price it did not. Archiving must free the label,
   or an archive silently burns a name and the failure surfaces later, on an
   unrelated import, as a constraint naming a document the user cannot see. The
   cost is that **a restore can now be refused**: another résumé may have taken
   the label. `RestoreResume` asks first and answers 400 with a sentence, rather
   than letting the index throw and become a 500.

### What the scope actually was

**Three entities are soft-deletable — the three with a delete slice:**
`JobApplication`, `JobPosting`, `Resume`.

**`companies.Name` and `skills.Name` did NOT get filtered unique indexes**, and
this doc's Scope section is wrong to ask for them. Neither entity has a delete
path and neither ever has — *"nothing deletes a skill"* is a standing property of
this codebase. A filter predicate on an index no row can fail is a promise about a
code path that does not exist. `ISoftDeletable`'s comment records the exclusions
and the rule they produce: **a row is soft-deletable when a user can end its life,
and only then.**

### How it is built

- **`ISoftDeletable` in SharedKernel**, matched by `AuditSaveChangesInterceptor`,
  which converts `EntityState.Deleted` to `Modified` and stamps the two columns.
  So **`Remove()` means archive**, and a slice written next year cannot
  accidentally hard-delete by writing the obvious thing. It runs *before* the
  audit loop, so an archive is stamped with `UpdatedAtUtc` by the code that
  already does that.
- **The cascades stop firing, and that is the mechanism rather than a side
  effect.** No `DELETE` reaches Postgres, so `posting_skills`, `job_requirements`,
  `resume_skills`, `resume_experiences` and `resume_educations` all survive. That
  is the difference between a restore and a re-import — those rows are the
  expensive part, since they cost a model call to produce.
- **Three restore slices, `POST /{resource}/{id}/restore` on both surfaces.**
  `IgnoreQueryFilters()` is the whole slice: every other read is written as if
  archived rows do not exist, and this is the one place that has to see past it.
  Restoring a live row is a **404**, which is the delete's "already gone is a 404"
  from the other side.
- **`?includeArchived=true` on the two list reads.** It means *include*, not
  *only* — which is what the words mean to the person ticking the box. On
  applications it calls `IgnoreQueryFilters()` on the whole query rather than
  loosening one predicate, because `job_postings` is filtered too and an inner
  join would otherwise silently drop an application whose ad was also archived.
- **`isArchived` is on both list items.** A client that could not tell which rows
  were archived would have to infer it from the request it made, and a component
  re-rendering off cached data no longer knows which request that was.

### Deviations from the plan, listed

| Plan said | What shipped | Why |
|---|---|---|
| Filtered unique indexes on `companies.Name`, `skills.Name`, `resumes.Label` | **`resumes` only** | Neither of the other two has a delete path |
| "Every list route changes" | **One screen changed** | The query filter covers the other four for free |
| "Every delete affordance becomes an archive affordance" | **New affordance, nothing converted** | There were no delete affordances in the UI |
| Nothing about the analytics views | **Three views rewritten in the migration** | A view is SQL; `HasQueryFilter` does not reach it |
| Nothing about the 13.3c notifications | **Both stopped being published** | An archive must not destroy derived work on a surviving row |
| "Redraw `schema-erd.svg` anyway; the index predicate is the kind of thing an interviewer probes" | **NOT redrawn** | Diagrams are frozen until 1.0 ships on master (CLAUDE.md, 2026-09-02). Debt noted here so the eventual redraw is a list: **two new columns on three tables, and a partial predicate on `IX_resumes_LabelNormalized`.** |

### The UI decision worth defending out loud

**The archive button is not `.btn-danger`.** `PRODUCT.md` reserves the alert red
for genuine failures and for destruction, and an archive is neither — it is
reversible, and it is a tidy-up the user does on purpose. Dressing it in red would
make the safest action on the screen look like the most dangerous one. The plan
anticipated exactly this ("the alert-red tone rule says an archive is not an
error"), and it is the one part of the front-end section that survived contact.

**The undo is an inline banner with no timeout**, not a floating toast. The
archive is reversible on the server for as long as the row exists, so there is no
deadline to communicate and a timer would only punish someone who looked away. It
sits in the flow and pushes the table down by a line, which is also what makes it
impossible to miss.

### One warning suppressed, with its reason

EF warns that `JobPosting` carries a query filter while `PostingSkill` and
`JobRequirement`, on the required end of a relationship with it, do not. The path
it describes cannot be reached: every slice touching either table resolves its
posting id out of `job_applications` first, and that read carries the filter. EF's
suggested remedy — matching filters on both children — would add an `EXISTS` back
to `job_postings` on every read of either table, **including inside
`ListApplications`' skill filter and per-row skill projection**, which is the
app's busiest query. The suppression is in `Program.cs` with a `ponytail:` note
naming what would break it: a future route addressed by posting id.

### Verification

Everything this doc's Verification section asked for, plus the views:

- The delete-behaviour matrix from Phase 2.2 was **flipped in place**, not
  deleted — the repository's standing practice. `DeletingAnApplication_TakesItsMatchResultWithIt`
  has now been 0, then 1, then 0, and is **1 again**, across four phases; the test
  carries all four states and what each one meant, because the churn is the
  interesting part.
- The filtered-index gotcha has its own test, and it asserts **both halves in one
  place**: archiving frees the label (case-insensitively, so Phase 7's natural key
  and Phase 8's predicate are proven to be on the *same* index), and the restore
  is then refused with a sentence.
- `ThePostingRestrict_StillExists_EvenThoughNothingReachesItAnyMore` goes around
  EF with a raw `DELETE`, because the change tracker can no longer make that
  constraint speak. The key is **dormant, not retired** — a purge would wake it —
  so keeping it provable is worth one line of SQL.
- Both surfaces, including restore. A GraphQL client that could archive but not
  restore would be able to reach a state it cannot leave.

### What is explicitly still not done

**No purge and no retention rule.** That is F18, it was out of scope here, and
soft delete makes it *more* necessary rather than less: `match_results` and
`ai_analyses` rows now outlive every archive, because nothing deletes them any
more. The two unpublished notifications are waiting for exactly that job.

