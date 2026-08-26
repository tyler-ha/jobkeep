# Backlog — feature candidates (not committed)

Parking lot for features we've *considered* but deliberately have **not**
scheduled into a phase. The point is to capture the idea (and why it's deferred)
so nothing is lost — **without** flooding the committed phase plan. A CLAUDE.md
priority is small, runnable phases; this doc is where scope goes to wait, not to
grow.

**Nothing here is a commitment.** Pull an item into a numbered phase doc only
when we actually decide to build it. Committed work lives in `phase-N-*.md` and
the README status table — not here.

Last reviewed: 2026-08-25.

## How this was sourced

From a market comparison against common job trackers (Huntr, Teal) and generic
web-app conventions. This section previously flagged two attributions as
**overconfident, pending web verification before being repeated in an interview
or a doc**. They were verified on 2026-08-25 — the caution was correct on both
counts, and both are now safe to state:

- **Teal does *not* do skill-demand analytics.** Verified. Teal is
  resume-first with a tracker attached, and its keyword feature is the
  **Job Matcher**: link one resume to one saved job, get a Match Score plus
  matched / missing / suggested keywords that update live as you edit. That is
  resume-vs-**one**-job — our **Phase 5**, not our Phase 2.4. "Top in-demand
  skills across **all** tracked postings" is a different question that neither
  Teal nor Huntr answers. It stays **our differentiator**.
- **Don't use Simplify as a tracker benchmark.** Still correct — it's an
  autofill/apply tool with a tracker attached, not a tracker-first product.
  **Huntr is the right comparable**: tracker-first, with a Kanban board,
  contact/recruiter CRM, Chrome-extension autofill for Workday/Greenhouse, and
  a map view. The CRM and Kanban rows below map directly onto its feature set,
  which is confirmation those rows are prioritised sensibly.

One more thing worth not overclaiming: **neither product exposes a public API
or GraphQL.** Our dual REST+GraphQL surface is a portfolio decision, not an
industry norm — say it that way.

Full market context and sources: `docs/architecture.md` section 6.

## Already covered elsewhere (not backlog — here for cross-reference)

- Filter / sort / page → **Phase 2.3** (planned)
- Dashboard / status funnel → **Phase 2.4** (planned)
- Skill-demand analytics → **Phase 2.4** (planned)
- Resume-vs-job keyword matching → **Phase 5** (ATS check)

## Deferred candidates

Ordered roughly cheapest/most-Phase-2-shaped first.

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **Soft delete / archive** | Mark rows inactive instead of hard `DELETE` so nothing is lost | Low — CRUD only, one nullable column + query filter | Could be a small Phase 2.x | No new concepts; cheap. Strongest candidate to pull in. **Gotcha found during the audit:** the unique indexes on `companies.Name` / `skills.Name` must become *filtered* unique indexes, or a soft-deleted company permanently blocks re-adding that name — and the find-or-create dedup depends on them. See [`security-and-data-audit.md`](security-and-data-audit.md) §5 step 2. |
| **Data export (CSV/JSON)** | Export your applications | Low — read + serialize, no schema change | Could be a small Phase 2.x | Cheap, self-contained, ends runnable. |
| **Reminders / follow-ups** | Date-based nudges ("follow up in 7 days", "interview tomorrow") | Medium — new entity + a due-date query; notifications later | Own phase (e.g. 2.5) | Flagship tracker feature. New entity = real scope. Notifications (email/push) are a *further* deferral tied to deploy. |
| **Contacts / recruiter tracking** | Log who you spoke to at each company | Medium — new `Contact` entity + relationships | Own phase | Common in Huntr. New entity. |
| **Document / resume versions** | Attach the specific resume/cover-letter version sent per application | Medium — new entity + storage decision (text now, files need blob storage → cost) | Own phase | Partially exists via `ResumeText`. File attachments would touch the cost priority (S3, etc.). |
| **Audit / activity history** | "What changed and when" — a change log per entity | Medium-High — new table + write-path change on *every* mutation | Own phase | Touches everything; don't fold into an unrelated phase. `CreatedAtUtc`/`UpdatedAtUtc` exist but aren't a log — and per the audit (A8) they aren't even reliable yet; fix those first. |
| **Authentication / multi-user** | Scope all data per user; turn the tool into a real product | High — architectural, every query gets user-scoped | Own phase, tied to deploy (Phase 3+) | Deliberately *not* a Phase 2 item — would violate the small-phase priority. Scoping root is decided in `architecture.md` decision 9 (`skills` stays global). |

### Added by the user-journey review (2026-08-25)

Surfaced by writing [`user-journeys.md`](user-journeys.md) — the gap between the
intended end-to-end procedure and what any doc actually commits to. Same status as
everything else here: **not a commitment.**

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **Document intake (text-only)** | A `Documents` slice: raw text + a `kind` (Resume / JobAd / CoverLetter), optionally linked to an application | Low — one table, one slice, **no blob storage** | Own small phase, before Phase 4 | The largest journey gap: **nothing in any doc uploads or parses a file.** Phase 4 says "*paste* a job description in"; Phase 5 says "store your resume text once". Text-only keeps the near-zero-cost priority intact and also de-duplicates `ResumeText`, which is finding F2. PDF/DOCX parsing stays deferred. |
| **AI-extracted requirements** | Extend Phase 4's prompt to write `job_requirements`, not just `posting_skills` | Low — prompt + inserts, table already exists | Fold into Phase 4 | Phase 4 extracts skills, seniority and a summary but never requirements — yet `job_requirements` exists *"for the Phase 5 ATS check"*. The AI phase currently feeds only half of the phase that depends on it. |
| **Provenance on `job_requirements`** | A `Source` column mirroring `posting_skills.Source` (Parsed / AiExtracted) | Low — one column | With the row above | No way to tell a requirement you typed from one a model guessed. Harmless today, load-bearing the moment the row above ships. **Not recorded anywhere else**, including the audit. |
| **Target profile** | Store the roles / seniority / skills you are aiming at | Medium — new entity + one analytics join | Own phase, after 2.3 | Upgrades the differentiator: Phase 2.4 answers *"what is in demand?"*; with a target it answers **"what is in demand that I do not have yet?"** — the question a job seeker actually has. |
| **Interview rounds** | Round number, date, outcome, who you spoke to | Medium — new entity | Own phase | `Interviewing` is one enum value. "Second round Thursday" has nowhere to live but free-text `Notes`, which no query can use. Related: status history is scoped out of 2.4 (F16), and the audit lists interview rounds as missing against tracker convention. |

### Added by the security & data audit (2026-08-25)

Recorded because they were **absent from every document**, not deferred. Full
evidence in [`security-and-data-audit.md`](security-and-data-audit.md).

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **Audit & integrity baseline** | Interceptor-maintained timestamps, DB-side defaults, CHECK constraints, `xmin` concurrency token, bounded text, two missing indexes | Low — one migration + one interceptor, no auth needed | Small Phase 2.7 | The cheapest real fix on this list, and it corrects a column that is already wrong (A8). Best interview story in the audit. |
| **Transport & secrets hardening** | `SSL Mode=VerifyFull`, RDS storage encryption, untrack `appsettings.Development.json`, connection string in SSM Parameter Store (free tier) | Low — config only, no schema | **Phase 3** | RDS storage encryption can only be enabled *at instance creation* — decide before the instance exists. |
| **PII classification & retention** | Identify `ResumeText` / `Notes` / `Description` as personal information; decide whether they leave the machine once Phase 4 swaps off Ollama; retention rule per Privacy Act APP 11.2 | Low as a doc, Medium if purge is automated | Phase 4/5 guardrail | The one item here with an external obligation attached, not just good practice. |
| **schema.org `JobPosting` gaps** | `validThrough` (expiry), `jobLocationType` (remote/hybrid), `identifier` (employer req id), source/channel | Low — four columns on `job_postings` | **Unowned** — 2.1 is Done; fold into 2.2 or its own small phase | Remote/hybrid is the most-filtered attribute in the current market and free-text `Location` cannot answer it. |

### Added by the .NET 10 upgrade (2026-08-26)

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **HotChocolate 14 → 16** | The GraphQL server is on the tail of the 14 line (14.3.1); 16.6.x is current | Medium — two majors of breaking changes | **Unowned** | Deliberately *not* done in Phase 2.6. The only thing forcing a move then was [GHSA-qr3m-xw4c-jqw3](https://github.com/advisories/GHSA-qr3m-xw4c-jqw3), and 14.3.1 patches it, so the 14 line is secure and supported for now. Pull this forward if a second advisory lands on 14, or if Phase 4/5 wants something only 15+ ships. Doing it inside a framework bump would have made any failure ambiguous. |
| **GraphQL parse-depth limit** | A document-size / nesting guard in front of `/graphql` | Low | **Phase 3** (with rate limiting) | The advisory above is the argument: the parser runs *before* validation, so `MaxExecutionDepth` cannot protect it, and `StackOverflowException` is uncatchable. Patching HotChocolate fixed *this* parser bug; it did not give the app a way to reject an absurd document. Belongs with the rest of the deploy-time API hygiene. |


## Convention / industry-standard adoptions (committed intent, unscheduled)

Unlike the feature candidates above, these are **not** "maybe" — they're
standing decisions to bring the codebase in line with common industry practice,
deliberately deferred so they don't disrupt the current phase's runnable scope.
The motivation is partly the STAR log: being able to speak to *why* a mainstream
pattern was adopted (and the tradeoff vs. what was there before) is stronger
interview material than a bespoke choice. Pull each into a numbered phase when
its timing is right; when we do, **we apply it fully**, not half-way.

| Adoption | From → To | Why deferred | STAR angle |
|---|---|---|---|
| ~~**Automated tests**~~ **DONE (Phase 2.2)** | No test project → xUnit + Testcontainers (real Postgres in Docker) | Not deferred for a good reason — this is simply the largest gap in the project. **Should be scheduled as its own phase, ahead of further architecture work.** Named in essentially every Melbourne .NET ad reviewed. | "Tested the EF mapping and find-or-create dedup against a real Postgres in a container, because a fake repository would have passed while the actual SQL was wrong — and that dedup is the feature the whole storage choice rests on." |
| ~~**CI/CD**~~ **DONE (Phase 2.2)** | Manual local build → GitHub Actions (build + test on push) | Depends on tests existing to be worth much. Pair it with the testing phase. Free for public repos. | "Set up the pipeline early so a broken build was visible immediately rather than discovered at deploy time." |
| **Response DTOs** | Endpoints/resolvers return EF entities → explicit response records | Cheap and worth folding into Phase 2.3 rather than doing standalone. Removes the `ReferenceHandler.IgnoreCycles` band-aid at the same time. | "The serializer needed a cycle-handling flag, which was the clue that I was leaking my database schema out as my API contract." |
| **docker-compose** | Manual `docker run` in the README → one `compose.yaml` | Trivial; slot into any phase. Docker appears on nearly every ad. | Minor on its own, but it makes the quick-start a single command. |
| ~~**MVC Controllers**~~ | ~~Minimal-API endpoint files → attribute-routed controllers~~ | **Proposed for retirement (2026-08-25)** — see below. | — |

### On the MVC controllers adoption

This row was committed on the reasoning that attribute-routed controllers are
"the convention most teams use". That reasoning no longer holds up:

- Controllers organise code by **technical layer** — a controller class
  collects every action for a resource. That cuts directly across vertical
  slices, where one file owns one use case end to end. Adopting both means
  fighting one with the other.
- The premise is weaker than it looked. Minimal APIs are not the niche option
  in .NET 8+; grouped minimal-API endpoints are thoroughly mainstream, and the
  `Endpoints/` split already demonstrates route organisation.

**Recommendation: retire this adoption.** It's flagged rather than deleted
because it was a deliberate commitment — see decision 7 in
`docs/architecture.md`, status *Proposed*. Confirm or overturn it there.

The STAR angle survives either way, and is arguably better as a reversal:
*"I committed to adopting controllers because they were the familiar
convention, then dropped it when I picked a slice-based structure they'd have
worked against — and I can explain the tradeoff in both directions."*

## Explicitly NOT backlog (already owned or out of character)

- **Kanban board / drag-and-drop** — frontend, belongs to **Phase 6** (the data
  behind it is the Phase 2.4 analytics + status field).
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
