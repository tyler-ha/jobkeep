# Phase 6.6 — the ad goes somewhere

**Status: Done (2026-09-01).** Front end only. No migration, no new endpoint, no
new dependency. Suite 46 → 49 green, `tsc` clean, `oxlint` clean bar the
pre-existing `AtsCheck.tsx:193` warning.

Numbered 6.6 for the same reason 6.5 was: it finishes work Phase 6 shipped
incomplete, and renumbering 7–12 to accommodate a two-file fix would cost more
than it is worth.

---

## The report

Second piece of real feedback the front end has had, and the first that came from
someone trying to *use* it rather than look at it. Paraphrased from the session:

> I upload a job ad and I add a job application with this note \[a full Airwallex
> Backend Engineer advertisement pasted into the form]. It doesn't read the job ad
> and fill the skill correctly. Tell me why.

The ad was a real one: ~5,500 characters, of which the technology list —
`Java, Kotlin, Go, Python, Spring Boot, AWS, GCP, Kubernetes, Grafana, Prometheus,
Splunk` — occupies maybe 200, in parenthesised `e.g.` lists, at the bottom, behind
company valuation, investor names, an "Attributes We Value" section, a recruitment
fraud policy and an EEO statement.

**The skills came back empty.** The model was never called.

---

## What was actually wrong

Three defects, none of them the model's fault, and only one of them visible from
the code without running it.

### D1 — the add form collected the ad into a field nothing reads

`JobApplication.Notes` and `JobPosting.Description` are two different fields on two
different tables with two different owners. The distinction is correct and it is
original — `Models/JobPosting.cs:24` has carried the comment `// raw pasted ad
text` since the init commit.

- The **analyser** reads `Description`. `AnalyzePosting.cs:149` reads
  `content.Description` and refuses before reaching Ollama if it is empty.
- **Nothing reads `Notes`.** Not the analyser, not the ATS check, not the
  extractor. It is your commentary on the application, by design.

The add form (`Applications.tsx`) collected Company, Role, Location, Link to the
ad, and **Notes**. No description. So the only textarea on the form — the only
control shaped like "somewhere to put prose" — was wired to the field that is a
dead end. Someone pasting an advertisement finds the box that looks right and gets
nothing, silently, with no error to explain it.

### D2 — the job post told you to paste the ad in and gave you nowhere to paste it

`JobPost.tsx`, empty-description state, as shipped:

> *"No description was saved with this one. Paste the ad in and the analyser has
> something to read."*

That was a bare `<p className="quiet">`. There was no input, no button, no edit
mode anywhere on the screen. `description` was reachable only through a
hand-written `PATCH /applications/{id}` or by going back through the upload
pipeline — and the upload pipeline creates its *own* application (see D3), so it
was not a repair path either.

**A screen instructing an action it does not provide is the worst of the three**,
because it converts a missing feature into a user believing they did something
wrong.

### D3 — pressing "Analyse the ad" destroyed the screen

`analyse()` sent every failure to `setError`, and `setError` short-circuits the
whole component render into a `Failure` card. `AnalyzePosting` returns **400** with
`"This posting has no description to analyze. Add one first."` — a rule, not a
fault — so on exactly the applications D1 and D2 produced, the one button that
looked like it would help replaced the entire detail view with an error.

`setStatus()`, fifteen lines above it in the same file, already did this correctly:
it routes `err.isRuleRefusal` to an explained in-place banner. Two handlers, one
convention, one of them applied.

---

## Where each one came from

The point of this section is that **none of these was a wrong decision.** Each is a
correct decision from one cycle meeting a correct decision from another with
nobody standing where the two met.

| # | Introduced by | The decision that was right on its own |
|---|---|---|
| D1 | **Phase 6.3**, `18bbe07` (2026-08-31) | The add form was designed as a *quick log* — "Company and role are all that is required". That is the right shape for logging an application you just applied to on your phone. It is the wrong shape for the case where you have the ad in your clipboard, and nobody wrote down that the second case existed. |
| D2 | **Phase 6.3**, `18bbe07` — same commit | The Job post screen was written assuming ads arrive through the **Phase 4.5 import pipeline**, which is true for uploaded ads and puts the full extracted text in `Description` (`CommitImport.cs:323`). The empty state was written as an instruction to a user who would never see it. Both screens were built in the same commit; the assumption held on one and not the other. |
| D3 | **Phase 6.3**, `18bbe07` — same commit again | `isRuleRefusal` exists because **Phase 2.5** made the status lifecycle refuse moves, and the Pipeline board had to render that as normal. So the refusal convention was invented for a refusal that was already known. `AnalyzePosting`'s 400 shipped in **Phase 4** (2026-08-27), four days earlier, and no screen could reach it until 6.3 — by which point it was nobody's live case. |

There is a fourth entry that is not a defect but is load-bearing:

**`CreateApplication.cs:19-21`, written in Phase 2.3, says:**

> *"Kept intentionally small — company, title and a few optional posting fields.
> Skills and requirements are attached afterwards through their own slices, and
> Phase 4's analyzer fills in the rest."*

That sentence is true and it is also the trap. The analyser fills in the rest
**from `Description`**, so `Description` is not one of "a few optional posting
fields" — it is the input to everything the sentence promises. The backend was
never wrong: `CreateApplicationRequest` has taken `Description` since Phase 2.1,
and `UpdateApplicationRequest` still does. The front end simply did not send the
one optional field that is not optional in practice.

### Why no test caught it

Because there was nothing to catch. Every defect here is a *missing* thing:

- `screens.test.tsx` mounts each screen and asserts it renders. A form with a
  field missing renders perfectly.
- The backend suite is green and correct. `POST /applications` accepts a
  description; it was simply never sent one.
- `tsc` is satisfied — `description` is optional on `CreateApplicationRequest` in
  `lib/api.ts`, correctly, because it is optional on the wire.

And the reason nobody noticed by eye is already recorded in
[`phase-6.5-upload-experience.md`](phase-6.5-upload-experience.md): **Phase 6.3
built all eight screens and nobody opened them in a browser.** The Chrome
extension has been disconnected since 2026-08-28. Phase 6.5 was the first feedback
the front end received; this is the second, and both came from a human using the
app rather than from any check in the repo.

**The lesson worth carrying:** a test suite pins what the code does. It says
nothing about what the code was supposed to offer and didn't. Only use finds that,
which is the argument for shipping a screen to a human early rather than for
writing more tests of the screens that exist.

---

## What was NOT wrong: the model

Worth stating so it is not "fixed" later by someone reading only the title.

Nothing was truncated. The ad is ~5,500 characters, comfortably under both
`AiOptions.MaxDescriptionChars` (12,000) and `DocumentOptions.MaxStructureChars`
(24,000). The prompt is already correct — `DocumentStructurer.cs:226` says *"List
every technology named anywhere in it, including the ones in the
responsibilities"*, temperature is 0, the schema is constrained, and the document
is fenced.

But this ad is a genuinely hard case for `llama3.2:3b` on CPU: the signal is ~4% of
the text, it is at the end, and it is inside `(e.g., …)` lists. Partial extraction
is the expected outcome, and it is the same family as the finding already recorded
from Phase 5 — **the skill gap matches skill *rows*, not skill *text*.**

That is a model-quality question, it is not what this phase is about, and the
correction path already exists (`addPostingSkill`, and a hand-typed skill outranks
an AI-extracted one — `AddExtractedSkillsAsync` refuses to restamp).

---

## The fix

### `web/src/routes/Applications.tsx`

- **"The ad" textarea**, `rows={8}`, full width, above Notes — deliberately the
  largest control on the form. Sent as `description`.
- **"Notes" relabelled "Your notes"**, with a hint saying so: *"Yours, not the
  employer's. Nothing reads these — they are for you."* The label was the trap;
  saying what the field is for is most of the fix.
- The form's lede now points at the Upload screen as the other way in.

### `web/src/routes/JobPost.tsx`

- **The ad panel is editable.** A "Paste the ad" / "Edit the ad" button in the
  panel head opens a `rows={14}` textarea prefilled from what is stored, with a
  character count — the count because a 3B model reading 5,000 characters of
  company history to find ten technologies is the case that started this, and
  saying the size out loud is one line.
- Saving `PATCH`es `description`. The endpoint always accepted it.
- The empty state now links to `/upload` as well as offering the box.
- **`analyse()` routes rule refusals to the in-place banner**, matching
  `setStatus()`. The banner's lead moved into state (`{ lead, message }`) because
  it now has two callers and *"Not a move this application can make"* is wrong for
  the second one.

### `web/src/styles/screens.css`

- `.ad-head-actions` — the ad panel's head carries two actions now where it
  carried one. Nine lines. No new tokens.

### Tests — `screens.test.tsx`, 46 → 49

Three, and each pins the *wiring* rather than the markup, because markup is what
the existing tests already cover and wiring is what broke:

1. The add form's ad box sends `description`, and Notes still sends `notes` — the
   two fields stay distinct.
2. The job post's edit box prefills from what is stored and `PATCH`es
   `description`.
3. **An analyser 400 is explained in place and the screen survives** — asserted by
   finding the "The ad" heading still on the page afterwards.

`fixtures.ts` gained a `POST /applications` branch and a 400-returning
`POST /applications/{id}/analyze` branch. Both are in the shared `stubFetch`, which
throws by name for unrecognised paths, so this had to be done for the suite to run
at all.

---

## Deliberately not fixed

- **An upload cannot be attached to an existing application.** Confirming a job-ad
  import calls `CreateApplicationHandler` itself (`CommitImport.cs:306`), so
  uploading the ad *and* logging the application by hand produces two rows for one
  job — which is exactly what happened in the report. Fixing it means a target
  application on the confirm step and a duplicate check, which is a backend
  change, a screen change and a decision about what "the same job" means. Logged
  in [`../backlog.md`](../backlog.md); it belongs with Phase 8's soft delete,
  which is where duplicate rows get a cheap answer.
- **The other seven screens' visual pass** and **step 6.4 (the README)** are still
  Phase 6's outstanding work, and still blocked on someone looking at the app.
- **Nothing here was verified in a browser.** Sixth session with the Chrome
  extension disconnected. The tests pass and the types check; whether the panel
  looks right is unverified and is stated as unverified.
