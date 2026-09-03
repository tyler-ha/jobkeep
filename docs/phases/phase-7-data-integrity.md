# Phase 7 — data integrity, and the dedup key that is already wrong

**Status: Done (2026-09-01).** One migration, `DataIntegrityAndNaturalKeys`.
Suite 228 → 239, all green.

> **Formerly "Phase 2.7".** It was referred to by that number in five documents
> and three source comments for four phases and never written, which is how it
> stayed unowned. Renumbered 2026-09-01 into build order; older *done* phase docs
> still say 2.7 and were deliberately not rewritten — they are dated records of
> what was decided then. See `architecture.md` decision 18.

## Why this one is first

Everything else on the roadmap is a flat cost: it costs the same whether it is
built now or in six months. **This one does not.** It gets more expensive along
two axes, both of which are moving right now:

- **Per write path.** F8 is a hand-maintained `UpdatedAtUtc`, and the audit caught
  it demonstrating its own finding: one stale write path in Phase 2.1 became
  *four* in the very next phase, none of which maintains the column. Every slice
  added before the interceptor lands is another write path to audit by hand.
- **Per row of real data.** The case-insensitive dedup gap is not a future defect.
  Every application the user files today can add a duplicate `C#`/`c#` row, and
  the migration that eventually merges them has to reconcile every duplicate that
  accumulated in the meantime. Deferring this does not postpone the work; it
  grows it.

And unlike every other integrity item, it is **already producing wrong output**,
in two places that are shipped and visible:

- `/stats/skill-demand` splits one skill's count across two rows. Pinned by a test
  that asserts the defect —
  `SkillDemand_SplitsSkillsDifferingOnlyInCase_WhichIsTheKnownDedupGap` — so the
  fix announces itself by breaking that test.
- The Phase 5 ATS check reported `PostgreSQL` as a missing skill against a CV that
  names it, because the résumé's structured list says `SQL`. Matching skill *rows*
  rather than skill *text* is the same root cause: there is no normalised natural
  key on `skills`.

No auth is required, no module boundary moves, and it is one migration.

## Scope

Two things that must ship together, because they touch the same indexes and
splitting them means migrating those indexes twice.

### 1. The audit & integrity baseline

Closes **F7, F8, F11, F12, F13, F14**. Lifted unchanged from
[`security-and-data-audit.md`](../security-and-data-audit.md) §5 step 1 — read
that for the evidence per finding.

- `IAuditable { CreatedAtUtc, UpdatedAtUtc }` on all 8 entities.
- **`AuditSaveChangesInterceptor`** (new, `src/Data/`), registered on
  `AddDbContext`. This is the F8 fix: one write path for the timestamps, so a
  second mutation method cannot silently skip them.
- DB-side defaults — `now()` on both timestamps, `gen_random_uuid()` on PKs (F11).
- `UseXminAsConcurrencyToken()` on `job_applications`, `job_postings`, `companies`
  (F7). Zero added columns; the update slice's read-modify-write stops being
  last-write-wins.
- CHECK constraints (F12: `SalaryMin <= SalaryMax`, ISO-4217 on `SalaryCurrency`).
- `HasMaxLength` on the eleven unbounded `text` columns (F13).
- Indexes on `Status` and `DateApplied DESC` (F14) — Phase 2.3 shipped the
  filtering and parked the indexes here so this stays one migration, and the
  query pattern has settled since.

### 2. The case-insensitive natural key

Not in the audit's step 1 — it is recorded in `CLAUDE.md` under "Known gaps" and
in three source comments. It belongs here because it is the same migration.

- A normalised natural key on `skills.Name` and `companies.Name`, and on
  `resumes.Label` — **all three, or none.** Phase 4.5 deliberately made `Label`
  case-sensitive to match the other two rather than fix one and make them
  disagree; `src/Data/AppDbContext.cs:203`,
  `src/Modules/Applications/PostingContract.cs:100` and
  `src/Modules/Documents/CommitImport.cs:115` all say so.
- A data migration merging existing duplicates. This is the part that grows with
  time, and the reason the phase is first.
- The `SkillDemand_Splits...` test flips from asserting the defect to asserting
  the fix.

## Frontend impact: **near zero**

The lowest of any phase on the roadmap, and worth stating because it is why this
one is cheap to do now:

- No response DTO changes shape. No screen adds, removes or moves a field.
- `/stats/skill-demand` stops double-counting, so the Insights bar chart gets
  *more correct* without its markup changing. `lib/chart.ts` needs nothing.
- One second-order effect worth checking rather than assuming: `HasMaxLength` on
  `Notes` and `Description` means a long paste can now 400 where it used to
  succeed. The front end already renders a 400 as a rule refusal with the API's
  own `detail` string (`ApiError.isRuleRefusal`), so this presents correctly
  without a change — but it is the one place to verify by hand.

## What actually happened

Four deviations from the plan above, and the first two are the ones worth carrying.

**1. The interceptor stamps the row that changed, not its parent — and the first
version of the F8 test got this wrong.** The test was written to add a skill to a
posting and expect `job_postings.UpdatedAtUtc` to move. It did not, and the
interceptor was right: inserting a `posting_skills` row leaves `job_postings`
`Unchanged`, so there is nothing to stamp. That reads at first like a gap against
F8, which complained that *"AddSkillToPostingAsync mutated the aggregate and saved
without touching it"* — but stamping a parent whenever any descendant changes needs
an aggregate definition this codebase has never written down, and it makes "when
did this row change" unanswerable. **The rule adopted: `UpdatedAtUtc` means this
row, not this row and everything under it.** The test now PATCHes a title, which
reaches through to the posting and modifies it for real.

**2. The natural key broke more call sites than the plan expected, and two of them
would have been outages rather than duplicates.** The plan named the find-or-create
lookups. It missed that `PostingContract` and `CommitImport` both *batch*-resolve
skills into a dictionary keyed on the raw name, and that `CommitImport` deduped
within one upload using `StringComparer.Ordinal`. Before the unique index those
produced a silent duplicate row; after it they produce a failed `INSERT`. So this
was a correctness fix riding along with a cleanup, and the lesson generalises:
**adding a constraint turns every existing near-miss from a silent defect into a
loud failure**, which is the point, but it means the blast radius is every writer,
not just the obvious one.

**3. `UseXminAsConcurrencyToken()` no longer exists.** It was removed in the Npgsql
7 provider; the replacement is a shadow property mapped to the `xmin` system
column, written once as a local helper in `OnModelCreating` and applied to the
three tables with a read-modify-write path.

**4. F13's column list in the audit is stale, and was already stale before this
phase.** It names `job_applications.ResumeText` — a column Phase 4.5 deleted when
the résumé moved to its own table. Eleven columns were bounded, but not the eleven
the audit lists. Worth noting because the audit is refreshed on a cadence, and this
is what "the standing docs lag between sweeps" looks like in practice.

**Also done, not in the plan:** the hand-set `application.UpdatedAtUtc = DateTime.UtcNow`
in `UpdateApplication` was removed. Leaving it beside the interceptor would put two
writers on one column, which is the exact shape F8 was about.

**Resumes are not merged.** The migration merges duplicate skills and companies but
*suffixes* duplicate résumé labels instead. A résumé is a document with its own
skills, experiences and educations; two files labelled "Backend" and "backend" are
two documents, and collapsing them would destroy content silently. Loud and
reversible beat clever and lossy.

## Verification

**Done, 2026-09-01 — 239/239 green.**

- The full backend suite green. **Two** tests asserted the defect, not one: the
  Analytics `SkillDemand_Splits...` test the plan named, and
  `DedupTests.SkillLookupIsCaseSensitive_...`, which the plan missed. Both broke on
  the migration and both were flipped in place, so `git log` on each method reads
  defect → fix. That is the payoff of writing known defects as executable tests:
  the fix announced itself twice rather than being noticed later.
- A new test asserting the interceptor maintains `UpdatedAtUtc` across a write
  path that does *not* set it by hand — that is the regression guard for F8, and
  it must be written against a slice that never touched the column.
- An `xmin` concurrency test: two reads, two writes, second one throws
  `DbUpdateConcurrencyException`.
- **`docs/diagrams/schema-erd.svg` — deferred within the phase, redrawn 2026-09-01.**
  The schema moved, so the trigger fired; it was deliberately not rushed at the
  expensive end of a long session, because the skill's own rule is that a diagram
  which silently omits an edge is worse than no diagram. Redrawn at the start of
  the next session instead, and the deferral paid for itself twice: the fresh
  session derived the schema from a **`pg_dump --schema-only` of the migrated
  database** rather than from `dotnet ef migrations script`, which for an
  *idempotent* script is the more honest source — that script is a sequence of
  migrations, so later `ALTER`s silently correct earlier `CREATE`s and reading the
  final state out of the text is guesswork. The dump is the applied result. It
  confirmed the numbers recorded here: **13 tables, 13 foreign keys (7 CASCADE /
  6 RESTRICT), 5 unique indexes and 12 plain**, with the three old unique indexes
  on `companies.Name`, `skills.Name` and `resumes.Label` dropped and the new ones
  on the `*Normalized` columns. `has-pending-model-changes` reports no drift.
  Two things the redraw also corrected, both stale since Phase 2: the alt text in
  `README.md` and `docs/architecture.md` still said "eight-table".

## Next

[Phase 8](phase-8-soft-delete.md) — soft delete, which needs the filtered unique
indexes this phase's natural-key work creates.
