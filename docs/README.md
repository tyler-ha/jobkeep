# Docs

Three kinds of document live here, and they answer different questions. When two
disagree, the order below is the precedence order.

## Standing records — kept current

| Doc | Answers |
|---|---|
| [`architecture.md`](architecture.md) | **How the code is shaped and why.** The decision record, the known-problems table, the gap register, and the verified market comparison. *The authority — read it before proposing structural changes.* |
| [`security-and-data-audit.md`](security-and-data-audit.md) | What the schema and config expose, what's missing, and the phased remediation plan (F1–F18). |
| [`user-journeys.md`](user-journeys.md) | **What the user actually does**, step by step, and where that procedure has holes. The counterpart to `architecture.md`: that one describes the system from the code's side, this one from the user's. |
| [`backlog.md`](backlog.md) | Considered-but-not-committed features. |
| [`token-log.md`](token-log.md) | What each phase cost to build, in tokens. Generated from session transcripts by `scripts/token-usage.py`. |

## Phase plans — a record of intent, then of what happened

[`phases/`](phases/) holds one doc per build phase, in order. Each carries a
**Status**, the plan, the cost note, and — once built — the deviations from that
plan. They are written *before* the work and corrected *after* it, so an old
phase doc is a historical record, not a description of the current tree.

| Phase | | Status |
|---|---|---|
| 1 | [Local API, in-memory storage](phases/phase-1-local-api.md) | Done |
| 2 | [Relational model on PostgreSQL + GraphQL](phases/phase-2-postgres.md) | Done |
| 2.1 | [Complete the write surface](phases/phase-2.1-write-surface.md) | Done |
| 2.2 | [Automated tests + CI](phases/phase-2.2-tests-and-ci.md) | Done |
| 2.3 | [Query, filter, sort & page the list](phases/phase-2.3-list-queries.md) | Done |
| 2.4 | [Analytics endpoints](phases/phase-2.4-analytics.md) | Done |
| 2.5 | [Enforce the status lifecycle](phases/phase-2.5-status-rules.md) | Done |
| 2.6 | [Upgrade to .NET 10 (LTS)](phases/phase-2.6-dotnet10-upgrade.md) | Done |
| 3 | [Deploy to AWS Lambda (Function URL)](phases/phase-3-aws-deploy.md) | **Parked** — plan done, $0/month, deferred behind local feature work |
| 4 | [AI job-description analyzer](phases/phase-4-ai-analyzer.md) | **Done** — tests verified during Phase 4.5, all 10 passed unchanged |
| 4.5 | [Document import: upload, parse, confirm, save](phases/phase-4.5-resume-import.md) | **Done** — PDF/DOCX/text → draft → human confirm → rows |
| 5 | [ATS compatibility check](phases/phase-5-ats-check.md) | **Done** — skill gap is a SQL join, not a model call; degrades when the model is down |
| 6 | [Front end](phases/phase-6-frontend.md) | Design approved, build not started |

**If a phase doc contradicts `architecture.md`, follow `architecture.md` and fix
the phase doc as part of the work.** The phase docs were written before the
architecture record existed.

## Diagrams — committed artefacts

[`diagrams/`](diagrams/) holds `schema-erd.svg` and `architecture.svg`, embedded
in the root `README.md` and in `architecture.md`.

**These go stale silently** — nothing fails a build when the schema moves and the
picture doesn't. Redraw them with the `schema-diagram` skill in the *same change*
that moves the schema. That skill derives the schema from
`dotnet ef migrations script` rather than from `Models/*.cs`, because column
types, precision, delete behaviour and index uniqueness live in Fluent API config
and the Npgsql provider — inferring them from the model classes produces a
diagram that is wrong in exactly the places an interviewer would probe.
