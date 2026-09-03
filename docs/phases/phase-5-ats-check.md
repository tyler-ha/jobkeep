# Phase 5 — ATS compatibility check

**Status: Done** (2026-08-28)

## Goal

Compare a stored resume against a job description and return matched vs.
missing keywords, plus basic formatting risk notes — without pretending
to be a mystical "ATS score."

## Plan

1. Add a way to store your resume text once (a simple field or small
   endpoint — this is a single-user tool, no need to overbuild this).
2. New endpoint: `POST /applications/{id}/ats-check`
   - Inputs: stored resume text + stored job description for that
     application.
   - Prompt the AI (same `IChatClient` abstraction from Phase 4) to
     return JSON: matched keywords, missing must-have keywords.
   - Add a small set of **static, non-AI rules** for formatting risk
     (e.g. "avoid tables/columns if uploading as PDF") — this part
     doesn't need a model call at all, keep it cheap and deterministic.
3. Deliberately skip building a "score out of 100" — real ATS systems
   vary too much for that number to mean anything, and it's the exact
   thing SEO-driven tools oversell. A clear missing-keywords list is
   more honest and more useful.

## What was actually built, and where it left the plan

Three of the four numbered items above survived contact unchanged. The
corrections are recorded here rather than by quietly editing the plan, because
*why* a plan changed is the part worth keeping.

### Step 1 was already done, by a phase that did not exist when this was written

Phase 4.5 built `resumes` and the import pipeline that fills it, so "add a way to
store your resume text once" was delivered before this phase opened. More than
that, it was delivered in a shape this phase depends on: `resumes.SourceText`
holds the verbatim extracted text, and resume skills and posting skills are rows
in the **same shared `skills` table**.

### Step 2's model call was replaced by a SQL join — the phase's main technical decision

The plan says to prompt the model for the keyword match. That is now wrong, and
the reason is a decision taken three phases earlier. Because `skills` is shared,
"what does this ad ask for that my resume never mentions" is a set difference
Postgres computes exactly, instantly and for nothing:

```sql
SELECT s."Name" FROM posting_skills ps JOIN skills s ON s."Id" = ps."SkillId"
WHERE NOT EXISTS (SELECT 1 FROM resume_skills rs WHERE rs."SkillId" = ps."SkillId");
```

A model asked the same question would be slower, would cost a call, and would not
be reproducible — Phase 4 measured `Temperature = 0` at identical output on **6 of
7 runs, not 7 of 7**. Using a sampled model to answer a question a `JOIN` answers
exactly is the kind of thing that looks like AI and is actually a regression.

This is the query the shared-skills decision was made for in Phase 2, and until
this phase it had never once been run.

The model was not removed, it was **narrowed**: it now answers only the question
SQL cannot, which is whether the resume evidences a *free-text* requirement
("5+ years of professional backend engineering experience"). That is one call,
over `job_requirements` rows and `resumes.SourceText`.

### The check has four stages and only one of them needs a model

```
1. resolve       which application, which resume        two queries
2. skill gap     posting_skills minus resume_skills     ONE query, no model
3. requirements  free-text coverage                     one model call
4. formatting    static rules over resume metadata      no query, no model
```

The consequence is deliberate and is pinned by a test: when Ollama is down, the
check **degrades** — it returns the skill gap and the formatting notes with a
warning, rather than failing. Phase 4's analyzer rethrows in the same situation
and is right to, because there the model *is* the feature. Here it is one stage
of four, and throwing away three working stages because a fourth is unavailable
would make the whole feature exactly as available as Ollama.

That warning is **stored**, in a `Warning` column added to `ats_results` beyond
what the plan listed. Without it, a result computed during an outage sits in the
database with an empty `UnmetRequirements` list and every later read of it
cheerfully reports that the resume meets every written requirement. That is the
same failure `document_imports.Warning` was added in Phase 4.5 to prevent, so it
is the same column with the same argument behind it.

### Step 3 stands, and the real-CV test made it a stronger argument

No score, as planned. What changed is the quality of the reasoning. The plan
argued from principle ("real ATS systems vary too much"). The **real-CV test** on
2026-08-28 turned it into a measurement: the same CV as a designed PDF lost the
candidate's full name, their location and every real skill, while the ordinary
`.docx` kept all three.

So the biggest ATS risk in that document was never keyword coverage — it was that
a machine reading the file could not find who the candidate was. A number out of
100 averages that away into a digit. A list of specific missing things cannot.
The same test is where the formatting rules in stage 4 come from: they are
findings this repo owns, not advice copied off a careers blog.

## Architecture decision 17 — the boundary rule is about writes, not reads

The phase's main architectural output, and the strongest interview material in it.

Ats reads `posting_skills`, `skills` and `job_requirements` (owned by
Applications) and `resumes` and `resume_skills` (owned by Documents), and writes
only `ats_results`, which it owns. Under rule 2 as originally written — *a module
only queries the tables it owns; cross-module reads go through a public
contract* — that is five violations, and the fix would have been a contract
method per question. This project has already grown that shape and deleted it
twice: as `IJobApplicationRepository` (decision 5), and as the contract
`AnalyticsModule` refused to build (decision 13).

The rule was narrowed instead, and the case is that the narrower version is what
the three existing exceptions were already saying:

- **Decision 13** let Analytics read across, and every load-bearing word of its
  argument is about being read-only: *"can never leave another module's data in a
  state that module did not choose, so the coupling is to a shape, not to a
  lifecycle."*
- **Decision 14** built `IPostingContract` for Ai **because Ai writes**
  `posting_skills`, and said so: *"the read-only exception in decision 13 does not
  cover a writer."*
- **Decision 15** let Documents call Applications' handlers because rule 2
  protects **invariants** — and an invariant is a constraint on what gets written.

Three phases, three exceptions, one distinction underneath all of them. Stating it
once is cheaper than granting a fourth exception, and it is what keeps
`IPostingContract` at two methods: its cap comment says a third method means the
boundary is in the wrong place, and a `GetPostingSkills` method would have been
that third. The boundary was never wrong — the read simply did not need guarding.

**The cost, stated rather than discovered later.** A reader still couples to
another module's *schema*, so renaming `posting_skills.IsRequired` breaks a module
that did not change — loudly, at build time, which is the cheap failure. And
extracting Ats later stops being a pure code-move: those five reads would need a
view, a read replica, or an API call. Bounded and visible, and much cheaper than a
contract-per-question, which is unbounded by construction.

Full text in `docs/architecture.md`, decision 17.

## Verification — and the result is more interesting than a pass

Run against the real dev database, on the real CV imported in Phase 4.5
(`tyler-cv-2025`, 3,262 characters, 8 skills) and a real Melbourne job ad
imported as `kind=JobPosting` (a senior .NET role at REA Group, from which the
import extracted 12 skills and 12 requirements).

**The join works exactly.** The endpoint and the raw SQL above returned the same
12 rows, split 9 must-have / 3 nice-to-have. The query the shared-skills decision
was made for has now actually been run, which it never had been.

**And it reported PostgreSQL as missing when the CV literally names it.** That is
the finding worth keeping. The CV's experience section says *"Python, XML,
Javascript, PostgreSQL"* in prose, but the resume's **structured** skill list —
extracted by Phase 4.5 — says `SQL`, not `PostgreSQL`. `.NET Core` versus the ad's
`.NET` is the same class of miss. So:

> The skill gap is exact, and it is exactly as good as the skill extraction
> upstream of it. It matches skill *rows*, not skill *text*, and it has no notion
> of a synonym, a narrower term or a broader one.

This is the same family as the case-sensitive dedup gap already recorded in
`CLAUDE.md` (`C#` and `c#` are two rows), and it should be fixed with it rather
than separately — both want a normalised natural key on `skills`, which is a
migration and so its own phase.

> **BOTH HALVES ARE NOW FIXED, in two phases rather than one.** Phase 7
> (2026-09-01) put a unique index on a stored `lower("Name")`, so `C#` and `c#`
> are one row. **Phase 14** (2026-09-03) added `skills.skill_aliases`, resolved
> inside `SkillCatalog` so no call site changed, and `.NET Core` → `.NET` is one
> of the aliases it ships with — the exact miss recorded above. `PostgreSQL` vs
> `SQL` is deliberately NOT aliased: one is an instance of the other, and merging
> them would make this check claim a match the CV has not earned. See
> `phase-14-skill-vocabulary.md`.

**What shipped instead, in this same change: a correction path.**
`POST /resumes/{id}/skills` (`src/Modules/Documents/AddSkillToResume.cs`, and the
`addSkillToResume` mutation) lets the user say *"yes, I do have this"* and have it
land as a real `resume_skills` row against the shared skill. That is not the
synonym fix — nothing here has learnt that `SQL` and `PostgreSQL` are related —
but it makes the near-miss cost one click instead of a re-import, which is the
part that was actually unacceptable. It closes a real asymmetry too: until this
slice, `posting_skills` was editable by hand on both surfaces while
`resume_skills` could only ever be written by the Phase 4.5 import cycle. It is
also what backs the CV-centre drag in the Phase 6 design.

**The two halves turned out to be complementary, not redundant.** The model,
reading the verbatim `SourceText`, correctly credited the C#/.NET and PostgreSQL
requirements the join had missed, and correctly flagged *"5+ years of professional
backend engineering experience"* and *"Solid understanding of distributed
systems"* as unmet — which is right; the CV is a junior engineer with about two
years. Three consecutive runs returned identical output.

Read plainly: the deterministic half is precise but literal, the model half is
tolerant but unverifiable, and the honest version of this feature shows you both
rather than blending them into a number. Which is the no-score argument again,
arrived at from a different direction.

**The outage path was verified by breaking the code, not by stopping Ollama.**
`FakeChatClient.Unreachable()` throws a real `HttpRequestException` through the
real handler and the real HTTP surface; making the handler rethrow instead of
degrade fails that test on its own assertion. That is a stronger check than a
manual one, because it runs on every push.

## Tests

`tests/Jobkeep.Tests/Ats/AtsTests.cs` — 17 integration tests, everything real
except the model. `tests/Jobkeep.Tests/Documents/ResumeSkillTests.cs` — 10 more
for the correction path, the last of which reproduces the PostgreSQL near-miss
above end to end and proves adding the skill clears it on the next check.
Suite is **212 green**, up from 185.

Two of them were verified the way this project now verifies a test that claims to
pin something: by breaking the code and watching them fail on the right
assertion. Breaking the shared-skills join (comparing `SkillId` against the wrong
column) failed four tests; making the outage path rethrow failed the degrade test.
Both restored, suite green.

Postings are arranged through the real HTTP surface; resumes are seeded directly.
The asymmetry is deliberate — the seed helper **looks up the existing shared skill
row and throws if it is missing**, so a test cannot accidentally create a second
"C#" and pass trivially. That guard is what puts the shared table under test
rather than assuming it.

## Deviations from the plan, in one list

| | |
|---|---|
| Step 1 | Already delivered by Phase 4.5. Not rebuilt. |
| Step 2, keyword match | Model call replaced by a SQL set difference. The model now answers only free-text requirement coverage. |
| `ats_results.Warning` | Added beyond the planned migration, so a degraded result stays honest when read back. |
| `MissingNiceToHaveKeywords` | Added: `posting_skills.IsRequired` already distinguishes them, and collapsing both into one list throws away information Phase 4 paid a model call to get. |
| `resumes.SourceFormat` | Added, populated in `CommitImport.cs`, so the format rule reads a *detected* format rather than a filename extension. Existing rows are null and get no note. |
| `OrderBy` before `Select` | Not a design choice — ordering by a property of the projected record does not translate, and fails at runtime rather than at compile time. Noted in `CheckAts.cs` so it is not "tidied" back. |

## Cost notes

Cheaper than planned. One model call per check instead of two questions in one,
and three of the four stages cost nothing at all — the whole check returned in
**1.8 seconds** end to end against a local 3B model, most of which was the one
inference. Still fractions of a cent on a hosted provider, and $0 locally.

## Interview talking points from this phase

- **Refusing the flashy feature.** No score out of 100, argued from a measurement
  rather than a principle: the real failure in the tested CV was a parser not
  finding the candidate's name, and a number would have hidden it.
- **Using a model only where a query cannot answer.** The keyword match was
  planned as an AI call and shipped as a `JOIN`, because a shared vocabulary table
  decided three phases earlier made it exact and free. Knowing when *not* to reach
  for the model is the more interesting half of using one.
- **Degrading instead of failing.** Three of four stages need no model, so the
  feature stays useful when the model is down — and the warning is persisted, so a
  later reader is not misled by an empty list.
- **Narrowing an architectural rule instead of granting a fourth exception.**
  Decision 17, and the observation that decisions 13, 14 and 15 had each been
  saying the same thing about their own case.
- **A verification that failed usefully.** The end-to-end run surfaced a real
  limitation (the join matches skill rows, not skill text) that no test would have
  caught, because every test arranges skills that match by construction.

## Next

Phase 6 — simple front end.
