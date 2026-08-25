# User journeys — what someone actually does with JobKeep

**Last reviewed: 2026-08-25.**

Every other doc describes the system from the code's side: modules, tables, phases.
This one describes it from the user's side — the procedure, start to finish — and
records where that procedure has holes.

It exists because the journey was only reconstructable by reading eight phase docs
side by side, and doing that surfaced a gap worth naming up front:

> **The intended journey is wider than anything the docs commit to.** The mental
> model is "add a job post, upload a document, turn it into skills / requirements /
> targets / interviews." Three of those five nouns have nowhere to live today, and
> **nothing in any doc uploads or parses a file.**

`architecture.md` still wins on how the code is shaped. This doc owns *what the user
does*; `backlog.md` owns *what we have decided not to build yet*.

---

## The journey, step by step

| # | Step | State | Where it lives |
|---|---|---|---|
| 1 | Record a job you found — company, title, location, the ad text, the link | **Built** | `POST /applications` |
| 2 | Company is deduplicated by name automatically | **Built** | find-or-create on `companies.Name` |
| 3 | Tag the posting with skills (must-have vs nice-to-have) | **Built** | `POST /applications/{id}/skills` |
| 4 | Record structured requirements from the ad | **Built** | `POST /applications/{id}/requirements` |
| 5 | Review and update as things move — status, notes | **Built** | `PATCH /applications/{id}` |
| 6 | Find things again — filter by status, company, skill, date; sort; page | Planned | Phase 2.3 |
| 7 | Ask what it all adds up to — skill demand, status funnel | Planned | Phase 2.4 |
| 8 | Be stopped from recording an impossible move (Offer → Applied) | Planned | Phase 2.5 |
| 9 | Store your résumé once | Planned, thinly | Phase 5, step 1 |
| 10 | Paste an ad and have skills + seniority + a summary extracted | Planned | Phase 4 |
| 11 | Compare résumé against a posting — matched vs missing keywords | Planned | Phase 5 |
| 12 | See it all in a UI | Planned | Phase 6 |
| — | **Upload a CV or job ad as a file** | **Nowhere** | — |
| — | **Track interview rounds** | **Nowhere** | — |
| — | **Say what you are aiming at** | **Nowhere** | — |
| — | **Be reminded to follow up** | Backlog only | `backlog.md` #3 |

Steps 1–5 are the loop the tool already supports end to end, over both REST and
GraphQL. Everything from 6 down is a promise.

---

## What the journey is missing

Ordered cheapest-first, which is roughly also most-useful-first.

### 1. There is no document intake — anywhere

This is the largest gap between the intended journey and the recorded one.

- Phase 4's input is *"**paste** a job description in"*, reading `job_postings.Description`.
- Phase 5's is *"add a way to store your resume text once (a simple field or small
  endpoint — this is a single-user tool, no need to overbuild this)"*, backed by
  `job_applications.ResumeText`.
- The only mention of PDF in the entire repo is a static advice string to be returned
  to the user — *"avoid tables/columns if uploading as PDF"* — not a parser.
- File attachments appear once, in `backlog.md` #5, deferred on cost grounds:
  *"text now, files need blob storage → cost"*.

So "upload a doc, then convert it" currently means "paste text into a JSON field, on
the one entity that happens to have a column for it."

**The cheap version that closes it.** A `Documents` slice storing raw text with a
`kind` (`Resume` / `JobAd` / `CoverLetter`) and an optional link to an application:

- `POST /documents` — text in, id back.
- Extraction (Phase 4) reads from a document instead of only from
  `job_postings.Description`.
- Résumé text stops being duplicated per application, which is also finding **F2** in
  the security audit: *"an unbounded `text` column holding a full résumé… duplicated
  per application."*

This needs **no blob storage and no S3**, so the near-zero-cost priority holds. Actual
file parsing (PDF/DOCX → text) can stay backlogged, and the client can do the
extraction and post text in the meantime. The point is that the *shape* — a document
is a thing you keep, separate from one application — is right either way.

### 2. Phase 4 extracts skills but never requirements

Phase 4 writes an `ai_analyses` row plus `posting_skills` rows with
`Source = AiExtracted`. It does not touch `job_requirements`.

But `job_requirements` exists, per the Phase 2 table, as *"structured requirements for
the Phase 5 ATS check"* — and Phase 5 is the phase that compares a résumé against
them. So the AI phase feeds only half of the phase that depends on it, and the other
half stays hand-typed forever.

Extending Phase 4's prompt to return requirements alongside skills is a prompt change
and a handful of inserts, against a table that already exists.

### 3. `job_requirements` cannot record where it came from

`posting_skills` has `Source` (`Parsed` / `AiExtracted`). `job_requirements` has no
equivalent, so there is no way to tell a requirement you typed from one a model
guessed.

That is fine today, because everything is hand-entered. It becomes load-bearing the
moment gap 2 is built — and "which of these did the AI invent?" is precisely the
question you want to be able to ask of an extracted list.

One column, mirroring a pattern already in the schema. **Not currently recorded
anywhere**, including the security and data audit.

### 4. Interviews are not a thing

`Interviewing` is one value of one enum on the application. There is no place for:

- which round you are on,
- when it is,
- who you spoke to,
- how it went.

The audit's §2 already lists *"status **history**, interview rounds, contacts, a
next-action date, document versions"* as missing against tracker convention, and
Phase 2.5 explicitly scopes status history out (**F16**).

"Second round Thursday" is close to the single most common thing a person opens a job
tracker to write down. Today it goes in the free-text `Notes` field, which no query
can ever use.

### 5. Targets are not a thing

Nothing records what you are *aiming at* — the roles, the seniority, the skills you
want to be hired for.

This is the sharpest of the gaps, because it upgrades a feature the project already
claims as its differentiator. Phase 2.4 answers *"what skills are in demand across the
jobs I am tracking?"* With a target profile, the same `GROUP BY` answers:

> **"What is in demand that I do not have yet?"**

That is the question a job seeker actually has. `architecture.md` §6 notes that
skill-demand-across-all-postings is a question *"neither Huntr nor Teal answers"* —
this makes the answer actionable rather than merely interesting.

### 6. You cannot ask where a posting came from, or whether it is remote

Four columns absent from `job_postings`, recorded as **F17** and `backlog.md` #11:

| Missing | Why it matters |
|---|---|
| `jobLocationType` (remote / hybrid / onsite) | *"The most-filtered attribute in the current market"* — and free-text `Location` cannot answer it |
| source / channel (Seek, LinkedIn, referral) | F17: *"an analytics question Phase 2.4 will want and cannot currently ask"* |
| `validThrough` (expiry) | Distinguishes "no reply" from "the ad closed" |
| `identifier` (employer requisition id) | A natural key for de-duplicating the same role seen on two boards |

All four are schema.org `JobPosting` fields, so there is a standard to copy rather
than a shape to invent. `backlog.md` homes them in "Phase 2.1", which is already Done
— they are currently unowned.

### 7. No next action, no reminders

`backlog.md` #3, and the audit's "next-action date". A tracker that cannot tell you
what to do today is a filing cabinet. This is the flagship feature of the Huntr
comparable.

---

## One thing not to overclaim

The differentiator is **skill demand across all tracked postings** — and that is worth
stating precisely, because it is easy to overstate:

- **Teal's Job Matcher** is résumé-vs-**one**-job: a match score plus matched, missing
  and suggested keywords. That is **our Phase 5**, not our Phase 2.4.
- **Huntr** is the right tracker comparable — Kanban, contact CRM, autofill, map view.
- *"Top in-demand skills across **all** tracked postings"* is a different question, and
  **neither product answers it**.
- Neither product exposes a public API or GraphQL. Our dual surface is a **portfolio**
  decision, not an industry norm — say it that way.

---

## Where these go next

Gaps 1, 2, 3 and 5 are new; they are being added to `backlog.md` so the two documents
cannot drift. Gaps 4, 6 and 7 are already recorded there or in the audit and are
cross-referenced rather than duplicated.

Nothing in this document is scheduled. It is a description of the journey and its
holes, not a plan — a phase doc is where a commitment gets made.
