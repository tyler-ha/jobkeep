# Backlog — feature candidates (not committed)

Parking lot for features we've *considered* but deliberately have **not**
scheduled into a phase. The point is to capture the idea (and why it's deferred)
so nothing is lost — **without** flooding the committed phase plan. A CLAUDE.md
priority is small, runnable phases; this doc is where scope goes to wait, not to
grow.

> **2026-09-04 — read every "Phase 10" below as "the deploy that replaces it".**
> The AWS deploy was **dropped** (`architecture.md` decision 22); a free host is
> still to be chosen. The *triggers* are unchanged — these findings still come due
> when the API first becomes reachable — but any **AWS-specific mitigation named
> below is moot** (RDS `StorageEncrypted`, SSM Parameter Store, the Lambda
> specifics); its replacement is chosen with the host. **Phase 11 (auth) also moved
> to last** on the roadmap, keeping its number.

**Nothing here is a commitment.** Pull an item into a numbered phase doc only
when we actually decide to build it. Committed work lives in `phase-N-*.md` and
the README status table — not here.

Last reviewed: **2026-09-01** — every row was given a phase home and a **P1–P4**
band. The bands come from one test, recorded as `architecture.md` decision 18:
**does deferring this make the later work bigger?** P1 passes it and is scheduled
now; P2 is gated on the deploy; P3 is flat-cost feature work that waits without
penalty; P4 stays here, recorded and unscheduled. Most of this list is P3 or P4,
and that is the finding — appeal and urgency are not the same axis.

Previously reviewed 2026-08-28 (real-CV test; see the 2026-08-28 section).

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
| **Soft delete / archive** — ~~candidate~~ **BUILT, Phase 8, 2026-09-04** | Mark rows inactive instead of hard `DELETE` so nothing is lost | Low — CRUD only, one nullable column + query filter | **Phase 8 (P1)** | No new concepts; cheap on the backend, **wide on the front** — five list routes, five empty states, an undo. Scheduled because that front-end cost grows per screen. **Gotcha found during the audit:** the unique indexes on `companies.Name` / `skills.Name` must become *filtered* unique indexes, or a soft-deleted company permanently blocks re-adding that name — and the find-or-create dedup depends on them. See [`security-and-data-audit.md`](security-and-data-audit.md) §5 step 2. **BOTH ESTIMATES IN THIS ROW WERE WRONG, and the row is kept as written so the misses stay legible.** The front-end cost was *not* wide: **one screen changed, not five**, because there were no delete affordances to convert — `deleteApplication` was exported and called by nothing — and the global query filter covers the other four list routes for free. And the gotcha named the wrong tables: `companies` and `skills` have no delete path, so **only `resumes.LabelNormalized`** needed the filtered index. The real cost was in two places this row does not mention: three raw-SQL analytics views `HasQueryFilter` cannot reach, and the decision to stop publishing the 13.3c delete notifications. See [`phase-8-soft-delete.md`](phases/phase-8-soft-delete.md). |
| **Data export (CSV/JSON)** | Export your applications | Low — read + serialize, no schema change | **Phase 12 (P3)** | Cheap, self-contained, ends runnable. The cheapest real feature on the list now a UI exists — and flat-cost, so there is no penalty for waiting. |
| **Reminders / follow-ups** | Date-based nudges ("follow up in 7 days", "interview tomorrow") | Medium — new entity + a due-date query; notifications later | **Phase 12 (P3)** | Flagship tracker feature, and it gives the Today screen its reason to exist. New entity = real scope. Notifications (email/push) are a *further* deferral tied to deploy. |
| **Contacts / recruiter tracking** | Log who you spoke to at each company | Medium — new `Contact` entity + relationships | **Phase 12 (P3)** | Common in Huntr. New entity, and the first genuinely new *screen* — a ninth, plus a presence on Job post. |
| **Keep the uploaded file itself** | Store the original PDF/DOCX, not just the text extracted from it | Medium — a storage decision with a bill attached | **P4 — recorded, not scheduled** | **Deferred deliberately in Phase 4.5, at the user's request** (*"For now, no saving documents yet. We will have it in the backlog."*). Today the bytes are read, converted to text, hashed for provenance and dropped. Bringing them back means either `bytea` in Postgres — which eats Neon's free-tier 0.5 GB, the only genuinely scarce resource in the deployed plan — or S3, a new AWS surface. It would also reintroduce the filename/path-handling risk that currently **cannot** occur, since nothing is written to disk. Worth doing when there is a reason to re-download the exact file that was sent, not before. |
| **Document / resume versions** | Attach the specific resume/cover-letter version sent per application | Low now — the hard part shipped | **Phase 12 (P3)** | **Mostly done by Phase 4.5**: `resumes` is a labelled aggregate and `job_applications.ResumeId` points at the version used. What remains is cover letters, and the file attachment covered by the row above. |
| **Audit / activity history** | "What changed and when" — a change log per entity | Medium-High — new table + write-path change on *every* mutation | **P4 — after Phase 11** | Touches everything; don't fold into an unrelated phase. Blocked on auth anyway: F9 needs an actor to name. Note Phase 7's interceptor is the write-path hook this would extend, so it gets *cheaper* by waiting, not dearer. `CreatedAtUtc`/`UpdatedAtUtc` exist but aren't a log — and per the audit (A8) they aren't even reliable yet; fix those first. |
| **Authentication / multi-user** | Scope all data per user; turn the tool into a real product | High — architectural, every query gets user-scoped | **Phase 11 (P2)** | Deliberately *not* a Phase 2 item — would violate the small-phase priority. Scoping root is decided in `architecture.md` decision 9 (`skills` stays global), **status still *Proposed* — confirm it before building**. The largest compounder on the roadmap, placed fourth on measurement: linear in slice count, near-flat on the front end. |

### Added by the user-journey review (2026-08-25)

Surfaced by writing [`user-journeys.md`](user-journeys.md) — the gap between the
intended end-to-end procedure and what any doc actually commits to. Same status as
everything else here: **not a commitment.**

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| ~~**Document intake (text-only)**~~ | — | — | — | **DONE, and larger than this row imagined — Phase 4.5.** It did not stay text-only: PDF and DOCX parsing shipped too (PdfPig, OpenXml), along with a human confirm-and-fix step before anything is written. `ResumeText` was not de-duplicated but deleted — the résumé moved to its own `resumes` table. See `docs/phases/phase-4.5-resume-import.md`. |
| **AI-extracted requirements** | Extend Phase 4's prompt to write `job_requirements`, not just `posting_skills` | Low — prompt + inserts, table already exists | **Phase 12 (P3)** | Phase 4 extracts skills, seniority and a summary but never requirements — yet `job_requirements` exists *"for the Phase 5 ATS check"*. The AI phase currently feeds only half of the phase that depends on it. |
| **Provenance on `job_requirements`** | A `Source` column mirroring `posting_skills.Source` (Parsed / AiExtracted) | Low — one column | **Phase 12 (P3)**, with the row above | No way to tell a requirement you typed from one a model guessed. Harmless today, load-bearing the moment the row above ships. **Not recorded anywhere else**, including the audit. |
| **Target profile** | Store the roles / seniority / skills you are aiming at | Medium — new entity + one analytics join | **Phase 12 (P3)** | Upgrades the differentiator: Phase 2.4 answers *"what is in demand?"*; with a target it answers **"what is in demand that I do not have yet?"** — the question a job seeker actually has. |
| **Interview rounds** | Round number, date, outcome, who you spoke to | Medium — new entity | **Phase 12 (P3)** | Reshapes the Pipeline board: `Interviewing` is one column today, and rounds make it a column with depth, so the drag semantics have to answer what moving between rounds means. `Interviewing` is one enum value. "Second round Thursday" has nowhere to live but free-text `Notes`, which no query can use. Related: status history is scoped out of 2.4 (F16), and the audit lists interview rounds as missing against tracker convention. |

### Added by the security & data audit (2026-08-25)

Recorded because they were **absent from every document**, not deferred. Full
evidence in [`security-and-data-audit.md`](security-and-data-audit.md).

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **Audit & integrity baseline** | Interceptor-maintained timestamps, DB-side defaults, CHECK constraints, `xmin` concurrency token, bounded text, two missing indexes | Low — one migration + one interceptor, no auth needed | **Phase 7 (P1) — next** | The cheapest real fix on this list, and it corrects a column that is already wrong (A8). Best interview story in the audit. **Scheduled first because it is the only item here whose cost grows while it waits** — per write path (F8) and per row of duplicate data. |
| **Transport & secrets hardening** | `SSL Mode=VerifyFull`, encryption at rest, untrack `appsettings.Development.json`, connection string in SSM Parameter Store (free tier) | Low — config only, no schema | **Phase 10 (P2)** | Was written when the deploy targeted RDS, where storage encryption can only be enabled *at instance creation*. Phase 10 now uses Neon, which encrypts at rest and enforces TLS by default — so the deadline is gone, but the config items remain. |
| **PII classification & retention** | Identify `ResumeText` / `Notes` / `Description` as personal information; decide whether they leave the machine once Phase 4 swaps off Ollama; retention rule per Privacy Act APP 11.2 | Low as a doc, Medium if purge is automated | **Phase 10 (P2)** | The one item here with an external obligation attached, not just good practice. |
| **schema.org `JobPosting` gaps** | `validThrough` (expiry), `jobLocationType` (remote/hybrid), `identifier` (employer req id), source/channel | Low — four columns on `job_postings` | **Phase 12 (P3)** | Remote/hybrid is the most-filtered attribute in the current market and free-text `Location` cannot answer it — so of the four columns this is the one that earns a filter on Applications and a field on Job post. |

### Added by the .NET 10 upgrade (2026-08-26)

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **HotChocolate 14 → 16** | The GraphQL server is on the tail of the 14 line (14.3.1); 16.6.x is current | Medium — two majors of breaking changes | **P4 — recorded, not scheduled** | Deliberately *not* done in Phase 2.6. The only thing forcing a move then was [GHSA-qr3m-xw4c-jqw3](https://github.com/advisories/GHSA-qr3m-xw4c-jqw3), and 14.3.1 patches it, so the 14 line is secure and supported for now. Pull this forward if a second advisory lands on 14, or if Phase 4/5 wants something only 15+ ships. Doing it inside a framework bump would have made any failure ambiguous. |
| **GraphQL parse-depth limit** | A document-size / nesting guard in front of `/graphql` | Low | **Phase 10 (P2)** (with rate limiting) | The advisory above is the argument: the parser runs *before* validation, so `MaxExecutionDepth` cannot protect it, and `StackOverflowException` is uncatchable. Patching HotChocolate fixed *this* parser bug; it did not give the app a way to reject an absurd document. Belongs with the rest of the deploy-time API hygiene. |


### Added by the real-CV test (2026-08-28)

Phase 4.5 was tested by uploading two real CVs of the same person — one exported
to PDF from a heavily designed template, one an ordinary Word document. The two
results were so far apart that the gap is the finding, and it is what these
entries are about.

**The headline number, same person, same model, same prompt:**

| | PDF (designed, sidebar layout) | DOCX (ordinary, linear) |
|---|---|---|
| Full name | ✗ lost | ✓ |
| Location | ✗ null | ✓ `Murrumbeena 3163 VIC` |
| Skills | 22, mostly right, after a rewrite; **4 and all wrong** before it | ✓ **8, exactly the technical-skills list** |
| Date ranges | detached column, model recovers most | ✓ clean |
| Employer / title | unreliable | ✓ correct |

The extractor rewrite (`bd624d8`) closed the worst of the PDF gap — reading order
is now reconstructed from glyph geometry rather than taken from the content
stream. What is left is deferred here rather than fixed, and the DOCX column is
the reason: **the format is doing more of the work than the parser is.**

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **Letter-spaced heading recovery** | A heading tracked out for effect (`M a s t e r  o f  I T`) has letter gaps as wide as word gaps, so the word extractor splits it into single characters | Medium — needs a per-font width heuristic, and a wrong one damages ordinary text | **P4** | This is what costs the full name on a designed PDF. Nothing in the geometry distinguishes tracking from word spacing; the fix is statistical (compare the gap against the median intra-word gap for that font size) and can regress documents that currently work. Not worth it for one field the user types anyway. |
| **Detached date columns** | Dates in their own narrow column segment as their own block and arrive separated from the entries they belong to | Medium — a column-association pass over blocks | **P4** | Partly self-correcting: the model reassociates most of them once blocks carry structure. Would matter more if the draft were ever committed without review, which the confirm gate is specifically designed to prevent. |
| **Employer / title pairing across a sidebar** | Which line is the employer and which the role, when both columns supply candidates | Medium | **P4** | Same shape as above and the same mitigation — the review screen exists for exactly this. |
| **OCR for scanned PDFs** | A scanned CV is a picture; it opens fine and yields nothing | High — Tesseract or a hosted vision model, plus a real latency and cost story | **P4** | Already detected and reported rather than silently stored empty. A different project, and the only item here that is a capability rather than a refinement. |

**The recommendation that falls out, and it is worth stating in an interview:**
tell the user to upload a `.docx` when they have one. Not as an apology for the
parser — a Word file *carries* its structure (paragraphs, tables, lists) while a
PDF has thrown it away and left coordinates, so the DOCX path is reconstructing
nothing. The measured difference above is that argument with numbers on it. The
PDF path exists because people do not always have the original.

**Libraries were reviewed at the same time and deliberately not changed.**
`PdfPig` 0.1.16 is the current stable (0.1.17 is alpha) and is the best free
option in .NET — the alternatives are AGPL (iText 7, copyleft, wrong for a
portfolio repo), page-capped free editions (Free Spire), or commercial
(IronPDF, Aspose, Syncfusion), and all of them fail the near-zero-cost priority.
`DocumentFormat.OpenXml` 3.5.1 is Microsoft's own SDK and is already current.
Neither of the defects found was a library gap; both were about preserving
structure the library already exposes. One genuine capability gap exists —
legacy `.doc`, which `NPOI.HWPF` could parse — and it is not taken: that package
is a port of Apache POI's *scratchpad* module, has not moved in years, and the
format has been superseded since 2007. Refusing `.doc` with a message telling the
user to re-save is the defensible call, and is what ships.


### Added by the Phase 6.5 upload design (2026-09-01)

Surfaced while designing the upload experience. Both were **absent from every
document** before this — the first was asked for by the user and refused with
reasons, the second is a live inconsistency the front end walked into.

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **Scrape a job ad from its URL** | Paste a Seek / LinkedIn / Indeed link and have the app fetch the ad itself | High, and the cost is structural rather than in code | **NOT SCHEDULED — refused, see notes** | **Asked for by the user in Phase 6.5 and declined with reasons, not deferred for time.** All three boards serve an ad as a JS shell behind bot protection, so it needs either a headless browser (Playwright, ~300 MB, needs a container image — which **breaks the Phase 10 Lambda-behind-a-Function-URL deploy**) or a paid scraping API ($30-150/month — which **breaks priority 1 outright**). All three sites' terms prohibit automated collection, and that matters more than usual here because this repo is a portfolio: it is the one feature where "I built it" is a liability unless the ToS position can be defended out loud. `hiQ v. LinkedIn` is narrower than its reputation — the CFAA claim failed, but LinkedIn won on breach of contract. Scrapers also break silently on a redesign. **What ships instead:** paste the ad's text (Phase 6.5 group 4), with `job_postings.SourceUrl` (varchar 2000, already exists) and the upload form's "Link to the ad" input keeping the link as provenance. Reopen only if a board ships a public ads API. **See the browser-extension section below (2026-09-01): it is a DIFFERENT mechanism, and this refusal's own objections are the argument for it.** |
| **A description input on the app forms** | Somewhere to type or paste a job description onto an application that already exists | Low — one `textarea`, the wire already accepts it | **Phase 12 (P3)** | A live inconsistency, not a missing feature: `JobPost.tsx:226` renders *"No description was saved with this one. Paste the ad in and the analyser has something to read"* — and **no screen anywhere exposes a description input**, though `CreateApplicationRequest.description` and the `PATCH` both accept one. So the app invites an action it does not offer. The user was shown this in Phase 6.5 and chose **not** to close it there, because the paste-an-ad path (group 4) covers the common case of a job that has not been created yet; this row is the remaining case, an application added by hand and enriched later. Cheap whenever it is picked up. |


### Added by the intake question (2026-09-01)

Raised by the user, in the session that finished Phase 13.2e:

> *"We are missing the aspect that where can we get those data for job ad."*

**This is the largest unaddressed gap in the product, and it is not a feature gap.**
Every capability the app has — the analyser, the ATS check, skill-demand analytics,
the requirement extractor — reads `job_postings.Description`. All of them assume an ad
is already in the database. Nothing in the app is good at putting one there:

| Path | State | Why it does not solve intake |
|---|---|---|
| Upload a document | Built (Phase 4.5) | Aimed at a **CV**. Nobody has their job ads as files. |
| Paste the ad's text | **Parked** (Phase 6.5 group 4) | Works, and is manual: switch tab, select all, copy, switch back, paste. |
| Type a description | Not built (row above, P3) | Manual and worse. |
| Fetch the ad from its URL | **Refused**, row above | Server-side scraping, with the costs recorded there. |

So the app's intake is *"retype what is already on your screen."* Every screen after
that is well designed, and the funnel starts with the worst step.

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **Browser extension: save the ad you are looking at** | A button in the toolbar that takes the page you already have open, sends its text to JobKeep, and creates a draft application through the existing import review cycle | Medium-high, and it is mostly *new-codebase* cost rather than hard cost | **Candidate for its own phase. Blocked on Phase 11 (auth) for any public ship; usable locally before that** | See the argument below. |

#### Why this is not the URL scraper wearing a hat

The refusal above is scoped to **the server fetching a URL**. An extension reads a page
**the user has already navigated to, in their own browser, as themselves.** Every
objection the refusal raises is answered by that change of mechanism, and it is worth
laying them side by side because the refusal is the strongest argument available:

| The refusal's objection | What an extension does about it |
|---|---|
| Boards serve a JS shell behind bot protection | The page has **already rendered**. There is no bot to detect and no shell to defeat. |
| Needs Playwright (~300 MB) — breaks the Lambda deploy | Uses the browser the user already has. **Zero server cost, zero deploy impact.** |
| Or a paid scraping API ($30-150/mo) — breaks priority 1 | Nothing to pay for. See the Chrome Web Store note below for the one real cost. |
| Terms prohibit automated collection | A person reading a page and saving it to their own tracker is not automated collection. **This is the mechanism the whole category settled on**, which is the ToS position being defensible out loud rather than argued from first principles. |
| Scrapers break silently on a redesign | **The one objection that partly survives — and this repo already has the answer.** See below. |

**The redesign objection is the interesting one, and JobKeep is unusually well placed.**
A normal extension extracts fields with CSS selectors (`.job-title`, `.description`),
which is exactly what a redesign breaks. This app does not need selectors: it already
has a pipeline that turns **unstructured text** into a structured draft — the Phase 4.5
`DocumentStructurer` and the Phase 4 `AnalyzePosting`. So the content script's whole job
is `document.body.innerText`, and the model does the rest. There is no selector to
break, and it works on a board nobody has ever tested it against. **The AI extractor
stops being a nice-to-have and becomes the thing that makes the intake robust** — which
is a genuinely good interview answer about why the AI is there at all.

#### The market check

**Every serious tracker in this category ships a browser extension, and for most of them
it is the primary intake.** This document already records one instance without drawing
the conclusion: the Huntr row above notes *"Chrome-extension autofill for
Workday/Greenhouse."* Teal ships one too, as does Simplify (which this doc correctly
says is autofill-first) and Careerflow.

So the honest framing is the opposite of the usual backlog question. This is not "should
we build a differentiator?" — it is **"we are missing the table stakes."** The
skill-demand analytics that this doc calls our differentiator only has data to work on
if intake works.

**Verification status, following this document's own norm on overconfident
attributions:** that Huntr and Teal ship extensions is safe to state — the Huntr claim
is already verified in this file. The specifics of *what each extension captures* (full
page text vs. per-board parsers, which boards are supported, whether they use a model)
are **not verified and should not be repeated in an interview** until they are. Check
each product's own store listing before relying on the detail.

#### What it would actually cost, stated plainly

Not cheap, and the cost is in a shape this project has not paid before:

- **A third codebase.** `src/` and `web/` become `src/`, `web/` and `extension/` —
  Manifest V3, a service worker, a content script, its own build and its own tests. The
  Phase 12 lesson (a feature is a slice *and* a screen, so estimates double) becomes a
  third half.
- **Blocked on Phase 11 for anything public.** The app has no auth. An extension posting
  to an unauthenticated Function URL is an open write endpoint on the public internet,
  which is a different and much worse thing than a local app with no login. **Locally it
  is fine**, and that is the version worth building first — priority 4 says local-first,
  and a personal-use extension pointed at `localhost:5080` needs no store listing, no
  review and no auth.
- **The Chrome Web Store costs a one-off developer registration fee** (US$5 at last
  check — verify). Flagged against priority 1 and it **passes**: the rule is that
  nothing in the deployed architecture may bill *per hour*, and a one-time registration
  is not recurring. It is only payable if the extension is ever listed publicly, which
  the local version does not require.
- **CORS and host permissions.** The extension's origin has to be allowed by the API's
  CORS policy, and the manifest has to declare the sites it may read. Both are small,
  and the CORS policy has been exercised since Phase 6.1 by design.
- **Firefox is a second listing**, not a second codebase — MV3 is broadly portable.
  Ignore it until Chrome works.

#### The cheapest version that is still worth having

Worth writing down so this is not remembered as an all-or-nothing item. In rough order
of cost:

1. **Group 4 first, regardless.** Paste-the-ad (Phase 6.5, parked until the 13.3
   boundary) is the endpoint an extension would post to anyway. **Building it is not
   throwaway work — it is the extension's backend.** Nothing here changes its priority
   except to raise it.
2. **A bookmarklet.** One `javascript:` link that grabs `document.body.innerText` and
   POSTs it. No store, no manifest, no review, no third codebase. Ugly, and it proves
   the whole loop end to end in an afternoon.
3. **The local extension.** MV3, one toolbar button, posts to `localhost:5080`. Personal
   use, no auth, no listing.
4. **The public extension.** After Phase 11. Store listing, auth, per-user data.

**Recommended sequencing:** 1 and 2 are cheap and answer the question *"does the model
reliably turn a Seek page's raw text into a usable draft?"* — which is the one real
technical risk, and it is answerable before committing to a third codebase. If the
answer is no, nothing else on this list is worth starting.

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
| ~~**docker-compose**~~ **DONE (2026-09-01)** | Manual `docker run` in the README → one `compose.yaml` | Was P4 and got built anyway, on request: it is one file, and it makes the quick start work on a machine with no .NET SDK and no Node. | "I made the compose file run the Vite dev server rather than a static build — a container that removed hot reload would have been strictly worse than `npm run dev`, so nobody would have used it and it would have rotted." |
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

### Added by the first real use of the add form (2026-09-01)

Phase 6.6 fixed the three defects behind *"it doesn't read the job ad and fill the
skill correctly"* — see
[`phases/phase-6.6-the-ad-goes-somewhere.md`](phases/phase-6.6-the-ad-goes-somewhere.md).
One thing it deliberately did not fix.

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **Attach an upload to an existing application** | Confirming a job-ad import always creates a *new* application, so uploading the ad and logging the application by hand produces two rows for one job | Medium — a target application on the confirm step, a duplicate check, and a decision about what "the same job" means | **Phase 8 (P1)**, with soft delete | This is what actually happened in the report: the user did both, and got two Airwallex rows, one with skills and one without. `CommitImport.cs:306` calls `CreateApplicationHandler` unconditionally — deliberately, so the import reuses the Applications use case rather than writing its tables (rule 2). Making it optional means `CommitImport` needs a target and a "does this already exist" answer, and `ContentHash` dedups *documents*, not jobs. Belongs with Phase 8 because that is where a duplicate row first gets a cheap answer — archive one instead of deleting it. |

### Added by the upload and ATS questions (2026-09-03)

Three questions from the user in one turn, after tracing what happens when a user
navigates away mid-upload. The first became
[Phase 6.5 group 6](phases/phase-6.5-upload-experience.md); the other two are here.
Both **reopen decisions that are currently argued in code**, so both rows carry the
existing argument as well as the case against it — the point is that the reversal
stays legible, not that the old reasoning was bad.

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| **An ATS score out of 100** | The single number every product in this category shows, derived from the check the app already runs | Low-medium — the four stages already compute every input; the work is the gate and the UI | **P2, with the Phase 6 visual pass** | **Reopens a decision argued at `Modules.Match/Application/RunMatchCheck.cs:85` ("Why there is no score").** That argument is specific and good: on the 2026-08-28 real-CV test the same CV as a designed PDF lost the candidate's name, location and every skill, while the plain `.docx` got all three — so the document's biggest ATS risk was never keyword coverage, and *"a number out of 100 would have averaged that away into a digit."* **The case against it is the market**: Jobscan, Resume Worded and Teal all ship a number, and a portfolio piece that omits the category's most recognisable output has to explain itself in every demo. **The design that keeps both** is a score plus a refusal state — a number when the parse is sound, and a blocking sentence instead of a number when it is not (no name found, no skills extracted, `FormattingRiskNotes` non-empty). That preserves the original point exactly: the objection was never to scoring, it was to a score that hides a catastrophic parse. Inputs already exist on `MatchCheckResponse` — `MatchedSkills`, `MissingMustHaveSkills`, `MissingNiceToHaveSkills`, `UnmetRequirements`, `FormattingRiskNotes`. **Do not weight it by guesswork and call it accurate**; whatever formula ships, the screen should say what it is made of, because a number nobody can decompose is the thing the original comment was right to distrust. |
| **Tell the user to update their actual CV** | The drag-a-skill-onto-the-CV gesture updates JobKeep's record of a résumé; nothing says the real document still does not contain the word | Low — a sentence and a state, no schema | **P2, with the ATS score row above** | **The gap:** `MatchCheck.tsx:269 onDragEnd` → `addToCv:244` → `POST /resumes/{id}/skills` → `AddSkillToResume.cs:136`, which writes a real `resume_skills` row with `Source = Parsed` (and *upgrades* an `AiExtracted` row, because a human confirming outranks the model). So the drag genuinely persists and the board repaints from a re-run server check — it is not a front-end illusion. But it edits the parsed skill list, **not the PDF or DOCX the user attaches to an application.** The match check therefore goes green while the document an employer's real ATS reads is unchanged. This is the Phase 5 finding in its final form: the CV said `SQL`, the ad said `PostgreSQL`, and saying *"yes, I have this"* fixes JobKeep's opinion without fixing the file. Nothing in the app, in any doc, or in any comment currently names this. **Cheapest honest fix:** the confirmation line after a drag already exists (`setAnnounce`) — say that the change is recorded here and the résumé file still needs it. Anything larger (tracking which skills were hand-added since the last upload, a "your CV is out of date" banner, generating an amended document) is a real feature and should be costed separately. |


### Added by Phase 14 (2026-09-03)

| Candidate | What it is | Cost / size | Likely home | Notes |
|---|---|---|---|---|
| ~~**Rename the ATS check to "Match check"**~~ — **DONE 2026-09-04**; the split off a real ATS check is NOT done and stays P2 | The feature called "ATS check" is mostly a CV-vs-one-ad comparison, which is a different product from what the industry calls an ATS check | Medium-large — module name, `ats_results`, the route, both API surfaces and the screen; the split adds a second feature | **P2, and NOT before Phase 13.5** | **Raised by the user, confirmed in code.** `RunMatchCheck.cs:17` lists four stages: resolve, skill gap vs the ad, free-text requirement coverage vs the ad, formatting risks. Only the last — plus contact-detail detection — is a real ATS check. The market splits these cleanly: **ATS-friendliness** (Jobscan, Resume Worded) is CV-only — real text layer or an image, tables/columns/text-boxes/header content that mangle a parse, contact details in the body not the header, standard section headings, parseable file type and dates, no job ad involved. **Match rate** (Jobscan's core, Teal) is the comparison. The file already argues the point without naming it: *"the biggest ATS risk in that document was never keyword coverage — it was that a machine reading the file could not find who the candidate was"*, and then treats that as one stage of four. **Name decided: "Match check"** for the comparison; "ATS check" is freed for the parseability feature. **Sequencing mattered** — 13.5 rewrote these endpoints, so the rename waited for it and landed after. **What shipped 2026-09-04:** `Jobkeep.Modules.Match`, `Jobkeep.Contracts.Match`, `match_results` (migration `RenameAtsResultsToMatchResults`), `POST`/`GET /applications/{id}/match-check`, GraphQL `runMatchCheck` / `matchResult`, and the screen. **The Postgres SCHEMA is still `ats`** — deliberately, and argued in `MatchResultConfiguration.cs`: it holds its own `__EFMigrationsHistory`, which EF resolves before applying anything, so renaming it makes EF re-run `InitialCreate` against tables that already exist. **What did NOT ship is the split** — the four stages are unchanged, so the parseability half is still one stage of the comparison feature, and "ATS check" is now a free name waiting for it. The Phase 14 skill vocabulary is a prerequisite for the match half either way, and it has landed. See `phases/phase-14-skill-vocabulary.md`. |
| **Show Technical vs Soft on screen** | Phase 14 stores `SkillKind` on every skill; nothing renders it | Low — one field on two response DTOs plus a chip or a grouping | **P3, with the Phase 6 visual pass** | Deliberately not shipped with Phase 14: nothing renders it yet, and an unread field on the wire is schema nobody can safely remove later — the same call `IPostingContract` made about requirement `Kind`. The data is already correct and already stored, so this is purely additive when a screen wants it. Obvious homes: grouping the Résumés skill list, and splitting the Insights demand chart so a soft-skill bar does not compete with C#. |

## Explicitly NOT backlog (already owned or out of character)

- ~~**Kanban board / drag-and-drop**~~ — **DONE, Phase 6.3.** `Pipeline.tsx`, dnd-kit,
  and the drag is a `PATCH`; a 400 from the status lifecycle renders as a rule
  refusal rather than an error.
- **AI job-description analysis** — that's **Phase 4**, already planned.
- **Rate limiting / production API hygiene** — revisit at deploy (**Phase 10**),
  not before there's a real endpoint to protect.

## When we revisit

Good triggers to pull something off this list:
- A committed phase finishes early and there's appetite for a small add-on
  (soft-delete / export are the low-cost picks).
- A real need shows up while using the tool ("I keep forgetting to follow up" →
  reminders).
- Deployment (Phase 10) forces the question (auth, rate limiting).
