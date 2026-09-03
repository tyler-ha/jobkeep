# Phase 12 — feature expansion, after the front end exists

> **Formerly "Phase 7".** Renumbered 2026-09-01 when the roadmap was reordered by
> compounding cost and phases 7–11 were slotted in front of it. Its content is
> unchanged; it is still not a feature list. See `architecture.md` decision 18.

**Status: placeholder.** Nothing here is scheduled and nothing here is committed.

## What this doc is, and what it is not

**It is not a feature list.** [`backlog.md`](../backlog.md) already holds those,
with sizes, likely homes and the reasoning behind each deferral. Copying them here
would create a second register, and two registers drift.

**It is the record of what changes about *building* a feature** once Phase 6 has
shipped a front end — written now, at the checkpoint, rather than discovered later
when an estimate turns out to be half the real number.

The commit where that changes is tagged:

```
checkpoint/backend-complete        # git show checkpoint/backend-complete
```

Everything before it was a backend-only project.

## The one rule: a feature now has two halves

Through Phase 5, adding a feature meant:

> a slice file under `src/Modules/<Module>/`, two lines in `<Module>Module.cs`,
> a field or two on `Query.cs` / `Mutation.cs`, and an integration test.

That is still true, and it is now **the first half of the job**. The second half
is a screen or a component, the fetch call behind it, and a visual that stays
inside the approved token system without inventing a colour.

Two consequences worth stating plainly, because both are easy to get wrong:

- **Estimates carried over from Phase 2–5 will be roughly half of the real cost.**
  "It's just one slice" was an honest description of a whole feature then. It
  describes half of one now.
- **A backend-only change can still be incomplete.** Shipping
  `POST /resumes/{id}/skills` in Phase 5 without its `DELETE` inverse was
  defensible while there was no UI — nothing could drag the wrong skill on. The
  moment a screen existed, the missing half became a hole in a shipped
  interaction, and step 6.1 had to go back for it. Ask what the UI would need,
  not just what the API can express.

## The checklist for one feature

1. **Backend slice**, in the owning module. `AppDbContext` directly, validate in
   the handler, return a DTO. The rules in `CLAUDE.md` under "Where new code goes"
   are unchanged.
2. **Both surfaces.** REST route *and* GraphQL field, over the same handler, so a
   rule cannot mean two things. File upload is the one standing exception
   (`DocumentsModule.cs` argues it).
3. **Integration test through the real surface**, not a unit test with a fake —
   plus a parity test if the feature adds a new failure mode.
4. **Front-end surface.** Whatever screen it lands on, or a new one.
5. **Tokens only.** No new colours, no new type family. The amber budget is one
   held moment per screen plus the two functional uses; a missing or absent thing
   never takes the alert red. If a feature seems to need a new token, that is a
   design decision to raise, not a CSS variable to add.
6. **Update the route table** in
   [`phase-6-frontend.md`](phase-6-frontend.md) — it is the snapshot the front end
   was built against, and a feature that adds an endpoint makes it stale.
7. **Ask before adding a dependency.** Standing instruction from the user, on both
   sides of the stack.

## Where front-end code goes

Filled in by step 6.2, 2026-08-29, once the scaffold actually existed.

The front end lives in **`web/`** at the repo root — deliberately not under
`src/`, which holds the .NET project and may contain exactly one `.slnx`.

```
web/src/routes/<Screen>.tsx   — one file per screen; it owns its own data fetching
web/src/components/           — a component only moves here once a SECOND screen needs it
web/src/lib/api.ts            — the fetch core: base URL, ApiError, shared domain types
web/src/lib/format.ts         — dates, salaries, enum names. DateOnly never touches new Date().
web/src/styles/tokens.css     — the design tokens. The palette lives here and nowhere else.
web/src/styles/base.css       — reset, element defaults, browser surfaces
web/src/styles/shell.css      — the app frame and the shared primitives
web/src/styles/screens.css    — the per-screen blocks (added 6.3, when shell.css would have passed 800 lines)
web/src/lib/chart.ts          — chart arithmetic (added 6.3). Pure, and tested, because a bad scale still looks like a chart
web/src/test/                 — the Vitest setup and the API fixtures the screen tests render against
web/src/App.tsx               — routes and navigation only
```

Tests sit **beside what they test** — `lib/format.test.ts`, `routes/screens.test.tsx` —
rather than in a mirrored tree. `vitest.config`'s `include` is `src/**/*.test.{ts,tsx}`,
so a new one needs no registration.

A new screen is a new file in `routes/`, plus one `<Route>` and (if it is a
destination rather than a detail view) one entry in the `NAV` array in
`App.tsx`. That mirrors the backend rule it sits opposite: a new slice is a new
file plus two lines in its module.

**Three rules, and they are the front-end counterparts of the backend's two:**

- **A screen owns its use case end to end**, the same way a slice does. It calls
  `api` directly. Do not introduce a store, a service layer, or a hook library
  over the top of a fetch that one screen makes — that is the repository mistake
  from Phase 2.3 in a different language.
- **`tokens.css` is the only place a colour is defined.** A raw hex anywhere else
  is a bug. This rule exists because the artboards used 145 unnamed hex values
  across eight screens; naming them was step 6.2's main work, and it only stays
  true if nothing reintroduces a literal.
- **On a tinted surface, the label is the `-dark` token, never the base.** This is
  what keeps the UI at WCAG 2.2 AA without auditing each component: every
  dark-on-tint pair clears AA text, and `--sec` on `--sec-tint` does not.

A fourth rule, learned in 6.3 rather than planned: **arithmetic that a chart or a
date depends on goes in `lib/` with a test, never inline in a screen.** Both
things that broke in this step were of that kind — a salary range that printed
its unit twice, and a percentage set that summed to 99. Neither looks wrong on
screen; both are one assertion to pin.

Anything the API returns gets a type in `lib/api.ts` mirroring the backend
record, with the source file named in a comment. The shapes are easy to guess
wrong — the list item is `company`, not `companyName`, and `dateApplied` is a
`DateOnly` string, not a timestamp. Since 6.3 the fixtures in `src/test/fixtures.ts`
are hand-written against the same C# records, so a guess that is wrong fails a
test instead of an opened screen.

## Which backlog items visibly reshape the front end

Pointers only — the rows themselves, with sizes and reasoning, live in
[`backlog.md`](../backlog.md). Listed by how much *front-end* work they imply,
which is not the same ordering the backlog uses:

| Backlog item | What it does to the UI |
|---|---|
| **Reminders / follow-ups** | Gives the Today screen its reason to exist. Today is currently a summary; with due dates it becomes the screen you actually open first. |
| **Interview rounds** | Reshapes the Pipeline board. `Interviewing` is one column today; rounds make it a column with depth, and the drag semantics have to answer what moving a card between rounds means. |
| **Contacts / recruiter tracking** | A ninth screen, plus a presence on the Job post detail. The first genuinely new surface. |
| **Target profile** | Reshapes Insights. Phase 2.4 answers "what is in demand"; a target changes it to "what is in demand that I don't have yet" — a different chart, not an extra row. |
| **Soft delete / archive** | Touches **every list**: an archive filter, an empty state that distinguishes "none" from "none active", and an undo affordance. Cheap on the backend, wide on the front. |
| **Authentication / multi-user** | Touches all of it, plus a login, plus every fetch growing a credential. Also forces the CORS policy added in 6.1 to be revisited — `AllowAnyOrigin` and credentials are mutually exclusive, and `Program.cs` says so at the point it matters. |
| **Data export (CSV/JSON)** | Nearly free on both sides — a read endpoint and a button. The cheapest real feature on the list once a UI exists. |

## Three backend gaps the front end found (6.3, 2026-08-31)

Neither is a bug. Both are places where the approved design asks for something
the frozen API cannot answer in one request, found by building the screen rather
than by reading the contract.

- **The application list carries no ATS summary.** The artboard's "CV match"
  column (`0/9`, `5/7`, `not checked`) needs it per row, and neither REST nor
  GraphQL can supply it without a request per row. The fix is to project
  `ats_results` into `ApplicationListItem` — a *read* across a module boundary,
  legal under decision 17, so it is a change to one projection rather than an
  architectural one. Until then the column is dropped, not faked.
- **`ApplicationQuery.Status` takes one status.** So there is no "Closed" tab
  covering Rejected and Withdrawn together; the union of two requests cannot be
  paged honestly. A `Status[]` would fix it, and `IsClosed` would be the sugar
  over it.

- **There is no "give me the whole set" read.** `ListApplications` caps
  `pageSize` at 100 and *rejects* above it, which is right for a list and awkward
  for a board: the Pipeline holds every card at once. It fetches the pages in a
  loop up to a ceiling of five and prints an honest footer past that, which works
  and is not the shape you would design. Whatever fixes this — a cursor, a
  board-shaped read — should wait until something other than one screen wants it.

Building the second half of the screens **reinforced the first gap rather than
adding a new one**: Today would like to say "these three have never been checked
against a CV", and cannot, for exactly the reason the CV-match column cannot
exist. Two callers now want `ats_results` on the list, which is the evidence that
projection is worth doing.

One finding needed no backend change and is worth carrying: **the counts on a
filtered list come from `GET /stats/funnel`, not from the list itself.** A list
filtered to one status can only count that status. That pattern repeated exactly
as predicted — Today's status strip is the same aggregate again, used as
navigation rather than as a chart.

## What does not change

- **The architecture rules.** Modules, slices, one rule / one implementation,
  aggregate in SQL, DTOs at the edge. A front end consumes the API; it does not
  get to reach past it, and "the UI needs it shaped differently" is an argument
  for a new slice, not for returning an entity.
- **Cost stays near-zero.** A static front end is free to host; nothing here
  introduces something that bills per hour.
- **Phases end runnable.** A feature whose backend shipped and whose screen did
  not is a half-applied change, which is exactly what priority 2 exists to stop.
