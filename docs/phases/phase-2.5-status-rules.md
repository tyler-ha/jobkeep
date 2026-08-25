# Phase 2.5 — Enforce the application status lifecycle

**Status: Not started**

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

## Proposed lifecycle

```
Applied      → Interviewing | Rejected | Withdrawn
Interviewing → Offer | Rejected | Withdrawn
Offer        → Rejected | Withdrawn        (rejected = declined/rescinded)
Rejected     → (terminal)
Withdrawn    → (terminal)
```

Same-status "transitions" (no-op PATCH that doesn't touch status) stay allowed.
Confirm this table matches how you actually want to track before implementing —
it's a business decision, not a technical one.

## Scope

- A pure domain rule in `src/Models/` (e.g. a static
  `ApplicationStatusTransitions.IsAllowed(from, to)`), deliberately kept out of
  the repository so it's storage-agnostic and unit-testable without a database.
- `UpdateAsync` consults it when `update.Status` is present and the value
  differs from the current status; on an illegal transition it surfaces a
  validation failure rather than saving.
- REST returns `400 Bad Request` with a message like
  `"Cannot move from Offer to Applied."`; GraphQL surfaces the same as a
  resolver error.

## Out of scope

- Recording status *history* (an audit trail of every transition + timestamp) —
  a reasonable future feature, but a new table; keep this sub-phase to enforcing
  the rule on the current single `Status` field.
- Auto-transitions driven by dates/reminders — out of scope (and reminders
  themselves are a deferred, separate feature).

## Cost

Zero — pure code, no new packages, no schema change.

## Verify locally

- PATCH an application from `Applied` to `Offer` → rejected with 400.
- PATCH `Applied` → `Interviewing` → `Offer` → each succeeds.
- PATCH a `Rejected` application to anything → rejected (terminal).
- A PATCH that changes only `Notes` (no `Status`) still succeeds.

## Interview talking points

- Encoding a domain invariant as a small, testable rule in the model layer
  rather than scattering `if` checks across endpoints — and *why* it lives
  outside the repository (storage-agnostic, no DB needed to test it).
- Choosing to enforce the state machine but defer status *history* — scoping a
  feature to what's needed now while noting the natural extension.

## Next

Revisit deferred items — optionally Phase 2.6 (reminders/follow-ups), otherwise
Phase 3 (deploy to AWS Lambda + API Gateway + RDS).
