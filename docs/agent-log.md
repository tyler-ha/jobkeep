# Agent log

**Read this before spawning a subagent.** Every entry below is an exploration
that has already been paid for. Re-running an agent over the same ground costs
its tokens again and returns roughly the same answer — the codebase moves, but
not that fast.

The standing rule in this project is **do not spawn subagents unless the user
asks.** This log exists so that when one *is* asked for, it is asked for
something not already here.

## How to use it

1. Find the area you are about to explore in the table below.
2. If there is an entry, read its findings and **verify only the specific facts
   your change depends on** — a `grep` for one symbol is cheaper than an agent by
   three orders of magnitude.
3. Spawn a new agent only for ground no entry covers, and **add a row here when
   it returns.**

Entries carry the date they were taken. A finding that names a file, a line
number or a constant is a claim about that date, not a permanent truth — check it
before you rely on it.

---

## Sessions

### 2026-09-01 — three parallel `Explore` agents, upload/import flow

Run with the user's explicit permission, to plan Phase 6.5 (the upload
experience). **Cost: 80k + 129k + 102k subagent tokens**, ~29 + 41 + 27 tool
calls, 158 / 200 / 240 seconds wall clock. They ran in parallel in one message.

Worth it because the three areas were genuinely disjoint and the answer needed
all three at once. Not worth repeating: the output below is the whole of it.

#### Agent 1 — front-end import UI (`web/`)

- `web/src/routes/Import.tsx` is **one file holding two screens**:
  `return id ? <Review id={id} /> : <Queue />`. Queue = uploader + a 3-tab
  filter + the list; Review = draft form beside the extracted text.
- **There is no drop zone.** No `onDrop` / `dragover` handler anywhere in
  `web/src`. The file control is a bare `<input type="file">` inside a
  `<label className="field">`, unstyled. dnd-kit is used only for skill and card
  dragging on Pipeline and AtsCheck.
- **There is no shared loading, spinner, skeleton or progress component.**
  `web/src/components/` has exactly three files: `Screen.tsx`, `Failure.tsx`,
  `StatusChip.tsx`. No `aria-busy` anywhere. The house pattern is a disabled
  button with a changed label plus an `aria-live` sentence, written per screen —
  `Loading…` appears verbatim in six files. The only `@keyframes` in the app is
  `landed` (`screens.css:189-208`).
- `web/src/lib/api.ts` uses **plain `fetch` only** — no `XMLHttpRequest`
  anywhere — so request-body upload progress is not available without adding one.
- Icons: `lucide-react`, imported per file, **no wrapper component and no shared
  size constant**. Sizes are hand-picked by context (16 nav, 15 in a `.btn`, 14
  grips and breadcrumbs, 13 small buttons, 11–12 pill marks). Bare `aria-hidden`
  on decorative icons; `aria-label` on icon-only buttons. Stroke width never set.
  `.btn { display:inline-flex; gap: var(--s-2) }` does icon/text spacing.
- **"Call this version" appears twice** — once on the upload form (initial value
  `''`, `placeholder="backend-focused"`) and once on the review form (bound to
  `draft.label`, `required`). `file.name` **is** in scope in the uploader and
  nothing reads it.
- No shared `<Button>` / `<Input>` / `<Field>` component exists. Everything is
  raw HTML plus fixed class names defined once in CSS.
- **One test file for all screens**: `web/src/routes/screens.test.tsx`, 13 facts,
  mounting the whole `<App />` in a `MemoryRouter`. **The uploader is completely
  untested.** `@testing-library/user-event` is installed but unused.
- Rename surface for import → upload: `App.tsx` (nav label + 2 routes),
  `Import.tsx` itself, cross-screen links in `Pipeline.tsx:200`,
  `Resumes.tsx:74`, `Insights.tsx:86`, `AtsCheck.tsx:213`, `Today.tsx`, plus the
  `screens.css:1422` section banner and two route strings in the test.

#### Agent 2 — Documents backend pipeline (`src/`)

- **`POST /imports` blocks synchronously on the model for up to 180 seconds.**
  `ImportDocument.cs` extracts text → saves the `DocumentImport` row → *then*
  calls `IDocumentStructurer` → `IChatClient`. `ModelOptions.TimeoutSeconds =
  180`; `llama3.2:3b` on CPU; the first call after boot also pays for loading the
  weights. There is no queue, no background job, no progress channel.
- **The filename-derived label already exists, server-side.**
  `ImportDocument.cs:99-108`: a user label over 100 chars is rejected 400;
  otherwise `Path.GetFileNameWithoutExtension(safeName)` clipped to 100; then
  `"Imported resume"`. Pinned by `ImportHardeningTests`, including a 250-char
  filename.
- `RestructureImport.cs` (`POST /imports/{id}/reparse`) **already is "the second
  half"** — it re-runs the model over the stored `ExtractedText` with no
  re-upload, preserving `Label` and `Posting.SourceUrl`. Any future 202+poll
  split starts there.
- `ImportStatus` is `AwaitingReview | Committed | Discarded` — **no in-flight
  state**, so an async split needs a new enum value and a migration.
- **The job-ad import path already exists end to end.** Every stage branches on
  `Kind`; a committed `JobPosting` import produces an application + posting +
  company, `posting_skills` and `job_requirements`, inside one transaction.
- `BuildPostingDraft` puts the **full untruncated extracted text** into
  `Description` — and `job_postings.Description` is capped at 20000 in the DB
  **with no C# guard**, so a long ad reaches Postgres and 500s with a 22001.
  A latent bug, not hypothetical.
- `DocumentOptions` defaults (the config section is absent from appsettings, so
  these all apply): `MaxBytes = 5MB`, `MinTextChars = 40`,
  `MaxStructureChars = 24000`, `MaxDecompressedBytes = 64MB`, `MaxListSize = 200`.
- `DocumentTextExtractor` sniffs **magic bytes, not the extension**: `%PDF` →
  PdfPig, `PK\x03\x04` → OpenXml with a zip-bomb probe, OLE2 `.doc` and `{\rtf`
  refused with actionable messages, everything else strict-UTF-8 plain text with
  a NUL probe. **A pasted string needs no new extractor.**
- **Never put `[FromForm]` on the `IFormFile`** — Swashbuckle 10 throws and 500s
  the whole `swagger.json`. The three scalars beside it must keep it. Pinned by
  `SwaggerDocumentTests.cs`.
- GraphQL has everything **except** the upload. The stated line is that the
  *bytes* arrive over REST and everything after that is on both surfaces — so a
  text paste, not being bytes, is in scope for a GraphQL mutation.
- Tests in `tests/Jobkeep.Tests/Documents/`: `ExtractionTests` (14),
  `ImportTests` (16), `ImportHardeningTests` (9), `ResumeReadTests` (9),
  `ResumeSkillTests` (6), `SwaggerDocumentTests` (2).

#### Agent 3 — design system, roadmap and cost docs

- **All CSS is four global files** — `tokens.css` (168), `base.css` (206),
  `shell.css` (566), `screens.css` (1956). No CSS modules, nothing beside a
  component.
- Spacing scale is 4px-based, `--s-1: 0.25rem` … `--s-8: 4rem`. **`--space-heading-top`
  is referenced in a comment but never defined** — a live doc/code drift.
- The contrast rules that bind any new UI: `--pop` amber is **1.45** on the
  ground, under the 3.0 non-text threshold, so it can never carry text and never
  be the sole cue for a state or boundary; `--rule` at 1.32 can never be an
  input's sole border; `--alert` red passes on white but **fails on the warm
  ground**; on a tinted surface the label is always the `-dark` token.
- **WCAG 2.2 AA is a stated hard requirement**, confirmed 2026-08-29, chosen as a
  defensible interview claim.
- `PRODUCT.md`'s tiebreak, which settles priority arguments: **"when daily
  usefulness and portfolio impact conflict on a screen, the tool wins."**
- **A live hole:** `JobPost.tsx:226` renders *"No description was saved with this
  one. Paste the ad in and the analyser has something to read."* — and **no
  screen anywhere exposes a description input**, though
  `CreateApplicationRequest.description` and the `PATCH` both accept one.
- **Nothing in `docs/backlog.md` covers** URL scraping, progress indication, the
  import→upload rename, icons/a11y, or job-description text entry. All five were
  unrecorded.
- Measured costs, from `docs/token-log.md`: Phase 6.3 built **three screens for
  22.9M and the next five for 26.2M** — two-thirds more screens for 15% more
  tokens, so per-screen cost roughly halved once the shared CSS existed. Phase
  6.1 (4 endpoints + 14 tests) was 23.2M; Phase 6.2 (the whole scaffold) 20.1M; a
  docs-only session 8.8M. Phase 4.5 cost 66.7M to build and **100.2M to review**.
- The cheapest session shape in the ledger is **Phase 5 at 1.9x — "the only
  session that started by *reading* a plan written elsewhere instead of deriving
  one. The deliberation was already paid for, so it never replayed."**
- `dnd-kit`'s `KeyboardSensor` is **deliberately not mounted**; each drag surface
  has an equivalent non-drag keyboard path instead, and the reasoning is written
  in the code. It is a decision, not an omission.

#### Agent 4 — `Plan`, killed

A `Plan` agent was launched to design the implementation and **the user stopped
it before it reported.** The design was written by hand instead, from the three
reports above, and is in the plan file. No output, no findings — recorded only so
nobody wonders whether there was a fourth result.

---

## What this log is worth

The three `Explore` runs cost **311k subagent tokens** between them. The section
above is what they bought. A future session that reads it instead of re-running
them keeps that money — which is the same argument the token log makes about
session length, applied one level down.
