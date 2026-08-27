# Phase 4 — AI job-description analyzer

**Status: In progress** (started 2026-08-27)

> The plan below is the original. What actually happened differs from it in
> several places, and **"Deviations from the plan" is the accurate record** —
> read that section before trusting a step above it.

## Goal

Paste a job description in, get back structured info: required skills,
nice-to-have skills, seniority level, and a short summary.

## Plan

1. Develop against **Ollama** locally first — free, no API key, nothing
   leaves your machine. Pull a small model:
   ```bash
   ollama pull llama3.2:3b
   ```
2. Add the `OllamaSharp` NuGet package, or integrate via
   `Microsoft.Extensions.AI`'s `IChatClient` abstraction — this is the
   detail that matters: code the analyzer against `IChatClient`, and
   swapping between Ollama (local, free) and a hosted API (cloud,
   deployed) becomes a config change, not a rewrite.
3. New endpoint: `POST /applications/{id}/analyze` (and/or a GraphQL
   `analyzeApplication` mutation — the app now exposes both surfaces).
   - Reads the posting's `Description` field.
   - Prompts the model to return **JSON only**: skills list, seniority
     level, 2-3 sentence summary.
   - Parses the JSON and saves it into the Phase-2 relational model:
     the summary/seniority/model go into an `ai_analyses` row (1:1 with the
     posting), and each extracted skill becomes a `posting_skills` row with
     `Source = AiExtracted` (reusing the shared `skills` table). There is no
     flat `AiExtractedSkills` list any more — that was the old Phase-1 shape.
4. For the deployed (Lambda) version, swap the `IChatClient` to point at
   a cheap hosted model instead of Ollama, since Lambda can't run a
   persistent local model server.

## Deviations from the plan

### 1. Local Ollama only. Step 4 is not being done.

Decided by the user on 2026-08-27: *"only use ollama free local model."*

So there is no hosted-provider path, and the "Cost notes" section below — which
priced a hosted analysis at $0.0003–0.0008 — describes a thing this phase does
not build. It is kept for the reasoning, not as a plan.

**The consequence, recorded now rather than discovered later:** Ollama cannot run
inside a Lambda. Phase 3 is parked, so nothing is blocked today — but if it ever
unparks, the deployed build has no analyzer until a provider is chosen and paid
for. That is a known hole, not an oversight.

`IChatClient` is kept anyway. The argument for it is *not* "we might swap it
later", which would be speculative; it is that the abstraction is what keeps the
constraint **reversible** — choosing a provider later changes `AiModule.cs` and
nothing else — and it is what lets the tests run with no model at all.

### 2. Step 2's "or" was not a real choice.

`CLAUDE.md` and `architecture.md` both commit to `IChatClient`, so the two
options in step 2 are not alternatives. Both packages are used, in a stack:
`Microsoft.Extensions.AI` provides the abstraction, and **OllamaSharp implements
`IChatClient` directly** behind it — no adapter package in between.
(`Microsoft.Extensions.AI.Ollama` existed for that job and was deprecated in
favour of exactly this arrangement.)

Versions: `Microsoft.Extensions.AI` 10.9.0, `OllamaSharp` 5.4.30. Restore
reported no vulnerability advisories.

### 3. Two prerequisites were already done, which made the phase much smaller.

- **The schema already existed.** `AiAnalysis`, the `ai_analyses` table,
  `SkillSource.AiExtracted` and `SeniorityLevel` all shipped in the
  **InitialCreate** migration back in Phase 2. So Phase 4 adds **no migration**,
  and therefore does **not** trigger a `schema-diagram` redraw. Worth knowing
  that Phase 2 paid this cost in advance.
- **Ollama 0.33.0 and `llama3.2:3b` were already installed**, so step 1 was
  already complete.

### 4. The module boundary needed a decision the plan didn't anticipate.

The `Ai` module owns `ai_analyses` (per the ownership table in
`architecture.md`). But the analyze use case must **read** `job_postings.
Description` and **write** `posting_skills` rows — both Applications-owned.

Decision 13 lets Analytics read across that boundary, and the whole argument for
that exception rests on Analytics being **read-only**: it "can never leave
another module's data in a state that module did not choose". The analyzer
writes. So the exception does not stretch to cover it, and stretching it would
retire the constraint that made it defensible.

**Resolution: a narrow contract, `IPostingContract`, on Applications**, capped at
two methods — read the text, write the extracted skills. This is the same move
`AnalyticsModule.cs` explicitly rejected, so the difference is stated in the code
and worth repeating here:

- Analytics needed one method *per question*, and a reporting module has no bound
  on how many questions it has. That is how `IJobApplicationRepository` grew, and
  why it was deleted in Phase 2.3.
- Ai needs one method *per side effect on another module's tables*, and there are
  exactly two.
- A **write** boundary is where the coupling actually costs something. Reading a
  shape you don't own is recoverable; writing rows another module's invariants
  depend on is not.

The cap is enforced by a comment, not a compiler. A third method is the signal
that the boundary is in the wrong place. This needs a decision-record entry
(**decision 14**) in `architecture.md`.

### 5. A read slice was added that the plan didn't have.

`GET /applications/{id}/analysis` and the `analysis` GraphQL field. Without it
the summary and seniority are write-only — readable only in the response of the
run that produced them. The extracted *skills* were already visible in the
application detail, since they are ordinary `posting_skills` rows.

Why not simply a field on `ApplicationDetail`: that projection belongs to
Applications and `ai_analyses` belongs to Ai, so adding it there crosses the same
boundary in the opposite direction. The note in `ApplicationDetail.cs` saying the
analysis is absent because it "isn't written yet" was rewritten rather than
deleted — the reason changed, the exclusion didn't.

### 6. Two statements in this doc were wrong and have been corrected.

- *"auth on the endpoint (already added in Phase 3)"* — **Phase 3 is parked, so
  no auth exists.** Acceptable only because the app is localhost-only. If Phase 3
  unparks, the API-key middleware in its plan is a prerequisite for exposing
  this endpoint, not a nicety: inference is the most expensive thing an
  unauthenticated caller could make this app do.
- *"same idea as the repository interface in Phase 1"* — that interface was
  deleted in Phase 2.3. The talking point survives with a live comparison
  (see below); the dead one is gone.

### 7. Structured output, not prompt-and-parse.

The plan says "prompt the model to return JSON only". Implemented instead as a
JSON **schema** passed to the provider as a response format, so the model is
**constrained during generation** rather than asked politely and parsed
hopefully. Ollama supports this natively, and the call is provider-neutral —
a second reason the `IChatClient` layer earns its place.

Not via the convenient `GetResponseAsync<T>` overload, though that was the first
attempt. That helper builds its own schema with default options, and the defaults
mark nothing required — which is bug 2 in the real-model check below, and the
reason the schema is now built explicitly and the reply deserialized by hand.

One deliberate looseness inside it: `seniority` is parsed as a **string**, not
bound directly to the `SeniorityLevel` enum. A model that answers `"Mid-Senior"`
or `"Graduate"` would otherwise fail the whole parse and throw away the summary
and the skills along with it. As a string it degrades to `Unknown` and everything
else survives — the failure mode is one wrong field instead of one wasted
inference. Pinned by a test.

### 8. The tests fake the model, and only the model.

`tests/Jobkeep.Tests/Ai/` runs against the real Postgres container, the real
`Program.cs`, and both real surfaces. Only `IChatClient` is substituted.

This does not break the standing rule that integration tests beat fakes. A
language model is non-deterministic by construction, so an assertion about its
output is either vacuous or flaky. What Phase 4 can actually get wrong is all on
this side of the boundary — parsing an off-schema reply, inserting a second
`ai_analyses` row on re-run, restamping a human-entered skill as AI-extracted,
crashing on a duplicate the model emitted twice — and every one of those needs a
*chosen* model response to provoke.

**The accepted cost:** nothing in CI proves the prompt gets good answers out of
`llama3.2:3b`. That is checked by hand, and the result is recorded below.

### 9. The known case-sensitivity gap was deliberately not fixed here.

The AI skill path dedups case-**sensitively**, exactly like the human-entry path,
so `C#` and `c#` still become two rows. Fixing it only on the AI path would make
the two entry points disagree, which is worse than one consistent known bug. It
stays parked in Phase 2.7 where the migration lives.

## Real-model check — and the three bugs it caught

Run by hand against `llama3.2:3b` on 2026-08-27, via a throwaway console probe.
The tests deliberately cannot cover this, and it is the most valuable half hour
of the phase: **the first working version produced schema-valid, completely
useless output**, and nothing in the suite would have said so.

### Bug 1 — field guidance in the prompt gets echoed back as the values

First run returned `"seniority": "one of Unknown, Junior, Mid, Senior, Lead,
Principal"` and the instruction text as the summary. A small model does not
reliably distinguish the instructions from the thing it is describing.

**Fix:** move every field description out of the prompt and into
`[Description]` attributes on the draft type, where they become part of the JSON
schema the model is constrained by rather than text it can copy. The prompt keeps
only the task.

### Bug 2 — the generated schema marked nothing as required, so `{}` was a legal answer

The most expensive one, and completely invisible without looking at the wire.
`Microsoft.Extensions.AI` generates a schema with **no `required` array**, so a
model may legally return an empty object — and offered that option, a 3B model
takes it. One probe run returned a bare `{}` in 53ms. Others returned
`{"skills":[...]}` with `seniority` and `summary` simply absent.

**Fix:** `RequireAllProperties = true`. Note where it lives, because it is not
where you would look first — it is on **`AIJsonSchemaTransformOptions`**, reached
through `AIJsonSchemaCreateOptions.TransformOptions`, not a property of
`AIJsonSchemaCreateOptions` itself. Also set `DisallowAdditionalProperties` so
the model cannot invent sibling fields that get silently dropped.

Same model, same prompt, before and after: `{}` becomes a real summary and eight
correctly-flagged skills. **The schema was doing all the work.**

### Bug 3 — default sampling made it unreliable about one run in three

With the schema fixed, repeated runs over one unchanged ad still returned an
empty `skills` array roughly a third of the time.

**Fix:** `Temperature = 0`. Extraction is not a creative task. Three consecutive
runs then produced identical output. A user re-analyzing an unchanged posting and
getting a different answer would reasonably read that as a bug.

### Where it landed

Against a synthetic Melbourne senior-backend ad, `llama3.2:3b`, after all three
fixes:

| | Result |
|---|---|
| Latency | **~2.2s**, consistent across runs |
| Seniority | `senior` — correct, and **lowercase**, so the case-insensitive parse is load-bearing rather than defensive |
| Summary | 2-3 real sentences naming the company, the work and the location |
| Skills | 8 extracted, with C#/SQL/PostgreSQL/REST/CI-CD flagged required and Kubernetes/Terraform/GraphQL flagged nice-to-have — **correct on both counts** |

Residual imperfection, accepted: the skill list flickers between 7 and 9 items
across runs at the margins (`.NET` and `SQL` come and go). For a tool whose
output a human reads and edits, that is fine.

**No bigger model was needed.** A `qwen2.5:7b` pull was attempted on the
assumption that 3B was simply too small; it failed twice on DNS, and by then the
schema and temperature fixes had made the question moot. Worth recording as a
lesson in its own right: the instinct was to reach for more model, and the actual
defect was three lines of configuration.

### The three settings are correctness, not preferences

`RequireAllProperties`, `DisallowAdditionalProperties` and `Temperature = 0` live
in `AiSchema` in code, deliberately **not** in `appsettings.json` alongside the
endpoint and model tag. They are not tuning knobs; each one is the difference
between a working analyzer and one that silently stores empty rows. Config is for
things a deployment may legitimately change.

## Cost notes

- Local dev via Ollama: **$0, always.** This is the whole phase now.
- ~~Deployed version calling a hosted API: roughly $0.0003–0.0008 per
  analysis~~ — **not built.** See deviation 1.
- Guardrails: ~~auth on the endpoint (already added in Phase 3)~~ — **does not
  exist**; the app is localhost-only. See deviation 6.

## Interview talking points from this phase

- **Coding against an abstraction (`IChatClient`) so the provider is swappable.**
  The honest version of this point is not "we might swap it" — it is that the
  abstraction is what let a *cost constraint* (local models only) be adopted
  without becoming a permanent architectural commitment. The comparison to reach
  for is `AppDbContext`, which is the analogous swappable dependency that
  survived; `IJobApplicationRepository` is the one that was deleted for growing
  a method per use case, and it is the cautionary half of the same story.
- **Where a module boundary is allowed to bend, and where it isn't.** Analytics
  reads across it; Ai writes across it and got a contract instead. The
  distinguishing rule — read-only coupling is to a shape, write coupling is to a
  lifecycle — is the reusable part.
- **Making an unreliable dependency degrade instead of fail.** One unparseable
  field costs one field, not the whole inference.
- **Knowing which dependency deserves a fake.** Everything else in this suite is
  a real container on purpose; the model is the exception, and being able to say
  why is the point.
- Reasoning explicitly about cost per call and guardrails against abuse,
  not just "it works."

## Next

Phase 5 — ATS compatibility check.
