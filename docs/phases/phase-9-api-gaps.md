# Phase 9 — the three reads the front end asked for and could not get

**Status: IN PROGRESS.** **Gaps 2 and 3 are DONE (2026-09-04)** — gap 2 took the
suite 314 → 323 and web 52 → 53, gap 3 took it 323 → 328 and web 53 → 54. Neither
needed a migration. **Only gap 1 is left, and it needs a design decision before any
code** (see premise 1 in the box below). They were taken in that order because gap
1 is the only one of the three with an open question in it.

What each shipped is at the bottom: ["Gap 2, as built"](#gap-2-as-built) and
["Gap 3, as built"](#gap-3-as-built).

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

---

## Gap 2, as built

`ApplicationQuery.Status` is `ApplicationStatus[]?` and there is a new
`bool? IsClosed`. **The route table did not change**, so the Phase 6 API snapshot
is untouched — this is a query-parameter change, not a surface change.

### Neither surface broke, and the reason is worth knowing

The plan said "GraphQL takes a list of enum values; REST takes a repeated query
parameter" and did not say what happens to callers written against the old shape.
Both keep working, for two different reasons, and **both are now pinned by tests
because neither was covered before**:

- **REST** binds a repeated query parameter to an array, so `?status=Applied`
  arrives as a one-element array. This was covered *by accident* —
  `ListApplicationsTests.Filter_ByStatus` kept passing.
- **GraphQL** coerces a single value to a list of one (the spec's input-coercion
  rule for list types), so `applications(query: { status: APPLIED })` still works
  unedited against `[ApplicationStatus!]`. **This had no test at all** — the only
  GraphQL status test in the suite is on `updateApplication`'s input. It was
  verified against the running HotChocolate schema, not just asserted.

### The surfaces genuinely differ in one place

An **empty** list. GraphQL can send `status: []`; REST cannot produce one, because
`?status=` binds an empty string to an enum, fails model binding and answers 400 —
identically to `?status=Banana`, which is correct. The handler treats an empty
array as "no filter" rather than "match nothing".

That is **not** a parity break: the two surfaces agree on every input both can
express. REST simply has no spelling for this one. The first version of the test
assumed they matched and failed, which is how this got written down.

### `IsClosed` is a domain fact, not query sugar

The plan called it "sugar over" the status set. It is more than that, and where it
lives is the decision:

**`ApplicationStatusTransitions.Closed` now names the set**, in `Domain/`. That set
is not new — it has been load-bearing since Phase 2.5, spelled out four times by
hand in the transition table and argued for in that file's header, because it is
what makes *"an Offer can only be reached from an active application"* true. What
is new is that it has a name.

If the front end had sent `?status=Rejected&status=Withdrawn` for its Closed tab
instead, there would be a **second copy of that answer in TypeScript**, free to
drift from the one the PATCH rule enforces. So the set is named once and both the
query and the screen read it.

**The transition table was deliberately not rewritten to derive from the set.** It
is a business decision confirmed with the user, listed stage by stage so it reads
as a table rather than a computation. Instead a test pins the two together: *the
closed stages are exactly the stages that cannot reach an Offer.* Phase 2.5 stated
that invariant in prose and nothing had ever checked it.

### `status` + `isClosed` together is refused

400, on both surfaces, rather than merged or silently resolved. Two ways of saying
which stages you want, in one request, is a question with no answer the caller can
predict — and letting one win silently means a caller who sent both never learns
which. The front end's filter state is a **single union** (`ApplicationStatus |
'Closed' | null`) precisely so the UI cannot construct the request the API rejects.

### The Closed tab has no count, on purpose

Every other status tab shows one, from `/stats/funnel`. Summing Rejected and
Withdrawn client-side would have been three lines — and it would have reintroduced
the second definition of "closed" that the rest of this work exists to avoid. A tab
reading "3" above four rows is exactly the small lie that costs trust in a screen.

**If that count is wanted, `/stats/funnel` should publish it.** Not doing that here
because it is a change to a Phase 2.4 read model for one label, and nothing else
has asked.

### Verified

Suite **323/323** and web **53/53**, plus a live check against the running stack
for the things a test can pass and MVC can still surprise you on: single status,
repeated `?status=A&status=B`, `isClosed=true`/`false`, the 400 on both, and both
GraphQL forms.

### Still open in this phase

- **Gap 1** — project the match result into `ApplicationListItem`. **Blocked on a
  decision**: decision 17 is reversed, so this needs an `IMatchContract` method, and
  whether a per-application match summary belongs on a contract is the question
  `CLAUDE.md`'s test asks (a fact about the thing, or a question the caller has
  about its own feature?). Settle that first.

---

## Gap 3, as built

`GET /applications/board` and the `applicationBoard` GraphQL field, one slice
(`Applications/Application/GetBoard.cs`), no migration. Suite **328**, web **54**.
The Pipeline screen's five-page loop and its `PAGE_SIZE` / `MAX_PAGES` constants
are deleted; the honest footer stays, because a cap is still a cap.

### It is a narrower row, not a bigger page

The obvious version of this is `pageSize=500`, and it is the wrong one twice over:
it widens the list's cap for every caller, and it keeps paying for a projection the
board does not use. `BoardCard` carries a **skill count** where `ApplicationListItem`
carries skill **names** — the card renders "· 3 skills" and never the names — so the
board's read also skips the `ISkillCatalog.GetAsync` call the list makes for every
page. One query per board instead of two per page, times five.

It is deliberately **not** a cursor API. Phase 12's note said whatever fixes this
should wait until something other than one screen wants it; one screen wants it, so
this reads that one screen and nothing else — no filter, no sort, no page, because
the board has no control for any of them. `GetBoard()` takes no arguments at all,
which is why the GraphQL field takes none either.

### Flat cards, not columns

The plan said "returning the columns' cards". It returns a flat list, because the
board *moves* cards between columns optimistically: with columns on the wire every
drag would splice two arrays instead of changing one field, to save a grouping the
client already does in one pass over rows it has.

### Two things that only show up against real Postgres

- **EF cannot order a constructor-projected record.** `Select(...).OrderBy(c => c.DateApplied)`
  over a positional record throws — the members are not mapped back to columns. The
  `ORDER BY` therefore sits on the entity query and the projection is a small private
  method applied after it. It **throws rather than falling back to client evaluation**,
  which is the good version of that failure and is why five tests found it at once.
- **The count is taken through the projection.** `job_postings` carries Phase 8's
  query filter too, so reaching `a.Posting` makes the read an inner join; a count
  taken off the bare table would report rows the board cannot show — a footer
  announcing missing cards on a board that is complete.

### The cap is 500, and it is not tested

The same number the front end reached by looping five pages of a hundred, so a full
board costs what it always did; only the request count changed. Observing the cap
needs 501 applications arranged through the real create path, which buys less than
it costs — and the same is true of the `ThenBy(a => a.Id)` tiebreak, which is only
reachable at that boundary. Both are argued in `GetBoard.cs` instead. What the tests
do cover is the projection (`skillCount` present, `skills` absent), archived rows
being off the board *and* out of its count, newest-first ordering, and GraphQL
agreeing with REST.

### Verified

Suite **328/328**, web **54/54**, oxlint clean apart from the pre-existing warning
at `web/src/routes/MatchCheck.tsx:193`. Checked live against the running stack as
well as the suite: `GET /applications/board`, the GraphQL field, `swagger.json`
still answering 200 (the whole document 500s if one route is unrepresentable), and
`/applications/{id}` still resolving — `board` cannot shadow it, because the route
constraint says `{id:guid}`.

