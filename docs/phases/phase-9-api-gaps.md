# Phase 9 — the three reads the front end asked for and could not get

**Status: Planned.** Not started.

> **Three premises in this plan expired after it was written (checked 2026-09-04).
> Read this box before anything below it.**
>
> 1. **Decision 17 is REVERSED.** Item 1 below argues that projecting another
>    module's table is "legal under decision 17 and needs no contract". **Phase 13
>    reversed that: every crossing needs a contract now, reads included.** So item 1
>    is *not* "a change to one projection expression" — it needs a method on
>    `IMatchContract`, and the test for whether it belongs there is the one in
>    `CLAUDE.md`: does it name a **fact about the thing**, or a **question the
>    caller has about its own feature**? A per-application match summary for a list
>    row is arguably the second kind, which is the design question item 1 actually
>    opens. **Settle that before writing code.**
> 2. **`Ats` is `Match` and `ats_results` is `match_results`** (renamed 2026-09-04).
>    The module is `Jobkeep.Modules.Match`, the contract `Jobkeep.Contracts.Match`,
>    the routes `/applications/{id}/match-check`. The Postgres *schema* is still
>    `ats`, deliberately. Names below are pre-rename and were left as written.
> 3. **Phase 10 is DROPPED**, so the "Next" line at the bottom is dead and the
>    "they come before the deploy" argument no longer has a deploy to come before.
>    The reasoning still holds against *whatever* deploy replaces it.
>
> Nothing else about the three gaps changed — they are all still real, and all
> three screens still work around them.
>
> **And the estimate below deserves the same suspicion.** Phase 8's front-end
> estimate was wrong by an order of magnitude in exactly this way: it was written
> against a *planned* UI, before Phase 6 shipped, and costed the conversion of
> affordances that never existed. This doc's "LOW–MEDIUM, entirely additive" is of
> the same vintage. **Re-check it against `web/` before trusting it.**

## Why these exist, and why they are one phase

All three were found the same way, in step 6.3: by **building the screen**, not by
reading the contract. None is a bug — each is a place where the approved design
asks a question the frozen API cannot answer in one request. They are grouped
because they are all changes to *reads on `job_applications`*, and because each
one currently costs a shipped screen something visible.

Their cost is **flat** — no more expensive in six months than today — which is why
they sit behind Phases 7 and 8 rather than in front of them. They come before the
deploy because each one is a screen currently telling a small lie, and the deploy
is the first time someone other than the author sees it.

The evidence for each is in
[`phase-12-feature-expansion.md`](phase-12-feature-expansion.md) → "Three backend
gaps the front end found".

## Scope

### 1. Project `ats_results` into `ApplicationListItem`

**Two callers now want this**, which is the evidence that it is worth doing:

- The Applications artboard has a "CV match" column (`0/9`, `5/7`, `not checked`).
  It cannot be served without a request per row, so **the column was dropped, not
  faked**.
- Today would like to say "these three have never been checked against a CV", and
  cannot, for the same reason.

This is a **read** across a module boundary — Applications projecting a table Ats
owns — which is **legal under decision 17** and needs no contract. So it is a
change to one projection expression, not an architectural one. That is worth
saying out loud, because under rule 2's *old* wording it would have needed a
contract on Ats, and decision 17 exists precisely so it does not.

### 2. `ApplicationQuery.Status` takes a set, not one value

`Status` is a single value today, so there is no "Closed" tab covering Rejected
and Withdrawn together — the union of two requests cannot be paged honestly.

- `Status[]` on the query, with `IsClosed` as sugar over it.
- Both surfaces. GraphQL takes a list of enum values; REST takes a repeated query
  parameter.

### 3. A board-shaped read

`ListApplications` caps `pageSize` at 100 and *rejects* above it. That is right for
a list and awkward for a board: the Pipeline holds every card at once, so it
currently fetches pages in a loop up to a ceiling of five and prints an honest
footer past that. It works, and it is not the shape anyone would design.

**Do the minimum here.** Phase 12's note says whatever fixes this — a cursor, a
board-shaped read — should wait until something other than one screen wants it.
One screen still wants it. So: a dedicated board read returning the columns'
cards, capped and projected, rather than a general-purpose cursor API built for a
caller that does not exist yet.

## Frontend impact: **LOW–MEDIUM, and entirely additive**

Nothing that exists changes shape. Three screens gain something they currently
work around:

- **Applications** — the CV-match column comes back. New markup, no layout change;
  the artboard already has the column.
- **Today** — the "never checked against a CV" signal becomes possible. Today's
  copy is currently, deliberately, honest about not knowing this.
- **Pipeline** — the five-page loop and its footer disappear. That is a deletion.

Each response type in `web/src/lib/api.ts` mirrors a C# record with the source
file named in a comment, and `src/test/fixtures.ts` is hand-written against the
same records — so the shapes must move in both places together, and a guess that
is wrong fails a test rather than an opened screen.

Per the Phase 12 checklist item 6: **update the route table** in
`phase-6-frontend.md`. This phase makes that snapshot stale, which is the one
thing it is there to detect.

## Verification

- Integration tests through both surfaces, plus a parity test — the `Status[]`
  filter is a new failure mode (an empty array, an unknown value) and parity is
  where "one rule, one implementation" is actually pinned.
- The projection must not reintroduce an include graph. Flat `.Select(...)`, per
  A1 and decision 11.
- Front-end tests: the CV-match column renders `not checked` for an application
  with no `ats_results` row, which is the common case and the one most likely to
  render as `undefined`.

## Next

~~[Phase 10](phase-10-aws-deploy.md) — the deploy, unparked.~~ **The AWS deploy was
dropped on 2026-09-04** (`architecture.md` decision 22) and a free host is still to
be chosen. Nothing is scheduled after this phase; see the roadmap table in
[`docs/README.md`](../README.md).
