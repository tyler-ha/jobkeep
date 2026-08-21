# Backlog — feature candidates (not committed)

Parking lot for features we've *considered* but deliberately have **not**
scheduled into a phase. The point is to capture the idea (and why it's deferred)
so nothing is lost — **without** flooding the committed phase plan. A CLAUDE.md
priority is small, runnable phases; this doc is where scope goes to wait, not to
grow.

**Nothing here is a commitment.** Pull an item into a numbered phase doc only
when we actually decide to build it. Committed work lives in `phase-N-*.md` and
the README status table — not here.

Last reviewed: 2026-08-21.

## How this was sourced

From a market comparison against common job trackers (Huntr, Teal) and generic
web-app conventions. Two attributions from that analysis are **overconfident and
should be web-verified before being repeated in an interview or a doc**:
- "Teal does skill-demand analytics" — Teal's keyword feature is really
  resume-vs-one-job matching (closer to our Phase 5), *not* a
  "top skills across all tracked jobs" rollup. Treat that rollup as **our
  differentiator**, not a feature we're copying.
- "Simplify as a tracker benchmark" — Simplify is primarily an autofill/apply
  tool with a tracker attached, not a tracker-first product. Don't lean on it.

## Already covered elsewhere (not backlog — here for cross-reference)

- Filter / sort / page → **Phase 2.2** (planned)
- Dashboard / status funnel → **Phase 2.3** (planned)
- Skill-demand analytics → **Phase 2.3** (planned)
- Resume-vs-job keyword matching → **Phase 5** (ATS check)

## Deferred candidates

Ordered roughly cheapest/most-Phase-2-shaped first.

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **Soft delete / archive** | Mark rows inactive instead of hard `DELETE` so nothing is lost | Low — CRUD only, one nullable column + query filter | Could be a small Phase 2.x | No new concepts; cheap. Strongest candidate to pull in. |
| **Data export (CSV/JSON)** | Export your applications | Low — read + serialize, no schema change | Could be a small Phase 2.x | Cheap, self-contained, ends runnable. |
| **Reminders / follow-ups** | Date-based nudges ("follow up in 7 days", "interview tomorrow") | Medium — new entity + a due-date query; notifications later | Own phase (e.g. 2.5) | Flagship tracker feature. New entity = real scope. Notifications (email/push) are a *further* deferral tied to deploy. |
| **Contacts / recruiter tracking** | Log who you spoke to at each company | Medium — new `Contact` entity + relationships | Own phase | Common in Huntr. New entity. |
| **Document / resume versions** | Attach the specific resume/cover-letter version sent per application | Medium — new entity + storage decision (text now, files need blob storage → cost) | Own phase | Partially exists via `ResumeText`. File attachments would touch the cost priority (S3, etc.). |
| **Audit / activity history** | "What changed and when" — a change log per entity | Medium-High — new table + write-path change on *every* mutation | Own phase | Touches everything; don't fold into an unrelated phase. `CreatedAtUtc`/`UpdatedAtUtc` exist but aren't a log. |
| **Authentication / multi-user** | Scope all data per user; turn the tool into a real product | High — architectural, every query gets user-scoped | Own phase, tied to deploy (Phase 3+) | Deliberately *not* a Phase 2 item — would violate the small-phase priority. |

## Explicitly NOT backlog (already owned or out of character)

- **Kanban board / drag-and-drop** — frontend, belongs to **Phase 6** (the data
  behind it is the Phase 2.3 analytics + status field).
- **AI job-description analysis** — that's **Phase 4**, already planned.
- **Rate limiting / production API hygiene** — revisit at deploy (**Phase 3**),
  not before there's a real endpoint to protect.

## When we revisit

Good triggers to pull something off this list:
- A committed phase finishes early and there's appetite for a small add-on
  (soft-delete / export are the low-cost picks).
- A real need shows up while using the tool ("I keep forgetting to follow up" →
  reminders).
- Deployment (Phase 3) forces the question (auth, rate limiting).
