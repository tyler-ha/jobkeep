# Phase 4.5 — Document import: upload, parse, confirm, save

**Status: Done** (2026-08-27), reviewed and hardened before merge (2026-08-28),
then tested against a real exported CV, which forced the PDF extractor to be
rewritten (2026-08-28).
Built, tested (**185 tests green**, up from 171), and checked by hand against the
real model. Ten defects found by the pre-merge review are fixed and pinned - see
"Pre-merge review" below, which is the most useful section in this document.

> Numbered 4.5 rather than inserted as a new 5 on purpose. Renumbering the phase
> docs cost 10.2M tokens the one time it was done (see `CLAUDE.md`, "Build cost"),
> and a decimal is free.

## What was asked for

> *"add a feature (if you don't have it) to parse text/docs/docx/pdf/etc file to
> input, then AI or tools analyse the doc (depends on what kind of tools or
> direction that industry usually work with), looking for keywords that fits our
> database, (like upload for job description, upload resume/cv). Then it should
> ask the user to confirm the correct input. Like a CV builder. Then confirm or
> fix on user term. Then save to upload the documents to save to the db. For now,
> no saving documents yet. We will have it in the backlog."*

Two things in that are broader than the original 4.5 plan, and both were built:
it covers **job ads as well as resumes**, and it has an explicit **confirm-or-fix
step** before anything is written.

## What shipped

```
POST   /imports                  multipart: file + kind (+ label, sourceUrl)
GET    /imports?status=          the review queue
GET    /imports/{id}             the review screen: draft + extracted text
PUT    /imports/{id}             the user's corrections
POST   /imports/{id}/reparse     re-run the model over the stored text
POST   /imports/{id}/confirm     the gate — writes the real rows
DELETE /imports/{id}             discard
```

GraphQL gets `imports`, `import`, `reviewImport`, `restructureImport`,
`confirmImport`, `discardImport`. **The upload is REST-only** — see "Deviations".

| File | Role |
|---|---|
| `src/Modules/Documents/DocumentTextExtractor.cs` | bytes → text. PdfPig, OpenXml, UTF-8. Content-sniffed |
| `src/Modules/Documents/DocumentStructurer.cs` | text → draft, via `IChatClient` |
| `src/Modules/Documents/ImportDraft.cs` | the draft shapes + the model-facing schema classes |
| `src/Modules/Documents/ImportDocument.cs` | the upload slice |
| `src/Modules/Documents/{Get,List,Review,Restructure,Commit,Discard}Import.cs` | the review cycle, one slice each |
| `src/Modules/Documents/DocumentsModule.cs` | DI, options, routes |
| `src/Shared/ModelClient.cs` | `IChatClient` registration, moved out of the Ai module |
| `src/Models/Resume.cs`, `DocumentImport.cs` | the new entities |
| `src/Migrations/…_ResumesAndDocumentImports.cs` | first migration since InitialCreate |
| `tests/Jobkeep.Tests/Documents/` | 13 extraction + 16 import tests |
| `tests/Jobkeep.Tests/Fixtures/` | real PDF / DOCX / TXT / OLE2 fixture files |

New packages: **PdfPig 0.1.16** (Apache 2.0) and **DocumentFormat.OpenXml 3.5.1**
(MIT). Both pure managed, no native dependency, no vulnerability advisories.

## The shape, and why it is this shape

**"Parse a document" is two problems with opposite failure modes.** Keeping them
apart is the whole design:

```
bytes ──[extraction]──> text ──[structuring]──> draft ──[human]──> rows
         deterministic          a sampled model        confirmed
         fails loudly           fails plausibly
```

Extraction is a library call: same file, same text, every time; when it fails it
throws. Structuring is a model: when it fails it returns something that looks
right and isn't. **The text is persisted between them**, which buys three things —
re-parsing after a prompt change costs no re-upload, a bad extraction is
diagnosable without a model in the loop, and each half gets the test it deserves
(a real fixture file / a canned reply).

This is also what the industry does, which is worth knowing since the question was
asked. Textkernel, Affinda and DaXtra all separate document conversion from field
extraction; the second stage moved from statistical sequence models to LLMs over
the last few years and the **seam between the two did not move**. The
confirm-and-fix screen is standard too — it is the "review your parsed resume"
step in every ATS onboarding flow.

## Decisions

The plan version of this doc ended with four open questions. All four were
answered while building; here is what was chosen and why.

**1. Where does a resume live? → B, a `resumes` table.** `JobApplication.ResumeText`
is gone, replaced by `ResumeId`. A resume is a property of *you*, not of one
application; as a column it stored the same text once per application and gave the
parsed records nowhere reusable to live. The payoff is immediate and is
demonstrated below: resume skills and posting skills are now rows in the **same
shared `skills` table**.

**2. Fold the parked Phase 2.7 migration in? → No.** This phase opened a migration,
which is the cheap moment to fix the case-sensitive dedup gap — but doing it would
make one phase do two things, which is what `CLAUDE.md` priority 2 exists to
prevent. The new `resumes.Label` index is deliberately case-sensitive **to match**
skills and companies, so all three still agree and 2.7 can fix them together.

**3. Which records? → All four.** Skills, basics, experience, education. "Like a CV
builder" needs experience and education; cutting to skills-plus-basics would have
halved the phase and the feature.

**4. `.doc`? → Out, explicitly.** A clear 400 telling the user to save as `.docx`.
There is no good free pure-managed extractor for the pre-2007 binary format, and
the alternatives are a native dependency or a commercial licence. This was the
phase's named scope trap and it stayed out.

**5. Keep the uploaded file? → No** (confirmed by the user: *"For now, no saving
documents yet. We will have it in the backlog."*). Extract the text, store the
text, hash the bytes for provenance, drop the bytes. The cost argument is Neon's
free-tier 0.5 GB; the better argument is security — nothing is written to disk, so
path-traversal-via-filename cannot occur here. Added to `backlog.md`.

Three decisions were **not** in the plan and were made while building:

**6. The draft is a `jsonb` column, not draft tables.** A draft has no query
surface: nothing asks "find drafts whose third experience mentions Kubernetes". It
is written whole, read whole, edited whole, then either committed or discarded.
Five throwaway tables mirroring the five real ones would double the schema to model
something whose entire lifetime is one review screen. The *committed* records get
real tables, because those are queried.

**7. A module may call another module's use case.** The posting commit calls
Applications' `CreateApplicationHandler` rather than writing `job_applications`.
That is **not** the rule-2 crossing `IPostingContract` exists to mediate — reaching
into another module's *tables* is what rule 2 forbids; invoking its published *use
case* is the boundary working. The validation and the company dedup run, so there
is still exactly one implementation of "create an application" (A4). Skills still
go through `IPostingContract.AddExtractedSkillsAsync` — the existing contract,
used unchanged. **Its two-method cap was not stretched**: a second consumer needing
exactly the methods that already exist is evidence the boundary was drawn right.

**8. `IChatClient` moved from the Ai module to `Shared/`.** Documents calls a model
too. The rule this produced, which the plan predicted: **the Ai module owns a
table, not a technology.** `IChatClient` is a shared dependency any module may
inject, like `AppDbContext`. Left in `AiModule`, Ai would grow a slice every time
any feature wanted a model — `IJobApplicationRepository` in a new costume.

## Real-model check — three findings, and one that transfers

Run against **llama3.2:3b** on the fixture resume (a real PDF) and a realistic
Melbourne job ad. As in Phase 4, none of these was catchable by the test suite,
because the tests fake the model on purpose.

### The resume path worked first time
9.4s. Name, email, phone and location all correct; the professional summary
copied; **5/5 skills**; both jobs with employer, title and dates; the education
entry. Dates came back transcribed — `"Mar 2021"`, `"Present"` — which is what the
`[Description]` saying "exactly as the resume writes it" is for. Asked for a date,
a small model normalises to `2021-03-01` and turns "Present" into today, inventing
precision the document does not contain.

### Finding 1 — **field order in the schema decided whether an array was filled at all**

The job-ad path first returned **zero skills and zero requirements in 0.9 seconds**,
reproducibly (Temperature = 0). Company, title and location were correct — all
answerable from the first two lines.

Two changes, tested one at a time:

| Change | skills | requirements |
|---|---:|---:|
| as built | 0 | 0 |
| prompt: stop granting permission to return an empty list; demand every technology | 0 | 6 |
| **schema: move `Requirements` before `Skills`** | **7** | 6 |

The reorder is the finding. With constrained decoding the model emits properties
in **schema order**, and the first array it reaches is emitted before it has
engaged with the document at all. Put another array in front of it and it fills.
Phase 4's analyzer never hit this because its schema has `Seniority` and a
2-3 sentence `Summary` before `Skills`, and generating the summary is what forces
the read.

**The general rule, worth carrying to any future extraction schema: put a field
that cannot be answered without reading the whole document FIRST.** Cheap scalar
fields at the top are a trap — they let the model answer, commit to a shape, and
coast.

The prompt change was kept as well: it is what took requirements from 0 to 6, and
`"use an empty list"` in a prompt turns out to be an invitation rather than a
permission.

### Finding 2 — **a schema-constrained enum guarantees a valid answer, not a right one**

Every extracted requirement came back `Responsibility`, including *"At least 5
years of professional backend engineering experience"*, which is plainly a
Qualification. The model also read only the "What we are looking for" section,
skipping the responsibilities and benefits entirely.

The first theory was a parsing artefact: `Kind` was a `string`, and the mapper's
tolerant `TryParse` fell back to `Responsibility` on anything unrecognised — so a
uniformly-wrong field would look exactly like this. It was changed to a real enum
that reaches the schema as a JSON Schema `enum` of the three names, making an
invalid value impossible.

**It made no difference.** The model had been answering with a legal word all
along and choosing badly. Classifying a sentence into three abstract categories is
simply at the edge of what a 3B model does well, and no prompt attempted moved it.

Kept as-is, and the enum change was kept too — it removes a silent fallback that
was actively hiding the problem. This is the case the confirm-and-fix step exists
for: a wrong label is one click to fix on the review screen, and `IsMustHave` —
the field Phase 5's ATS check actually reads — was **correct on all six**.

The meta-lesson matches Phase 4's exactly: the instinct was "the model is too
small". The first defect was a field reordering. The second is a real model
limitation, and the design already absorbs it — which is the point of putting a
human in the loop rather than trusting the output.

### The payoff query, on real imported data

A resume uploaded as a PDF, a job ad uploaded as text, both confirmed:

```
     Name     | posting_requires | on_my_resume
--------------+------------------+--------------
 .NET         | t                | f
 ASP.NET Core | f                | f
 Kafka        | f                | f
 AWS          | t                | t
 C#           | t                | t
 PostgreSQL   | t                | t
 Docker       | f                | t
```

*".NET is required and is not on my resume"* — one join, because both sides are
rows in the shared `skills` table. That is Phase 5's ATS check, already answerable,
and it is the concrete return on decision 1.

## Deviations from the plan

- **Scope widened to job ads**, on the user's ask. The plan was resume-only.
- **The confirm/fix cycle was added** — the plan went straight from parse to rows.
  It is now the centre of the design and the reason for `document_imports`.
- **`/reparse` was not planned.** It falls out of storing the text between the two
  stages and is what makes that decision pay: a prompt or model improvement
  re-runs over every past import with no re-upload.
- **The upload endpoint is REST-only.** Every other write in this app exists on
  both surfaces and that rule is deliberately broken here. GraphQL has no file
  type; uploading through it means the GraphQL multipart-request *convention* and
  an `Upload` scalar, i.e. a non-standard extension in the published schema so one
  mutation can do what a plain POST already does. The line drawn instead: the
  **bytes** arrive over REST, everything after that is on both surfaces. The rule
  is about business logic having one implementation, and "receive a file" is
  transport.
- **`DocumentFormat` had to be renamed `SourceFormat`** — `DocumentFormat.OpenXml`
  puts a namespace of that name in scope and CS0118 results. Same class of
  collision as `SliceResult` vs GreenDonut's `Result`.
- **The migration drops `ResumeText` without migrating its contents.** Safe here —
  single-user local database, never deployed, the column was Phase 5 scaffolding
  no endpoint meaningfully filled. The migration says so in a comment.

## Pre-merge review — what a second read found

The module was built, tested and committed in one session, and **nobody had read
it since**. Before opening the PR it got a deliberate review, on the grounds that
it is the first code in this project that accepts a file from outside. Ten
defects came out, and all ten are now fixed with a regression test each
(`tests/Jobkeep.Tests/Documents/ImportHardeningTests.cs`, plus two in
`ExtractionTests.cs`).

**The tests were verified by breaking the code again.** Every fix was reverted and
the suite re-run: all ten failed, and the three “the guard does not also refuse
the real thing” tests still passed. A regression test that has never seen the bug
it names is a comment with a `[Fact]` on it.

### The one worth remembering as a general fact

**`System.Text.Json` does not enforce nullable reference types.** `List<string>
Skills` on a record is a compile-time claim with no runtime enforcement: a PUT
body that simply omits `skills` deserializes it to `null`, stores `null`, and the
confirm that reads it back dereferences `null` — a 500 for a request whose only
sin was leaving a field out, which is the most ordinary thing a client does.

The fix is `DraftSanitiser` in `ImportDraft.cs`, applied at three boundaries: on
the way in (`ReviewImport`), on the way out (`ImportDocument.ReadDraft`, which
also covers rows written before it existed), and one level down, because
sanitising the top-level lists is not enough when the objects inside them carry
lists of their own.

### The pattern the ten share

An import carries content from three sources with three different trust levels —
**a document, a language model, and a person** — and almost every defect was a
place where the code treated one of them like another.

| # | Defect | Trust confusion |
|---|---|---|
| 1 | Null collections from a partial PUT crashed the confirm | Deserializer output trusted to honour the C# types |
| 2 | Model fields wider than their columns → 22001 → unhandled 500 | Model output trusted to fit the schema |
| 3 | A user-typed label was silently truncated at upload but rejected at confirm | Two gates disagreeing about the same value |
| 4 | A 250-character filename became a label the column could not hold | Filename trusted as a default |
| 5 | The label was dropped entirely when the model failed | The degraded path forgot the user's own input |
| 6 | Rejected requirements were dropped in silence | Partial success reported as success |
| 7 | The posting commit was not atomic — an application could exist with none of its skills | Multi-step write without a transaction |
| 8 | `resumeId` was handed to EF unchecked → FK violation → 500 | A foreign key trusted to point at something |
| 9 | The 5 MB cap was not enforced where the comment said it was | A check placed after the bytes were already buffered |
| 10 | A `.docx` is a zip, and nothing bounded what it decompressed to | Input size trusted to bound the work |

Two more that are not defects but were tightened in the same pass: a client
cancelling an upload was being reported as an Ollama outage
(`DocumentStructurer`'s catch filter now distinguishes them), and the posting
commit now runs inside an explicit transaction — safe to add because the app
configures no retrying execution strategy.

### The two that are actually about the attack surface

**9 — the size cap was documentation, not enforcement.** The handler's
`file.Length > MaxBytes` check runs *after* `[FromForm]` binding, and binding
reads the whole multipart body first, spooling anything over 64 KB to a temp
file. The framework defaults are 128 MB for a multipart body and 30 MB for the
request. So a 30 MB upload was written to disk in full and only then answered
“the limit is 5120KB”. The cap is now endpoint metadata —
`WithFormOptions(multipartBodyLengthLimit:)` plus `RequestSizeLimitAttribute` —
so the refusal happens mid-stream. The handler's check stays: it is what produces
the friendly message with the real numbers in it.

**10 — the input cap bounded what arrived, not what it became.** A `.docx` is a
zip, and zip compresses: a few hundred KB of crafted archive decompresses to
gigabytes. `MaxDecompressedBytes` (64 MB, ~13x the input cap) is now checked
twice — once against the archive's declared uncompressed total, read from the
central directory without decompressing anything, and once against the text
actually accumulated, because a crafted archive can understate the first number.
Two cheap bounds beat one clever one. The test builds a real zip that is under
64 KB and claims 4 MB.

This one is the reason the review was worth doing at all. Everything else on the
list is a 500 someone would have hit and reported; this is the one an attacker
reaches with a single small upload, and no test would ever have found it by
accident.

### One decision the review settled rather than found

**Model output is clipped; a label the user typed is refused.** The asymmetry is
deliberate and now consistent across both gates. A model asked to copy a job
title out of a resume will occasionally return the whole line it found it on —
nothing the user did caused that and nothing they can do fixes it, so shortening
it and committing beats a hard failure, which is the same call the review screen
already embodies. A label is the one field on that screen the user typed
themselves, so silently storing something else is the worse failure — and a
clipped label could collide with an existing one under the uniqueness rule.
`DraftLimits.MaxLabelLength` is the single number all three places now read.

### One thing the review looked for and did not find

The filename handling was already right: `Path.GetFileName` plus truncation, used
only as a display label, with the bytes never touching a filesystem path. That is
decision 5 paying for itself — the traversal class is unreachable rather than
mitigated.

## The real-CV test — and the extractor rewrite it forced

Run 2026-08-28 against a real two-page CV exported from a word processor, through
the live app on real Postgres and real Ollama. It is the by-hand check the
fixtures section said was needed, and it found what that section predicted.

**The pipeline worked: 201 in 15.7s, one `document_imports` row, nothing else
written.** The model reassembled employer, title and date range from three
clusters sitting ten lines apart in the extracted text — genuinely good work.

**And the extraction underneath it was wrong.** The CV is a sidebar layout, and
`ContentOrderTextExtractor` orders by the content stream, so it emitted every
date, then every section heading, then every employer, each cluster torn from the
entries it belonged to — and two columns of one line concatenated without even a
space:

```
'Learning Management Platform courseSoftware Architecture'
```

Everything downstream inherited it. Most damagingly, **all four extracted skills
were wrong** — they were LinkedIn Learning course titles that happened to sit
where skills belong, while every real skill (Python, Docker, PostgreSQL, ReactJS,
Spring Boot, AWS, OpenCV, TensorFlow, Flask) was inside body bullets and missed
entirely. For a feature whose whole point is feeding Phase 5's skills-gap join,
that is a total failure wearing a 201.

### What replaced it

PdfPig's document-layout analysis, already in the referenced package — **no new
dependency**: `NearestNeighbourWordExtractor` → `DocstrumBoundingBoxes` →
`UnsupervisedReadingOrderDetector`. Glyphs to words by spacing, words to blocks
by density, blocks to reading order. Blocks are then separated by a blank line,
which the old path could not do at all, so the model receives paragraph
boundaries instead of one undifferentiated wall.

Measured on the same CV, same model, same prompt:

| | Before | After |
|---|---|---|
| Skills | **4, all wrong** | **22, every real one found** |
| Date ranges | all in `start`, `end` null | correctly split |
| Experience entries | 3, with two projects merged | 4, separated |
| Full name | correct | **lost** |

### The segmenter was chosen by measurement, not by reading docs

`RecursiveXYCut` looked better on paper for a Manhattan layout and isolated the
sidebar more cleanly in the raw text. Through the model it was **much worse**: it
returned the "Skills and Ability" soft-skill sentences as skills and gave all
five experience entries the same employer. `DefaultPageSegmenter` preserved the
name but bled contact details back into the experience bullets.

Docstrum won on the thing that matters and lost on a field the user types anyway.
Worth stating plainly because it is the whole argument for testing through the
real surface: **the raw text that reads best to a human was not the one that
parsed best.**

### What it still gets wrong, recorded not fixed

- **Letter-spaced headings** (`M a s t e r  o f  I T`) split into single
  characters — tracking makes letter gaps as wide as word gaps, and no geometry
  distinguishes them. This is what costs the full name.
- **A narrow date column** still segments as its own block, so dates arrive
  detached from their entries. The model mostly recovers them; it did not before.
- **Employer/title pairing** is still unreliable across a sidebar.

All three are recoverable at the review screen, and none destroys which facts
belong together — which is the line. The accepted cost of the fix, stated: this
is a heuristic over geometry and it can mis-segment an unusual layout, where the
old path was merely deterministic about being wrong. A wrong **order** is
recoverable by the model and by a human; the old failure silently destroyed
**association**.

### The DOCX control, run straight after

The same person's CV as an ordinary Word document, same model, same prompt:

| | PDF (designed) | DOCX (ordinary) |
|---|---|---|
| Full name | lost | **correct** |
| Location | null | **`Murrumbeena 3163 VIC`** |
| Skills | 22, mostly right | **8, exactly the technical-skills list** |
| Employer / title | unreliable | **correct on both** |
| Date ranges | detached column | **clean** |

Nothing in the Documents module changed between the two runs. The DOCX path
walks `Descendants<Paragraph>()` and gets paragraphs, tables and list items in
document order because **a .docx still contains a document**; the PDF path is
reconstructing structure that the export threw away. Two details worth keeping:

- The skills table in that CV is a real Word table whose cells hold `● Python`,
  `● Linux` and so on. The bullet glyphs did **not** reach the stored skill
  names - the model stripped them. That was the risk worth checking, because a
  stored `"● Python"` would never join to a posting's `"Python"`, which is the
  entire premise of Phase 5, and it would fail silently.
- The soft-skills table sitting right beside it was correctly **not** treated as
  skills - the same trap `RecursiveXYCut` fell straight into on the PDF.

What the DOCX run still gets wrong is the document's own formatting, not the
parser's: `Monash University Melbourne` glues the institution to its location
because the CV puts institution, location, qualification and dates on one line
with no delimiter, and `Port Cities Outsourcing` loses its "Vietnam". The
Leadership & Activities section is dropped entirely - the draft schema has no
concept for unpaid roles, which is a schema question rather than a bug.

**So the honest guidance is: upload the .docx when you have one.** Not as an
apology for the PDF path - as the correct technical answer, with the table above
as the evidence.

### The over-fitting risk, said out loud

This was tuned against **one** document. One CV is not a corpus, and the
comparison above could be measuring what suits this layout rather than what suits
resumes. Two things limit the damage: the committed fixture
(`tests/Jobkeep.Tests/Fixtures/two-column.pdf`) is synthetic and minimal rather
than a copy of the CV, so the test pins the *principle* — columns stay separate —
and not this document's quirks; and the suite's 185 tests, including every
existing PDF assertion, pass unchanged. The next real CV should be run through it
before the choice is treated as settled.

## Security

File upload is the biggest change to this app's risk profile since it was written.
What was done, none of it expensive:

- **Size cap enforced before anything parses** (5 MB), and enforced where it says
  it is — as endpoint metadata (`multipartBodyLengthLimit` + a request size
  limit), so an oversized body is refused mid-stream rather than spooled to disk
  and then refused. The handler's own check remains for the friendly message, and
  the extractor checks the bytes actually received. See “Pre-merge review”, 9.
- **Content sniffing, not extension trust** — magic bytes decide the format.
- **Nothing reaches a filesystem path.** No file is ever written, so the
  filename-traversal class is unreachable rather than mitigated. This is the
  strongest argument for decision 5.
- **Parsers treated as processing hostile input** — a malformed PDF or zip returns
  400, never an unhandled 500.
- **Zip bombs bounded** (review finding 10). A `.docx` is a zip, so the upload cap
  does not bound the work: `MaxDecompressedBytes` (64 MB) is checked against the
  archive's declared uncompressed total *and* against the text actually extracted.
- **Invalid UTF-8 is refused**, not silently replaced with U+FFFD.
- **The list endpoint never returns résumé text**, matching the Phase 2.3 fix that
  removed `ResumeText` from the list projection.

Still open and belonging to the audit's refresh, not here: `resumes.SourceText` and
`document_imports.ExtractedText` are unbounded plaintext personal information with
no retention rule (APP 11.2), and a *discarded* import keeps its text on purpose.
`DisableAntiforgery()` on the upload endpoint is correct for an app with no auth
and no cookies, and is one of the things that must be revisited when auth lands.

## Known gaps

- **Fixed on 2026-08-29, two phases late: this route broke Swagger for the whole
  app.** `[FromForm]` on the `IFormFile` parameter is something Swashbuckle 10
  refuses outright, and it refuses by throwing rather than by skipping the
  operation — so `GET /swagger/v1/swagger.json` answered 500 and Swagger UI showed
  "Fetch error" on every endpoint in the app, not just this one. The attribute was
  redundant (a minimal API binds `IFormFile` from the multipart body without it);
  the three scalars beside it keep theirs, or they would bind from the query
  string instead.

  Worth recording as a *process* finding rather than a bug. It shipped here, the
  suite went 185 → 202 → 212 green across two later phases, CI passed every push,
  and the only human-facing surface this app has was unusable the whole time —
  found by opening it, by hand, at the end of Phase 5. The generated OpenAPI
  document had nothing watching it, which is the failure mode already recorded
  against the committed SVG diagrams: **a generated artefact with no build step
  behind it goes stale silently.** The difference is that this one *can* be
  checked, so it now is — `tests/Jobkeep.Tests/Documents/SwaggerDocumentTests.cs`,
  verified by putting the attribute back and watching both tests fail.
- **Requirement `Kind` is unreliable** (finding 2). Fix it on the review screen.
- **Real-word-processor PDFs are partly covered now.** Multi-column layout was the
  hole this bullet named, a real CV fell straight into it, and the fix plus a
  synthetic `two-column.pdf` fixture closed it — see “The real-CV test” above.
  Still open: subset fonts, ligatures, letter-spaced headings (which cost the
  full name) and a narrow date column. The by-hand check remains what covers
  those, and it is worth repeating on the next real CV.
- **Case-sensitive dedup** applies to `resumes.Label` too, deliberately (decision 2).
- **No OCR**, so a scanned PDF cannot be imported. It is detected and reported
  rather than stored as an empty resume.
- **`MaxStructureChars` truncates from the head** at 24,000 characters. It should
  effectively never fire on a real resume (a dense three-page resume is ~8,000),
  and it warns when it does. Chunk-and-merge was judged machinery in search of a
  problem at this size.

## Next

**Phase 5 — ATS compatibility check**, which consumes what this produces. Its step
1 ("store your resume text once") is now done, and the skills-gap join above is
most of its answer already.
