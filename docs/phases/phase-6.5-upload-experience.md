# Phase 6.5 — the upload experience

**Status: in progress.** Numbered 6.5 rather than 13 because it finishes Phase 6's
front end and leaves the 7–12 build order undisturbed.

| Group | | Status |
|---|---|---|
| D1–D5 | The documentation deliverables | **Done** (2026-09-01) |
| 1 | The rename — import → upload, UI wording only | **Done** (2026-09-01) |
| 2 | The drop zone, the icons, the label default | **Done** (2026-09-01) |
| 3 | The progress indicator | **Done** (2026-09-01) |
| 5 | Spacing and layout, upload screens only | **Done** (2026-09-01) |
| 4 | Paste text — the only group with a backend half | **Not started** |
| | Front-end suite | **46 green**, up from 35. `src/` untouched, so the backend suite was not run |

Group 4 is deliberately last and in its own session. It is the only group that
touches `src/` — a new slice, a GraphQL field and five tests — and the token
ledger says a session's back half costs +75–87% per turn over its front half,
measured on four phases running. Nothing about deferring it makes it bigger.

## Why this phase exists

Phase 6.3 built all eight screens, but **nobody had looked at them in a browser** —
the Chrome extension has been disconnected since 2026-08-28. This is the first
feedback the front end received from actual use, and it was all about one screen.

The user's five asks, in their own words:

1. "the import function, you can change it into upload"
2. "We need some loading bar (or percentage) to hide its longing from customer"
3. "There are some spacing issue"
4. "check upload should have some hover change the icon, or feedback to upload"
5. "Job ad sometimes cannot just have documents to upload, please add a upload
   note from job description" — clarified: *"Sometimes the user don't have
   documents, all they have is copy paste tool so it is easier to use text pasted
   and then use the same process input as parse text."*
6. "the 'call this version' or its name, you can take the name of the file as
   threshold, if the user decide to rename or fill in the input, then we take that
   instead"
7. "please add some icon in these feature (accessability)"

---

## Decisions taken — do not reopen

| Question | Decision | Consequence |
|---|---|---|
| How far does import → upload go? | **UI wording only.** | The API keeps `/imports`, `ImportStatus`, `ImportDraft`, `confirmImport`. The frozen route table in `phase-6-frontend.md` stays true. `lib/api.ts` type names stay as they are — they mirror the backend record. |
| How honest is the progress bar? | **Timer-driven percentage**, chosen after being told it cannot know when Ollama will answer. | No backend change, no migration, no `202`+poll. Built as defensibly as possible (below), with the tradeoff written into the code comment. |
| Where does pasted job-ad text land? | **A "Paste text" mode on the upload screen**, going through the same draft → review → confirm gate. | Needs one new backend slice + a GraphQL field. The user did **not** pick the "description field on the app forms" option — that is now a backlog row. |
| Which spacing gets fixed? | **Upload screens only** (`.uploader`, `.upload-grid`, `.segmented`, `.queue`, `.review`). | The other six screens keep whatever they have. The full Phase 6 visual pass stays outstanding. |
| Does the Upload screen get a held moment? | **Yes — the extracted-character count**, on the review. | Confirmed 2026-09-01. It is the only one of the eight screens that had spent neither its held moment nor either functional amber use. |

**Refused, with reasons — recorded in [`../backlog.md`](../backlog.md), do not
build:**

- **Scraping a job ad from a URL.** Seek / LinkedIn / Indeed serve ads as a JS
  shell behind bot protection, so it needs a headless browser (Playwright, ~300 MB,
  needs a container image — breaks the Phase 10 Lambda-behind-a-Function-URL
  deploy) or a paid scraping API ($30–150/month — breaks priority 1 outright). All
  three sites' terms prohibit automated collection, which matters more than usual
  here because this repo is a portfolio: it is the one feature where "I built it"
  is a liability unless the ToS position can be defended out loud. `hiQ v.
  LinkedIn` is narrower than its reputation — the CFAA claim failed, LinkedIn won
  on breach of contract. And scrapers break silently on a redesign.
  **`job_postings.SourceUrl` already exists (varchar 2000)** and the upload form
  already has a "Link to the ad" input, so the link is kept as provenance beside
  pasted text without any of that.

---

## What the exploration found

Three parallel `Explore` agents were run on 2026-09-01 with the user's explicit
permission, at a cost of **311k subagent tokens**. Their compacted output is in
[`../agent-log.md`](../agent-log.md); what follows is the load-bearing part, kept
here so the next session does not re-derive it. **These are claims about
2026-09-01** — a finding that names a file, a line or a constant should be checked
before it is relied on.

### Backend

- **`POST /imports` blocks synchronously on Ollama for up to 180 seconds.**
  `ImportDocument.cs` extracts text, saves the `DocumentImport` row, *then* calls
  `IDocumentStructurer` → `IChatClient`. `ModelOptions.TimeoutSeconds = 180`,
  `llama3.2:3b` on CPU, and the first call after boot also pays for loading the
  weights. There is no queue, no background job and no progress channel. This is
  why the loading feedback matters, and it is why a byte-transfer progress bar
  would be useless — the file cap is 5 MB and the API is local, so **the entire
  wait is the model**.
- **The filename-derived label already exists, server-side.**
  `ImportDocument.cs:99-108` rejects a user label over 100 chars, else falls back
  to `Path.GetFileNameWithoutExtension(safeName)` clipped to 100, else
  `"Imported resume"`. `ImportHardeningTests` pins it, including a 250-char
  filename. The user's request #6 is therefore **a front-end visibility fix**, not
  a new feature: the upload form showed an empty box with a `backend-focused`
  placeholder, so the default was invisible until the review screen.
- `RestructureImport.cs` (`POST /imports/{id}/reparse`) already *is* "the second
  half" — it re-runs the model over the stored `ExtractedText` with no re-upload,
  preserving `Label` and `Posting.SourceUrl`. Anyone who later wants a `202`+poll
  split should start there.
- `ImportStatus` has **no in-flight state** (`AwaitingReview | Committed |
  Discarded`), so any async split needs a new enum value and a migration.
- `job_postings.Description` is capped at 20000 in the DB **with no C# guard**.
  `BuildPostingDraft` puts the *full untruncated extracted text* in `Description`,
  so a long ad reaches Postgres and 500s with a 22001. **A latent bug the paste
  path makes much more likely** — group 4 fixes it for both paths.
- `DocumentOptions` defaults (the config section is absent from appsettings, so
  these all apply): `MaxBytes = 5 MB`, `MinTextChars = 40`,
  `MaxStructureChars = 24000`, `MaxDecompressedBytes = 64 MB`, `MaxListSize = 200`.
- `DocumentTextExtractor` sniffs **magic bytes, not the extension**: `%PDF` →
  PdfPig, `PK\x03\x04` → OpenXml with a zip-bomb probe, OLE2 `.doc` and `{\rtf`
  refused with actionable messages, everything else strict-UTF-8 plain text with a
  NUL probe. **A pasted string needs no new extractor** — which is what makes the
  user's "same process input as parse text" requirement literally satisfiable.
- **Never put `[FromForm]` on the `IFormFile`** — Swashbuckle 10 throws and 500s
  the whole `swagger.json`. The three scalars beside it must keep it. Pinned by
  `SwaggerDocumentTests.cs`. This is why group 4's paste route is a separate JSON
  endpoint, not an optional-file variant of `POST /imports`.
- GraphQL has everything **except** the upload. The stated line is that the *bytes*
  arrive over REST and everything after that is on both surfaces — so a text paste,
  not being bytes, is in scope for a GraphQL mutation.

### Front end (as at the start of this phase)

- **There was no drop zone.** No `onDrop` / `dragover` handler anywhere in
  `web/src`. The file control was a bare `<input type="file">` inside a
  `<label className="field">`, unstyled. dnd-kit is used only for skill and card
  dragging on Pipeline and AtsCheck.
- **There was no shared loading, spinner, skeleton or progress component.**
  `web/src/components/` has exactly three files: `Screen.tsx`, `Failure.tsx`,
  `StatusChip.tsx`. No `aria-busy` anywhere. The house pattern is a disabled button
  with a changed label plus an `aria-live` sentence, written per screen —
  `Loading…` appears verbatim in six files. The only `@keyframes` in the app is
  `landed` (`screens.css:189-208`).
- `web/src/lib/api.ts` uses **plain `fetch` only** — no `XMLHttpRequest` anywhere —
  so real request-body upload progress is not available without adding one.
- Icons: `lucide-react`, imported per file, **no wrapper component and no shared
  size constant**. Sizes are hand-picked by context (16 nav, 15 in a `.btn`, 14
  grips and breadcrumbs, 13 small buttons, 11–12 pill marks). Bare `aria-hidden` on
  decorative icons; `aria-label` on icon-only buttons. Stroke width never set.
- **"Call this version" appears twice** — once on the upload form (initial value
  `''`, `placeholder="backend-focused"`) and once on the review form (bound to
  `draft.label`, `required`). `file.name` **is** in scope in the uploader and
  nothing read it.
- No shared `<Button>` / `<Input>` / `<Field>` component exists. Everything is raw
  HTML plus fixed class names defined once in CSS. All CSS is four global files —
  `tokens.css`, `base.css`, `shell.css`, `screens.css`. No CSS modules, nothing
  beside a component.
- **One test file for all screens**: `web/src/routes/screens.test.tsx`, mounting
  the whole `<App />` in a `MemoryRouter`. **The uploader was completely untested.**
  `@testing-library/user-event` is installed but unused.
- **`stubFetch` (`web/src/test/fixtures.ts`) throws** `No fixture for ${method}
  ${path}` for anything it does not know. It answers only `GET /imports` and
  `GET /imports/${IMPORT_ID}` for this screen. **Any new call needs a fixture
  branch or every test that mounts `<App />` breaks at once.**
- **A live hole the user chose not to close:** `JobPost.tsx:226` renders "No
  description was saved with this one. Paste the ad in and the analyser has
  something to read." — and no screen anywhere exposes a description input, even
  though `CreateApplicationRequest.description` and the `PATCH` both accept one.
  Now a backlog row.

### Design constraints that bind this work

From `PRODUCT.md` and `tokens.css`. **No new colour, no new token, no new
dependency** — the constraint `phase-12-feature-expansion.md` says to hold.

- WCAG 2.2 AA is a hard requirement. On a tinted surface the label is the `-dark`
  token, never the base.
- `--pop` amber is **1.45** on the ground — under the 3.0 non-text threshold. It
  can never carry text and never be the sole cue for a state or boundary.
  `PRODUCT.md` names "the hot drop zone" specifically as needing a second,
  non-colour cue. Precedent to copy: `.board-cv` (`screens.css:652–668`), which
  changes its outline **and** its label as well as its ground.
- `--rule` (1.32) can never be a control's sole border; `--rule-strong` is the one
  a control may rely on.
- `--alert` red is reserved for genuine failures; an absent or missing thing never
  takes it.
- `tokens.css` is the only place a colour is defined. The only raw hex outside it
  is a *comment*; that rule holds 100%.
- Spacing scale `--s-1: 0.25rem` … `--s-8: 4rem`, 4px base.
- `prefers-reduced-motion` already zeroes all durations globally.

---

## The design pass

Run through the `impeccable` skill on 2026-09-01, scoped by the user to the Upload
tab only — `web/src/routes/Import.tsx` and the `Import & confirm` block of
`screens.css`. Mode is **Operate**: the visitor is completing a task, so
scanability and native expectations outrank expression, and brand lives in the
precise details.

### The identity gap

**Upload was the only one of the eight screens with no held moment.** `Today`,
`Insights` and `AtsCheck` each carry a display-face figure with the `.marked`
amber stroke (`.today-figure`, `.insight-figure`, `.check-figure`). Upload used
amber only on `.queue-warn`. `PRODUCT.md` allots every screen one held moment plus
two functional amber uses, and this screen had spent neither.

**What shipped:** on the review, the extracted-text length moves out of
`.panel-head`'s small `quiet num` span and becomes a figure above the `<pre>`,
reusing the `.today-figure` pattern exactly — a `.marked` span in `--font-display`
at `--t-2xl` with `wdth 118`, and a `--t-md` `--muted` label beside it. It is
literally a value the parser counted, which is what the mono-and-marker rule exists
for, and it spends the screen's amber allowance on the thing the screen is about:
*did the machine actually read your document?*

### Eight defects, all fixed

1. **The vocabulary was already split three ways** — screen title `"Import"`, lede
   *"Upload a CV or a job ad"*, panel head *"Upload a document"*, button *"Upload
   and read"*, route `/import`. The rename resolves an existing inconsistency; it
   is not only a preference. This is the argument for group 1 that the original
   plan did not have.
2. **`.upload-grid` was `repeat(auto-fit, minmax(220px, 1fr))`.** The plan claimed
   the form reflows between 1, 2 and 3 columns as the kind changes. **That claim
   is wrong and is corrected here:** the grid always has exactly three children —
   the kind fieldset, the file field, and *either* "Call this version" *or* "Link
   to the ad", never both. The real defect is that `auto-fit` leaves a ragged
   orphan row between roughly 660–880px, and gives the file control the same `1fr`
   as a text input when it is the most important control on the form.
3. **`.add-actions` had no `flex-wrap`.** The busy sentence is a flex sibling of
   the submit button, so on a narrow main column it crushed the button rather than
   wrapping under it.
4. **The native file input was the one place OS chrome showed through the
   design.** `.field input` styles the wrapper, but the grey "Choose File" button
   inside `input[type=file]` cannot be styled, so a Windows control sat in the
   middle of a hand-built warm palette.
5. **`required` on the file input becomes a live bug the moment the drop zone
   lands.** Once the input is `.sr-only`, Chrome refuses to submit with *"An
   invalid form control with name='' is not focusable"*. It was already redundant —
   the submit button is `disabled={busy || !file}`. **Group 4 would have hit this
   for certain**, because paste mode hides the file input while leaving it in the
   form.
6. **`.queue-item` had no entry in the responsive block.** `.review`,
   `.review-text`, `.add-grid` and `.table` all have narrow treatments; the queue
   row's four-column grid had none, so the filename, the meta line and the
   timestamp competed on a phone.
7. **The review action row had no hierarchy.** "Confirm — create it", "Save
   corrections", "Read it again" and "Discard" were one flat flex row at the same
   size, with the destructive action shoulder-to-shoulder with the primary.
8. **Icons were applied inconsistently on the review actions** — `RefreshCw` and
   `Trash2` were there; Confirm and Save had none.

---

## The groups

### Group 1 — the rename (UI wording only)

Mechanical, and done first because groups 2–5 all edit the file it moves.

- `web/src/routes/Import.tsx` → **`web/src/routes/Upload.tsx`**, default export
  `Upload`. Internal `Queue` / `Review` component names stay.
- `App.tsx` — the `NAV` entry, both routes (`/upload`, `/upload/:id`), the import
  and the two route comments.
- Cross-screen **links**: `Pipeline.tsx`, `Resumes.tsx`, `Insights.tsx`, and two in
  `Today.tsx`.
- Cross-screen **copy only, no link**: `AtsCheck.tsx` and `Applications.tsx`.
  **Correction to the plan and to `agent-log.md`:** both described
  `AtsCheck.tsx:213` as a link. It is not — there is no `<Link to="/import">` in
  either file, only prose.
- `screens.css` — the section banner comment.
- `screens.test.tsx` — the two route strings, and **only** those. The heading
  assertions (`'Is this your CV?'`, `'What was extracted'`) and the filename
  (`'alex-demo-cv.pdf'`) stay unchanged; they are what proves the rename did not
  change behaviour.
- `lib/api.ts` keeps `ImportResponse`, `uploadImport`, `listImports` and the rest,
  with **one comment** recording the deliberate split: the wire says import, the UI
  says upload, and the types mirror the backend record.

### Group 2 — the drop zone, the icons, and the label default

- The bare input becomes a labelled drop target. The native input stays in the DOM
  and focusable, so the keyboard path survives, and `.dropzone:focus-within`
  carries the focus ring. `required` is dropped, with a comment saying why so it
  does not get helpfully re-added.
- **Icon states** (lucide, already a dependency): empty `FileUp` → drag-over
  `FileDown` → chosen `FileCheck2`, plus an `X` clear button with an `aria-label`.
- **Drag-over is three signals**, because amber alone is illegal here: a
  `--pop-tint` ground **+** a solid 2px `--pop-dark` outline **+** the label text
  changing to "Drop it here". Every cue works with colour removed. Hover is the
  calmer pair — border to `--pri`, ground to `--pri-tint`.
- **The label default (request #6):** when a file is chosen and the label box is
  untouched, the input *shows* `file.name` with the extension stripped, and a
  `labelTouched` flag means typing wins and a later file swap does not overwrite
  it. This makes the server-side default **visible** rather than changing
  behaviour — the request still sends `label.trim() || undefined`, so
  `ImportDocument.cs`'s fallback stays the one source of truth.
- **Icons elsewhere, restrained:** `FileText` on "A CV", `Briefcase` on "A job ad",
  `Check` on "Confirm — create it", `Save` on "Save corrections". **Queue filter
  tabs stay text** — `aria-pressed` already carries their state, and icon soup on a
  filter row helps nobody. Every decorative icon takes bare `aria-hidden`.

### Group 3 — the progress indicator

- **`web/src/lib/progress.ts`** — a pure function, because the house rule is that
  arithmetic a UI depends on lives in `lib/` with a test (precedent `lib/chart.ts`).
  Asymptotic: `1 - Math.exp(-1.4 * elapsed / median)`, so it never reaches 1 and
  never stalls on a round number. Unit-tested in `progress.test.ts`.
- **The component stays local to `Upload.tsx`.** The house rule is that a component
  moves to `components/` only once a *second* screen needs it; the trigger is noted
  in a comment.
- **a11y:** `role="progressbar"` with `aria-valuemin/max/now` and
  `aria-valuetext`, and **not** inside an `aria-live` region — politely announcing
  a changing percentage is a screen-reader disaster. One separate
  `aria-live="polite"` sentence announces the stage, once.
- **One honest line of copy, not a staged-on-a-timer fake.** Faking a stage
  transition the client cannot observe was considered and refused.
- **Reduced motion:** durations are already zeroed globally, so the width
  transition becomes instant; the update interval drops to ~1s so the element does
  not twitch.

**The tradeoff, written into the code comment because it is the interview
material:** this bar models the *wait*, not the transfer. The cap is 5 MB against a
local API, so the transfer is meaningless and the entire wait is the model. It
cannot know when Ollama will answer, so it decelerates rather than lying about
being nearly done. **Do not "fix" it into a `202`+poll design** — that needs a new
`ImportStatus` value and a migration, and `RestructureImport.cs` is where it would
start.

### Group 5 — spacing and layout, upload screens only

**Stated honestly: this is a code audit against the 4px scale, not a visual fix,
because the app still cannot be seen.** Folded into this session because it edits
the same CSS block as groups 2 and 3.

- `.upload-grid` — explicit two columns at ≥720px, one below, the drop zone
  spanning both. Deterministic, no orphan row.
- `.add-actions` — `flex-wrap`, and the sentence takes its own full-width row.
- `.review-actions` — Discard pushed right so the destructive action is not
  adjacent to the primary; the status sentence takes its own row.
- `.queue-item` — a narrow rule in the existing responsive block.
- The rest of `.uploader`, `.panel-head`, `.segmented`, `.queue`, `.review` and
  `.source-body` audited against `--s-1`…`--s-8`. **The other six screens' blocks
  were not touched.**

### Group 4 — paste text (not started)

The only group with a backend half. Plan, for the session that picks it up:

- **`src/Modules/Documents/ImportText.cs`** — `POST /imports/text`, JSON body
  `ImportTextRequest(DocumentKind Kind, string Text, string? Label, string?
  SourceUrl, string? Name)`, returning the same `ImportResponse`. *A sibling route
  rather than making `file` optional*, because one endpoint with two mutually
  exclusive bodies is an OpenAPI shape Swashbuckle represents badly, and it walks
  straight back toward the `[FromForm]`/`IFormFile` trap that already took down
  `swagger.json` once.
- **Reuse the extractor, do not bypass it.** Call
  `IDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes(text), name ?? "pasted.txt")`.
  That gives the NUL probe, the strict-UTF8 guard, `Normalise` and
  `SourceFormat.PlainText` for free — and it is what makes the user's requirement
  literally true: **a pasted ad and an uploaded `.txt` take the identical path.**
- Row fields for a pasted import: `FileName` = the supplied name or `"Pasted text"`
  (≤260, NOT NULL), `Format` = `PlainText`, `ByteCount` = the UTF-8 byte count,
  `ContentHash` = SHA-256 of the same bytes, so paste dedup behaves identically.
- Validation before the model: text required and trimmed; byte length ≤
  `DocumentOptions.MaxBytes`; **under `MinTextChars` (40) → 400 with a sentence**,
  not the silent empty-draft path. A 12-character paste is a mistake; a scanned PDF
  is a real document, which is why the file path differs.
- **Fix the latent 500 while there:** a `DraftLimits.MaxDescriptionLength = 20000`
  clip in `CommitImport.CommitPostingAsync`, matching the "clip, don't refuse"
  convention every other model-supplied field already follows. Applies to both
  paths, not just paste.
- **A GraphQL field is required.** The file-upload exception covers *receiving
  bytes* only; a text paste is not bytes, so the house parity rule applies.
- Tests (`tests/Jobkeep.Tests/Documents/ImportTextTests.cs`): a pasted ad produces
  a draft and **writes no records at all**; a paste under 40 chars is refused with
  a sentence; **the strongest one — a paste and an identical `.txt` upload produce
  the same `ContentHash` and the same draft**; an over-long description is clipped,
  not a 500; GraphQL parity.
- Front end: a source toggle above the kind selector reusing the existing
  `.segmented` pattern — "Choose a file" (`Paperclip`) / "Paste text"
  (`ClipboardPaste`) — a `<textarea>` with a mono character counter, `importText()`
  in `lib/api.ts`, `stubFetch` branches for **both** `POST /imports` and
  `POST /imports/text`, and one `user-event` test that types and submits.

### Group 6 — the upload stops blocking (DONE, 2026-09-04)

**This REVERSES a decision written into Group 3 above and into the header comment
of `web/src/lib/progress.ts`**, at the user's instruction on 2026-09-03: *"upload is
one process, don't block the user, then we process through our backend. Whenever it
is done, we update the changes in the UI."* The reversal is recorded rather than
quietly applied, because the refused design is argued at length in two places and a
future reader would otherwise think the argument was simply lost.

Group 3 refused a `202`+poll design for two stated reasons: it needs a new
`ImportStatus` value, and it needs a migration. **The second is wrong, and checking
it is what makes this affordable.** `document_imports.Status` is
`HasConversion<string>().HasMaxLength(20)` with **no CHECK constraint**
(`DocumentImportConfiguration.cs:18`) — the only CHECK constraints in the schema are
the two on `job_postings`. A new enum value of twenty characters or fewer is a code
change and nothing else. This was already established at 13.2c, when `CommitFailed`
was added for exactly this reason, and it was never carried back into this doc.

The first reason stands: there is a new value, and it is `Parsing`.

#### What actually blocks

One line. `ImportDocument.cs:159` — `await _structurer.StructureAsync(...)`, up to
`ModelOptions.TimeoutSeconds` (180) against llama3.2:3b on CPU. Everything before it
is byte work and finishes in milliseconds.

**The split is already drawn in the code.** `ImportDocument.cs:149` commits the row
*before* the model call, deliberately, with a comment explaining that the durable
extraction is worth more than the structuring. So the change is not "make this
async" — it is "return at the line that already exists", and the reasoning for that
early save transfers unchanged.

#### Who runs the model, then — and why it is not a background service

The obvious answer is an `IHostedService` with a `Channel<Guid>` queue: about forty
lines, no new dependency, works locally and in Compose.

**It is refused, and the reason is Phase 10.** The deploy target is AWS Lambda
behind a Function URL. Lambda freezes the execution environment once the response is
returned; work queued to a background thread is not guaranteed to run, and when it
does run it may be on the next invocation, minutes later, or never. A design whose
central mechanism silently stops working on the deploy target is a design that buys
a local demo with a production defect.

The counter-argument, stated here so it is not re-made: Ollama is not on Lambda
either, so the AI story there is *already* unsolved. True — and that is the reason
not to add a **second** unsolved thing when a free alternative has none.

**The alternative is that the client drives it, and the endpoint already exists.**
`POST /imports/{id}/reparse` (`RestructureImport.cs`) runs the model over the stored
text with no re-upload. That is the entire second half of this feature: already
written, already tested, already on both surfaces. Group 3's own comment points at
it as the place this design would start; it turns out to be the place it finishes
too.

#### The shape

1. `POST /imports` extracts, saves, and returns **202** with the row in `Parsing`.
   The `StructureAsync` call and the two `SaveChangesAsync` after it move out.
2. The client navigates to the review screen **immediately** — honest now, because
   it is instant.
3. The client fires `POST /imports/{id}/reparse` and does not await it for the
   purpose of blocking anything.
4. The client polls `GET /imports/{id}` until `Status != Parsing`, then paints the
   draft.

`RestructureImport` already sets `AwaitingReview` on success and already writes a
`Warning` on model failure, so steps 1 and 3 need no new agreement between them
beyond the one new enum value.

#### `Parsing` is a claim, not a lock

**The known ceiling, written down rather than engineered away.** If the user closes
the tab while step 3 is in flight, the request is cancelled and the row stays in
`Parsing` for ever. There is no lease, no timeout, no sweeper and no outbox — the
same position 13.3c took on domain events, and for the same reason: the cheap
recovery already exists.

A `Parsing` row is re-runnable by pressing the button the queue already needs, and
the queue is where an abandoned one is visible. Anything more — a visibility
timeout, a lease column, a reaper — is Phase 14's, alongside the outbox.

#### Three things this fixes for free

- **The silent empty draft.** Today a parse cancelled mid-flight (tab closed, hard
  reload) leaves a row that looks *normal* in the queue: `AwaitingReview`, empty
  draft, no `Warning`. The other two degraded paths both explain themselves; this
  one cannot, because cancellation runs no code. `Parsing` makes the state
  self-describing, which is the property the other two already have.
- **The navigate-after-unmount bug.** `Upload.tsx:277` calls `navigate()` after the
  await with no liveness guard, and react-router 7's `useNavigate` sets
  `activeRef.current = true` in a layout effect **with no cleanup**, so the ref
  stays true after unmount and the navigation fires. Today that means: upload a CV,
  click another nav link, and up to 180 seconds later the app yanks you onto the
  review screen from wherever you went. After this change the navigate happens in
  about a second, so it stops being reachable in practice. **Guard it anyway** — two
  lines, matching the `let live = true` pattern the read effects in this same file
  already use (`Upload.tsx:130`, `JobPost.tsx:60`, `AtsCheck.tsx:127`,
  `Applications.tsx:112`). A latent bug that is merely hard to hit is still a bug.
- **The progress bar becomes truthful.** `estimateProgress` stays — the wait did not
  disappear, it moved — but it now models a wait the client can *observe ending*,
  because polling reports the transition rather than inferring it. The header
  comment in `progress.ts` must be rewritten, not deleted: its tradeoff was
  correctly reasoned for the design it described.

#### "Whenever it is done, we update the changes in the UI" — how far to take it

Two levels, and the recommendation is the first.

**In scope: the review screen polls.** Navigating away and coming back shows the
finished draft; the queue shows `Parsing` rows in their own state. This satisfies
"don't block the user" completely and needs no new machinery.

**Marked, not built: a global notification.** Telling the user on *whatever screen
they are on* needs a poller above the router and somewhere to keep its state.
`web/src` has no context, store or provider today — deliberately, per Phase 6 — and
adding the app's first one to deliver a toast is a large structural change bought
for a small courtesy. **Do it only if the user asks after using the polled
version**, because the polled version may well be enough.

#### Cost

No migration. One enum value, one slice shortened, one existing endpoint reused, and
the front-end poll. The `SwaggerDocumentTests` pin still applies — the multipart
route keeps its shape, only its body of work shrinks.

The tests that change: anything asserting `POST /imports` returns a populated draft
now asserts `202` and `Parsing`, and the round trip through `/reparse` becomes the
test that a draft eventually appears.

#### What the plan got wrong — deviations, written down

Four, and the first is the one that would have shipped a broken feature.

**1. `/reparse` did NOT already accept a `Parsing` row.** The plan said steps 1
and 3 "need no new agreement between them beyond the one new enum value", and
that was wrong three times over. `RestructureImport` guarded on
`Status != AwaitingReview`, so it would have refused every row the new upload
creates; it never *set* `Status` at all, because the row was already
`AwaitingReview` when it ran; and its model-failure path returns `Invalid`
writing nothing, so a failed parse would have left the row in `Parsing` for ever
with no explanation — strictly worse than the silent empty draft this group set
out to fix. All three are now handled, and the failure path branches on
`finishingUpload`: finishing an upload closes the row out into `AwaitingReview`
with a warning (exactly what `POST /imports` used to return), while a re-parse a
human pressed still reports `Invalid` and leaves the existing draft alone.

**2. The scanned-PDF path does not go through `Parsing`.** A document with no
text layer is finished the moment it is saved — nothing will ever be driven for
it — so marking it `Parsing` would strand it in a state whose only exit is a
parse that cannot happen. It stays `AwaitingReview`, and it is now the one upload
path that still completes in a single request.

**3. `POST /imports` still returns `201 Created`, not `202 Accepted`.** The plan
asked for 202. It was not taken, because a resource genuinely *is* created and
does have a URL — 202 is for "accepted, nothing to point at yet" — and because
deviation 2 means the status would otherwise have to vary by document, making the
wire contract depend on whether a PDF happened to have a text layer. The client
branches on the `status` field, which is unambiguous either way.

**4. No poller was built, and none is needed.** The plan's step 4 was "poll
`GET /imports/{id}` until `Status != Parsing`". There is nothing to poll *for*:
the model runs inside the client's own `/reparse` request, so its response IS the
completion event. The review screen fires the reparse and sets state from what
comes back. Navigating away does not cancel it — the request runs to completion
and the server writes the row, so returning shows the finished draft. Only
closing the tab strands the row, which is the ceiling `ImportStatus.Parsing`
documents and the queue's "Still reading" tab recovers.

Also done as planned: the `useNavigate` liveness guard (still latent, now
guarded), the `progress.ts` header rewritten rather than deleted, and the bar
moved from under the upload button to the review screen — the wait did not
disappear, it moved, and the bar moved with it.

#### Cost, as built

No migration, as predicted. One enum value, one slice shortened by ~30 lines, one
existing endpoint taught a second caller, one new queue tab. Suite **285 → 288**.
The two shared test helpers (`ImportTests.PostImportAsync`,
`ImportHardeningTests.ImportAsync`) now upload *and* reparse, because that pair is
what "an import with a draft" means from here — 17 downstream tests were failing
on a `Parsing` row until they did, which is the split proving it is real.

---

## Deviations from the plan

- **The `.upload-grid` reflow claim was wrong** and is corrected above (defect 2).
  It was in the design plan and would have been carried into this doc unexamined.
- **`AtsCheck.tsx:213` is not a link**, though the plan and `agent-log.md` both
  said so. Copy only.
- **`docs/agents-and-tools.md` was not written as one file.** The user's ask for
  "which agents and tools this project uses" shipped on 2026-09-01 as two:
  [`../agent-log.md`](../agent-log.md) (what each subagent run found, so it is not
  re-bought) and [`../tool-usage.md`](../tool-usage.md) (which tool for which job,
  and the traps that have cost a turn). `CLAUDE.md` points at both.
- **Group 5 was absorbed into this session** rather than left for the Group 4
  session, because it edits the same CSS block groups 2 and 3 do and would have
  cost more as a separate visit.
- **The held moment was added**, which is not in the original five asks. It came
  out of the `impeccable` pass and was confirmed with the user before building.
- **`formatBytes` was added to `lib/format.ts`**, with tests. The drop zone shows
  the chosen file's size, and every other screen was writing
  `Math.round(byteCount / 1024)` inline. It counts in powers of ten, not 1024 —
  the number sits beside a filename the user just picked out of a file browser,
  and every desktop file browser counts the same way.
- **The uploader got its first two tests.** It was completely untested, which is
  how it kept the `required` attribute that defect 5 is about. They pin the label
  default (the extension is stripped, typing wins, a later file swap does not
  overwrite) and the fact that submit is gated by `disabled`, not by `required`.
- **The progress fill is `scaleX`, not `width`.** The first build animated
  `width`; the `impeccable` detector flagged it, and the repo had already made
  the same call twice with the reasoning written down (`.stage-rule::after`,
  `.demand-fill`). It matters more here than there — this bar updates eight
  times a second for as long as the model takes, which is the worst case an
  animated `width` has.

## What is still outstanding

- **Group 4**, above. It is now the only group left.
- **The Phase 6 visual pass on the other seven screens.** Still blocked on the
  user's eyes: the Chrome extension has been disconnected for five sessions, so
  nothing in this phase has been *seen*, only reasoned about from the CSS.
- **Four things about this phase only the user can confirm**, for the same
  reason: whether the spacing actually reads better, whether the drag-over state
  is obvious in the hand, whether the progress bar feels honest on a slow parse,
  and whether the character count is the right held moment. The build is verified
  — 46 tests, a clean `tsc` and a clean detector run — but *verified* and *seen*
  are not the same word, and this doc should not imply they are.
- **Step 6.4**, the root README.
