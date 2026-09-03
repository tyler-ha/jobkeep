# Phase 14 — the skill vocabulary

**Status: Done (2026-09-03).** One migration (`SkillKindAndAliases`, `skills`
schema), suite 268 → 281 green, seed of 228 skills and 322 aliases verified on a
clean boot and against the real model.

---

## Why

Running the app against a real ad produced a skill list that was both too short
and duplicated. The live `skills.skills` table showed both faults at once:

```
Agile               |            ← two rows for one skill
Agile Methodologies |
Docker              |            ← two rows for one skill
containers          |
C#                  | Language   ← the only Category ever set, typed by hand
```

**Neither was a model failure.** Prompted directly, `llama3.2:3b` gives a fuller
answer and separates soft from technical of its own accord. The app was asking
the wrong question and then storing the answer in a table with no way to know two
names meant one thing.

Two causes, both ours:

1. **Soft skills were excluded by instruction.** All three model callers said
   some variant of *"Every technology, programming language, framework or tool"*.
   A model told to list technologies lists technologies. `ResumeExtraction.Skills`
   was a bare `List<string>` with nowhere to put a kind even if it found one.
2. **The catalogue had no notion of a synonym.** Phase 7's natural key
   (`NameNormalized`, `lower("Name")`) makes `C#` and `c#` one row — the whole of
   what a `lower()` index can do. It cannot know that two *different* strings name
   one thing.

This is the recorded second half of a known gap. `phase-5-ats-check.md` found it
against the real CV — the check reported `PostgreSQL` missing when the CV names
it, because the résumé's structured list said `SQL` — and said it should be fixed
together with the case key *"rather than separately"*. Phase 7 shipped the case
half. This is the other half.

---

## What shipped

### `SkillKind` — a second axis, not a replacement

`SkillKind { Unknown, Technical, Soft }`, in `Jobkeep.Contracts/Shared/SharedEnums.cs`
alongside `ApplicationStatus` and `SkillSource`. It is there for the reason those
two are: it rides on `SkillInfo`, so it reaches the response DTOs of three modules,
and two CLR enums of one name is a GraphQL schema **build** failure. The 13.3b test
— *copy only when one side is unpublished* — says share it.

`Category` is untouched and still means the **family** (`Language`, `Cloud`,
`Practice`). The two axes are independent: C# is Technical *and* a Language.
Writing "Technical" into `Category` would have spent the family axis to buy the
kind axis, and Insights renders `Category` beside the skill name, so the loss
would have been on screen.

Non-nullable with a database-side default of `Unknown`, unlike `Category`, which
is nullable. The asymmetry is the point: a category is genuinely optional, a
classification is not — every skill has one whether or not we know it, so the
missing case is a value rather than a null and callers switch on it without a
null check.

### `skills.skill_aliases` — one table, resolved in one place

```
Id               uuid PK
SkillId          uuid → skills.skills(Id) CASCADE   (intra-schema; crosses nothing)
Alias            varchar(100)
AliasNormalized  varchar(100) GENERATED ALWAYS AS (lower("Alias")) STORED, UNIQUE
```

The generated column mirrors `skills.NameNormalized` deliberately — same Phase 7
mechanism, same `NaturalKey.Of` as the C# half, same invariant that the two must
agree or a lookup misses a row the index then refuses to insert.

**Resolution lives entirely inside `SkillCatalog`, and no call site changed.**
That file was already *"the only place in `src/` that calls `NaturalKey.Of` on a
skill name"*, and this keeps it that way. All five find-or-create callers got
aliasing for free — which is the argument for putting it there rather than in
five places.

**Skill first, alias second, and the ordering is load-bearing.** An alias that
collides with a real skill row is therefore inert rather than harmful. The seeder
enforces the invariant properly; the lookup order is what makes a hand-broken
invariant not matter. `FindOrCreateAsync` asks the alias table only about the
names that missed, so the warm path is still one round trip.

### The seed — JSON, embedded, idempotent

`src/Jobkeep.Modules.Skills/skills-seed.json`, an embedded resource read by
`SkillSeeder` immediately after the Skills migration. **228 skills — 184
Technical, 44 Soft — and 322 aliases**, weighted to what Melbourne .NET/backend
ads name.

Not `HasData`: that wants a fixed GUID per row in source and makes every edit a
new migration. This list is edited every time an ad uses a word we have not met,
so it belongs in data, not in schema history.

Three rules, all conditional, which is what makes running it on every boot safe:

- An existing skill is never re-inserted and never renamed.
- `Kind` and `Category` are filled **only** when unset (`Unknown` / null). A row a
  human categorised outranks the file — the same "first writer names it" rule
  `SkillRequest` has carried since 13.2.
- An alias colliding with a skill name or another alias is **skipped and logged**,
  never thrown. Reference data with one bad row must not stop the app booting.

**That logging paid for itself immediately.** The first run reported three
skipped aliases — `DotNet`, `Javascript`, `K8S` — all case-duplicates of names
already in the file, invisible on reading it because the natural key is
case-insensitive. Three data bugs found by the mechanism designed to tolerate
them.

### Extraction: ask for soft skills, and for which is which

- **Posting and analyser**: a `SkillKind Kind` property on the per-item object,
  which already existed for the `Required` flag. Because both schema builders
  carry `JsonStringEnumConverter`, this emits a JSON Schema `enum` of *names* —
  constrained decoding makes an invalid answer unrepresentable rather than
  discouraged.
- **Résumé**: a second list, `SoftSkills`. Not a per-item tag, and the asymmetry
  is deliberate — `ResumeExtraction.Skills` is a `List<string>` that reaches the
  wire as `ResumeDraft.skills: string[]`, so a second list is additive where
  changing the element type is breaking. It is also the shape the model already
  produces unprompted.

---

## Deviations from the plan

### `AiSchema` had the field-order trap the Documents module documented

`AnalyzePosting.cs` built its schema with no `serializerOptions` at all, so
without a change the new enum would have emitted an **integer** and the model
would have been guessing ordinals. Adding the converter meant moving
`AiSchema.Json` **above** `AiSchema.Schema`: static field initialisers run in
declaration order, so a `Json` declared below would have been null when `Schema`
read it — silently, with the constraint simply absent.

`StructuringSchema` carries a comment warning about exactly this. This is the
second instance, which is what makes it a trap rather than an anecdote.

### The seeder is switched off under test, and that is not a knob

Not planned, and forced by a genuine conflict. The suite runs in Development (so
the real migration path is exercised) and Respawn truncates every table between
tests — so the vocabulary re-materialised after every reset and put 228 reference
rows inside every unrelated arrange. Three existing tests broke, one subtly:
`Check_SortsSkillNamesCaseInsensitively` seeded `aws` and got the seed's `AWS` row
back, because the natural key makes them one.

`Skills:SeedOnStartup` defaults true and is set false in exactly one place,
`JobkeepAppFactory`. It is a test seam, not tuning: Respawn's contract is that
each test starts from empty, and reference data that reappears after every reset
breaks that for every future test, not just the three. Coverage is unaffected —
`SkillVocabularyTests` calls the seeder directly, which is also the only way to
assert idempotency.

### `ResetIsolationTests` asserted an exact migration count

It asserted each schema's `__EFMigrationsHistory` held exactly **1** row. Skills
now has two, and the failure was a false alarm: the property under test is that
the history *survives the reset*. Changed to `> 0` with the reason written down —
an exact count turns every future migration into a broken test in a file about
Respawn configuration, which is how a test stops being read and starts being
edited until it passes.

### The model returns PHRASES, and that is fixed at the source

The first end-to-end run against a real-shaped ad produced `Excellent
communication skills`, `Proven stakeholder management`, `Strong problem-solving
ability`, `Mentoring junior engineers` and `CI/CD pipelines` — the ad's own
wording, adjectives and all. Each became its own row, because **a catalogue
cannot alias its way out of an open set of sentence fragments.**

The fix is one line in the `[Description]`, not fifty aliases:

> *"The name of the skill itself, not the sentence it appears in. Write
> "Communication", not "Excellent communication skills"; "CI/CD", not "CI/CD
> pipelines"."*

Re-run on the same ad: **five unmatched became two.** `Excellent communication
skills` → `Communication`, `Proven stakeholder management` → `Stakeholder
Management`. The residue (`Mentoring junior engineers`, `Problem-solving
ability`) is accepted: those are ad phrasing rather than name variants, an extra
row for them is harmless, and the alias table is where a recurring one gets
absorbed. Only `CI/CD Pipelines` was added as an alias, because that genuinely is
what people call it.

---

## Verified

- Suite **281 green**, up from 268. Thirteen new tests in
  `tests/Jobkeep.Tests/Skills/SkillVocabularyTests.cs`.
- `has-pending-model-changes` clean on all five contexts.
- `npm run build`, `npm test` (49) and `oxlint` clean, bar the pre-existing
  `set-state-in-effect` warning in `AtsCheck.tsx:193`.
- **Clean boot from a dropped volume**: `228 skills added, 321 aliases added, 0
  aliases skipped`. Restart: `nothing to do, 228 skills already present`.
- **End to end against `llama3.2:3b`** with a real-shaped Melbourne backend ad,
  uploaded and confirmed through `/imports`. 16 skills linked, and every alias in
  the ad resolved to its canonical row:

  | The ad said | It linked to |
  |---|---|
  | `.NET Core` | **`.NET`** |
  | `Docker Containers` | **`Docker`** |
  | `Agile Methodologies` | **`Agile`** |
  | `Willingness to learn` | **`Continuous Learning`** |
  | `Excellent communication skills` | **`Communication`** |
  | `Proven stakeholder management` | **`Stakeholder Management`** |

  Six soft skills were extracted where the old prompt would have returned none.

  **One result worth keeping:** the model labelled `Scrum` as **Soft**, and the
  seeded row won — it is stored Technical/Practice, because Kind is set on create
  and never overwritten. The seed corrected the model, which is the whole reason
  "first writer names it" is the rule and the seed runs first.

---

## Not done, deliberately

**The four duplicate rows that predate this phase are not merged.** Aliases apply
to resolution going forward; `Agile` and `Agile Methodologies` both survive in a
database seeded before the phase, and the seeder logs the refused alias so it is
visible rather than silent. There is a test pinning that behaviour.

Merging would mean rewriting `posting_skills` and `resume_skills`, which belong
to Applications and Documents — Skills cannot write them, so it needs contract
methods or a data migration per owning module, roughly doubling the phase. The
user's dev database is dropped often enough that the cost is a re-import.

**`Kind` is not on the stored skill responses** (`PostingSkillResponse`,
`ResumeSkillItem`), only on the drafts. Nothing renders it yet, and an unread
field on the wire is schema nobody can safely remove later — the same call
`IPostingContract` made about requirement `Kind`. Adding it is additive when a
screen wants it.

**Diagrams not redrawn.** A new table and a new column in the `skills` schema
would have been a redraw trigger before 2026-09-02; diagrams are frozen until 1.0
ships on master. Debt recorded here so the eventual redraw is a list rather than
an investigation: **`skills.skill_aliases` is new, and `skills.skills` has a
`Kind` column.**

---

## The ATS check is misnamed — recorded here, not fixed

Raised by the user while planning this phase, and confirmed in the code.
`CheckAts.cs:17` lists four stages: resolve, **skill gap vs the ad**, **free-text
requirement coverage vs the ad**, formatting risks. Only the last — plus
contact-detail detection — is a real ATS check. Three quarters of the feature is
job-ad matching under the wrong name.

The market splits these cleanly. **ATS-friendliness** (Jobscan, Resume Worded) is
CV-only: is there a real text layer or is it an image; do tables, columns, text
boxes or header/footer content mangle the parse; are contact details in the body
rather than the header; are section headings standard; is the file type and date
format parseable. No job ad is involved. **Match rate** (Jobscan's core, Teal) is
the CV-against-one-ad comparison.

The file already argues the point without naming it: *"the biggest ATS risk in
that document was never keyword coverage — it was that a machine reading the file
could not find who the candidate was"* — and then treats that as one stage of four.

**Decided: the ad-comparison feature becomes "Match check", in its own phase.** It
touches the module name, `ats_results`, the route, both API surfaces and the
screen, and Phase 13.5 is about to rewrite those same endpoints. Doing both at
once fights itself. Also in `docs/backlog.md`.

**Consequence of THIS phase that the rename does not remove:** soft skills now
appear in the gap list, so an ad wanting "stakeholder management" that the CV does
not name is a reported gap. That is wanted — it is usually the more actionable
gap, a wording fix rather than a year of learning — and `Kind` is on the row if it
ever needs filtering.

---

## Interview talking points

- **The bug was in the question, not the answer.** The model could always find
  soft skills; three `[Description]` attributes told it not to. Worth telling
  because the instinct is to blame the small model.
- **Where a normalisation rule lives decides how many places can get it wrong.**
  Phase 7 put the case key in the database; 13.2c put it in one class; this phase
  extended that class to aliases and changed no call site. Five callers got the
  fix by not being involved.
- **A tolerant mechanism found its own data bugs.** The seeder skips-and-logs
  rather than throwing, and the first run reported three case-duplicate aliases
  that reading the file would not have shown.
- **"First writer names it" let the seed correct the model.** The model called
  Scrum a soft skill; the seeded row already said Technical, and won.
- **Knowing when to stop aliasing.** Five unmatched phrases could have been five
  aliases. One line of instruction fixed three of them at the source, and the rest
  were left as rows — a catalogue cannot alias its way out of an open set of
  sentence fragments.
