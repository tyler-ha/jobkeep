# Phase 2.1 — Complete the write surface

**Status: Not started**

## Goal

Finish the CRUD on the relational model so no table is a dead end. Two concrete
holes today:

1. `AddSkillToPosting` exists in GraphQL and the repository, but **REST can't
   reach it**.
2. The `job_requirements` table exists in the schema, but **nothing can create,
   list, or remove a requirement** — it's write-unreachable.

Filling these first means Phases 2.2–2.4 (querying, analytics, rules) have real
data to work against.

## Why this is Phase 2 work

The phase-2 justification for Postgres was a *rich, queryable relational model*.
A model with tables you can't populate isn't finished. This is completion, not
new scope — no new infra, no AI, still local/free.

## Scope

**Skills (close the REST gap):**
- REST `POST /applications/{id}/skills` → body `{ skillName, category?, isRequired }`,
  delegates to the existing `AddSkillToPostingAsync`. (No repo change needed.)
- REST `DELETE /applications/{id}/skills/{skillName}` → new repo method
  `RemoveSkillFromPostingAsync(applicationId, skillName)` that unlinks the
  `PostingSkill` join row but **leaves the shared `Skill`** (it may be used by
  other postings).

**Requirements (new operations):**
- New repo methods on `IJobApplicationRepository`:
  - `AddRequirementToPostingAsync(applicationId, text, kind, isMustHave)`
  - `RemoveRequirementAsync(applicationId, requirementId)`
- REST `POST /applications/{id}/requirements` and
  `DELETE /applications/{id}/requirements/{requirementId}`.
- GraphQL mutations `addRequirementToPosting`, `removeRequirement`,
  `removeSkillFromPosting` (the read side already returns them — `WithGraph()`
  in `PostgresJobApplicationRepository` already `.Include`s `Requirements`).

**Both implementations:** add the new methods to `InMemoryJobApplicationRepository`
too — CLAUDE.md keeps it as a valid no-DB fallback, so the interface must stay
fully implemented on both sides.

## Out of scope

- AI-extracted skills/requirements (that's Phase 4 — `SkillSource.AiExtracted`
  and `AiAnalysis` stay untouched here).
- Editing a requirement in place — add/remove is enough at personal volume;
  revisit only if it bites.

## Cost

Zero — local Postgres in Docker, no new packages.

## Verify locally

- `POST /applications/{id}/skills` then `GET /applications/{id}` shows the skill
  under `posting.postingSkills`.
- `POST /applications/{id}/requirements` with `kind: "Qualification"`,
  `isMustHave: true`, then confirm it appears under `posting.requirements`.
- Same operations via the GraphQL Nitro IDE at `/graphql`.

## Interview talking points

- One repository contract, two API surfaces kept in lockstep — adding an
  operation means touching the interface + both impls, which is the cost/benefit
  of the abstraction made concrete.
- Deleting a join row vs. the shared entity it points at (unlink the
  `posting_skills` row, keep the `skills` row) — a normalization consequence you
  have to reason about explicitly.

## Next

Phase 2.2 — filter, sort, and page the applications list.
