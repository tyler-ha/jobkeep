# Phase 8 — soft delete / archive

**Status: Planned.** Not started. Runs immediately after
[Phase 7](phase-7-data-integrity.md), and cannot sensibly run before it.

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
