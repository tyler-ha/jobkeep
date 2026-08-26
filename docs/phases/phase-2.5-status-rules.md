# Phase 2.5 — Enforce the application status lifecycle

**Status: Done** (2026-08-26)

> **Architecture note (2026-08-25):** build this as vertical slices under
> `src/Modules/`, per `docs/architecture.md`. Do **not** add methods to
> `IJobApplicationRepository` — it is retiring, not growing.

## Goal

Today `Status` is a free-for-all enum — a PATCH can jump Applied → Offer →
Withdrawn → Applied with no rules. Add the domain invariant: only valid status
transitions are allowed, and invalid ones are rejected with a clear error.

## Why this is Phase 2 work

The relational model captures the *nouns*; this captures a *business rule* about
how the core noun (an application) is allowed to change over time. It belongs
with the model, before the API is deployed (Phase 3) and before AI features
(Phase 4/5) start writing to it. Small, self-contained, ends runnable.

## The lifecycle, as built

Confirmed with the user before implementing, which is what this section asked for.
The agreed table is **looser than the one originally proposed** — both changes are
below, with the reasoning, because "why is this rule so permissive?" is exactly
what an interviewer would ask.

```
Applied      → Interviewing | Offer | Rejected | Withdrawn
Interviewing → Offer | Rejected | Withdrawn
Offer        → Rejected | Withdrawn        (rejected = declined/rescinded)
Rejected     → Applied | Interviewing | Withdrawn
Withdrawn    → Applied | Interviewing | Rejected
```

Same-status "transitions" (a no-op PATCH, or one that doesn't mention status at
all) stay allowed.

**Deviation 1 — `Applied → Offer` is legal.** The original table forced every
offer to pass through `Interviewing`. An offer can genuinely arrive without an
interview stage ever being *logged*: a referral, a contract role, or simply not
having moved the card. A tracker that argues with what happened is worse than one
that records it.

**Deviation 2 — `Rejected` and `Withdrawn` are *closed*, not terminal.** The
original table made both one-way doors. The user asked to follow how the
comparable products behave, and both are permissive: **Huntr** is a drag-and-drop
Kanban board whose cards move between any columns in either direction, and
**Teal** is a status dropdown you can re-select at any time. Neither treats a
closed stage as final. So a closed application can be re-opened or re-labelled.

The invariant that survives, and the one worth defending: **an `Offer` can only be
reached from an active application.** Five transitions are refused —
`Interviewing→Applied`, `Offer→Applied`, `Offer→Interviewing`, `Rejected→Offer`,
`Withdrawn→Offer`. An active application does not walk backwards down the
pipeline, and an offer cannot be conjured out of a rejection.

Sources for deviation 2: [Huntr job tracker](https://huntr.co/product/job-tracker) ·
[Teal Job Tracker guidance](https://help.tealhq.com/en/articles/9530119-job-tracker-guidance).

## Scope — what was built

- **`src/Models/ApplicationStatusTransitions.cs`** — a static
  `IsAllowed(from, to)` over a table of allowed targets, plus
  `RejectionMessage(from, to)` so the two surfaces cannot word the same refusal
  differently. No `AppDbContext`, no SQL, no HTTP.
- **`src/Modules/Applications/UpdateApplication.cs`** — consults it when
  `request.Status` is present, and returns `SliceResult.Invalid` rather than
  saving. The plan said `UpdateAsync`; there is no repository any more, so it
  went in the update slice, which is where the file's own header comment had
  already predicted it would land.
- The check runs **before any field is assigned**, so a PATCH carrying both a
  legal `Notes` change and an illegal status change writes neither. A 400 that
  meant "some of your request landed" is not something a caller can act on.
- REST returns `400 Bad Request` with `"Cannot move from Offer to Applied."`;
  GraphQL returns the identical message under `INVALID_INPUT`. Both come from the
  one handler — no second copy of the table.

### Tests

The suite is 132 green. This phase added:

- **`tests/Jobkeep.Tests/Domain/ApplicationStatusTransitionTests.cs`** — the
  suite's first and only *unit* tests, per the exception in `CLAUDE.md`: a pure
  function of two enums has no SQL, no mapping and no surface for an integration
  test to catch anything in. All **25** combinations are asserted explicitly
  rather than sampled — the table is the specification, and an omitted row is a
  rule nobody agreed to. Two further tests pin that staying put is always legal,
  and that **every enum member has a row** (a status added without deciding its
  transitions would otherwise throw `KeyNotFoundException` on PATCH — a red build
  is better than a production 500).
- **Four tests in `Parity/SurfaceParityTests.cs`** for what a unit test cannot
  see: the refusal is 400 on REST and `INVALID_INPUT` on GraphQL with the same
  message; a refused PATCH leaves the other fields unapplied; a PATCH that never
  mentions status still succeeds on a closed application; and a closed
  application can be re-opened but not straight to an `Offer`.

## Out of scope

- Recording status *history* (an audit trail of every transition + timestamp) —
  a reasonable future feature, but a new table; keep this sub-phase to enforcing
  the rule on the current single `Status` field.
- Auto-transitions driven by dates/reminders — out of scope (and reminders
  themselves are a deferred, separate feature).

## Cost

Zero, as planned — pure code, no new packages, no schema change, no migration.

## Verify locally

Rewritten against the table as agreed — the original list here asserted the
*proposed* rules, and two of its four steps are the opposite of what shipped.
There are ready-made requests at the bottom of `src/Jobkeep.http`.

- PATCH `Applied` → `Interviewing` → `Offer` → each succeeds.
- PATCH an application from `Applied` straight to `Offer` → **succeeds**
  (deviation 1).
- PATCH an `Offer` back to `Applied` → rejected with 400,
  `"Cannot move from Offer to Applied."`
- PATCH a `Rejected` application to `Interviewing` → **succeeds** (deviation 2);
  to `Offer` → rejected with 400.
- A PATCH that changes only `Notes` (no `Status`) still succeeds, whatever status
  the application is in.

## Interview talking points

- Encoding a domain invariant as a small, testable rule in the model layer
  rather than scattering `if` checks across endpoints — and *why* it lives
  outside the data layer (storage-agnostic, no DB needed to test it). It is also
  the one place in a suite of 132 integration tests where a plain unit test is
  the right call, which is a better answer than "I write unit tests".
- **Checking the comparable products before deciding how strict to be.** The
  first draft of this table made `Rejected` terminal on instinct. Huntr and Teal
  both let a user move a job out of a closed stage, so shipping a one-way door
  would have been stricter than any product in the category — and the complaint
  it generates ("I mis-clicked and now I have to delete the row") is the kind
  that gets a feature ripped out rather than tuned. The rule was loosened to the
  part that actually protects data integrity: an offer can only come from an
  active application.
- Choosing to enforce the state machine but defer status *history* — scoping a
  feature to what's needed now while noting the natural extension.

## Next

Phase 2.6 — upgrade to .NET 10 (LTS), which is a prerequisite for Phase 3.

Reminders/follow-ups and the other deferred items in `backlog.md` are candidates
to pull in here if you want another feature phase first; nothing depends on them.
