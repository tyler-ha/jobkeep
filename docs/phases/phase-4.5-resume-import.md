# Phase 4.5 — Resume import and parsing

**Status: Proposed — architecture under discussion, nothing built, nothing decided.**

> Numbered 4.5 rather than inserted as a new 5 on purpose. Renumbering the phase
> docs cost 10.2M tokens the one time it was done (see `CLAUDE.md`, "Build cost"),
> and a decimal is free.

## Where this came from

Asked for on 2026-08-27: upload a resume as PDF / DOCX / TXT and parse it into
records the app can use.

It is worth noting that this is not a new idea bolted on — **it is Phase 5's step
1**, which currently reads in full:

> *Add a way to store your resume text once (a simple field or small endpoint —
> this is a single-user tool, no need to overbuild this).*

Phase 5 (ATS check) compares a stored resume against a posting, and cannot run
without one. So this either happens first as its own phase, or it happens badly
inside Phase 5 as a side quest. Doing it first is the reason for the 4.5.

## The one distinction the whole design rests on

"Parse a resume" is **two different problems** with different failure modes, and
conflating them is the mistake to avoid:

| | Extraction | Structuring |
|---|---|---|
| Job | bytes → plain text | text → records |
| Tool | a parser library | the model, via `IChatClient` |
| Determinism | **total** — same file, same text, always | none — a model, sampled |
| Failure | throws, or gives obviously empty text | silently plausible and wrong |
| Testable | yes, with a checked-in fixture file | only through a fake |

They should be **separate steps with the text persisted in between**. That buys
three things: re-parsing after a prompt change costs no re-upload; a bad
extraction is diagnosable without a model in the loop; and the two halves get the
kind of test each one actually deserves — a real fixture PDF for extraction, a
canned model reply for structuring.

This is the same shape Phase 4 already uses (`ai_analyses` is stored, and
re-analyzing is an update path, not a re-upload), so it is a pattern being reused
rather than invented.

## Decision 1 — where does a resume live? **This is the real question.**

Today `ResumeText` is a `string?` **column on `JobApplication`**. So the resume is
a property of *an application*.

That is wrong for what was asked, and it is worth being precise about why: a
resume is a property of **you**. You have perhaps two or three variants, and you
apply to thirty jobs with them. The current shape stores the same text thirty
times, gives you no way to ask "which applications used the resume I have since
improved", and makes "parse into records we can use" meaningless — records
attached to one application are not reusable.

Two options.

**A. Keep it per-application.** Zero migration, matches the code that exists.
Duplicates the text per application, and there is nowhere for parsed records to
live that makes sense.

**B. A `resumes` table, and `JobApplication` points at one.** A resume becomes its
own aggregate with a label ("backend-focused", "generalist"), its extracted text,
and its parsed records. `JobApplication.ResumeId` replaces `ResumeText`.

**Recommendation: B.** It is the shape the feature was asked for, it makes the
Phase 5 ATS check a comparison between two stored things rather than a comparison
against a column, and it unlocks the question a job tracker should be able to
answer — *which of my skills do the jobs I apply to actually ask for, and which
does my resume not mention?* That query is the whole point of having a shared
`skills` table already.

**What B costs, stated plainly:** it is a **migration**, and the first one since
InitialCreate. That means it also triggers a `schema-diagram` redraw, which
Phases 2.3–4 all correctly avoided. It also touches the existing
create/update slices, which write `ResumeText` today.

**A migration is the right moment to fix the parked case-sensitivity gap.**
`CLAUDE.md` has the skill/company dedup defect parked in "Phase 2.7 with the rest
of the audit migration", along with the missing `Status` / `DateApplied` indexes.
If this phase is opening a migration anyway, the marginal cost of folding 2.7 in
is small, and the alternative is two migrations doing adjacent work. **Worth
deciding deliberately rather than by accident** — the argument against is that it
makes one phase do two things, which is exactly what `CLAUDE.md` priority 2 warns
about.

## Decision 2 — which module owns it?

A resume is not an application, and it is not an analysis.

- **`Resumes` module**, owning `resumes` and its parsed child tables. Clean, and
  consistent with the ownership table.
- **Fold into `Applications`.** Fewer moving parts, but Applications becomes "the
  module for everything that isn't reporting or AI", which is how a modular
  monolith quietly becomes a monolith.

**Recommendation: a `Resumes` module.** With a caveat that must be faced up front,
because it is decision 14 all over again: the *structuring* step is an AI call
that writes records. If `Resumes` owns those tables and calls `IChatClient`
itself, it needs no contract — the Ai module is not involved and nothing crosses
a boundary. **That is the cheaper shape and probably the right one.** The
alternative — `Ai` owning resume parsing too — recreates the cross-module write
that `IPostingContract` exists to mediate, and would want a second contract.

The generalisable rule worth writing down if this holds: **`Ai` is not "the module
where model calls live".** `IChatClient` is a shared dependency any module may
inject, the same way `AppDbContext` is. `Ai` owns `ai_analyses` — a table — not a
technology. Getting this wrong produces a module that grows a slice every time
any feature wants a model, which is `IJobApplicationRepository` in a new costume.

## Decision 3 — which formats, and which libraries

Recommended, with the licensing checked because it is a portfolio project:

| Format | Library | Licence | Note |
|---|---|---|---|
| `.pdf` | **PdfPig** | Apache 2.0 | Pure .NET, actively maintained, no native dependency. The realistic default. |
| `.docx` | **DocumentFormat.OpenXml** | MIT | Microsoft's own. No Office install, no COM. |
| `.txt` / `.md` | none | — | Read the bytes. |

**Explicitly refuse `.doc`** (the pre-2007 binary format). There is no good free
pure-managed extractor for it, and the options are a native dependency or a
commercial library. The honest answer is a clear error telling the user to save
as `.docx`, and it costs nothing to say so. **`.doc` is the scope trap in this
phase** — it looks like one more entry in a switch statement and it is not.

Worth flagging and *not* doing: **iText7 is AGPL** unless you buy a licence. It is
the first result for "C# PDF library" and it is the wrong pick for a public
portfolio repo.

The other trap: **a PDF that is a scan has no text layer.** Extraction returns
empty, and no library fixes that without OCR, which is a different project. The
design answer is to detect it and say so — "this PDF has no selectable text, it
looks like a scan" — rather than storing an empty resume and letting the ATS
check silently report that you match nothing.

## Decision 4 — do we keep the uploaded file?

Cheapest defensible answer: **no.** Extract the text, store the text, discard the
bytes. The file's only job is to be a source of text, the text is what every
later feature reads, and re-uploading is trivial for a single user.

Keeping the original means either `bytea` in Postgres — which eats the Neon free
tier's 0.5 GB, the one resource in the deployed plan that is actually scarce — or
object storage, which is an S3 bucket and a new AWS surface in a phase that is
otherwise local. Store a filename and a hash for provenance; drop the bytes.

## Decision 5 — what records, exactly?

"Parse into records we can use" needs pinning down, because it is the difference
between a weekend and a month. Suggested minimum, and it is deliberately small:

- **Skills** — reusing the existing shared `skills` table, which is the entire
  reason it is a shared table. This is the one that pays off immediately: skills
  from your resume vs. skills from postings is a single query once both sides
  exist.
- **Contact/basics** — name, email, phone, location. Cheap and reliable.
- **Experience entries** — employer, title, start/end, bullets.
- **Education entries** — institution, qualification, year.

Do *not* try to extract a "years of experience" number or a seniority judgement.
The Phase 4 real-model check is directly relevant here: a 3B model asked for a
derived judgement returns something plausible and unfounded. Extraction of what
is literally written is what worked; inference is what produced garbage.

## What Phase 4 already proved, that this phase should not re-learn

All three are in the Phase 4 doc's "Real-model check", and every one applies
unchanged to the structuring step:

1. **Field guidance goes in `[Description]` attributes, not the prompt.** In the
   prompt, a small model echoes the instructions back as the values.
2. **Set `RequireAllProperties`.** The default schema marks nothing required, so
   `{}` is a legal reply and a small model will send it.
3. **`Temperature = 0`.** Default sampling made one run in three return nothing.

A resume is also *much longer* than a job ad, so the truncation limit and the
context window need thought rather than the copied 12,000 characters — and a
resume's important content is not all at the top, so head-truncation is a worse
answer here than it was for a job ad. This is the one place the Phase 4 pattern
does not transfer cleanly.

## Security — the first real attack surface

File upload is a bigger change to this app's risk profile than anything since it
was written, and the security audit is already due for a refresh at the Phase 3
boundary. Non-negotiables, none expensive:

- **A size cap enforced before the parse**, not after.
- **Sniff the content, don't trust the extension or the client's content-type.**
- **Never let an uploaded filename reach a filesystem path.** Nothing is written
  to disk under decision 4, which removes most of this class outright — a good
  argument for that decision beyond cost.
- Treat the parser as processing hostile input. PdfPig and OpenXml are managed
  code, which bounds this considerably, but a malformed file should return a 400
  rather than an unhandled exception.

Bounded today by the app being localhost-only with no auth — but Phase 6 is a
front end and Phase 3 is a public URL, so this is the wrong thing to leave until
it matters.

## Open questions for the user

1. **Decision 1 — per-application text (A) or a `resumes` table (B)?** B is
   recommended and is a migration.
2. **If B: fold the parked Phase 2.7 migration work in, or keep it separate?**
3. **Decision 5 — is the four-record-type list right?** Cutting experience and
   education to "skills plus basics" would roughly halve the phase.
4. Confirm `.doc` is out of scope.

## Next

Phase 5 — ATS compatibility check, which consumes whatever this produces.
