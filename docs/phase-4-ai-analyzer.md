# Phase 4 — AI job-description analyzer

**Status: Not started**

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
3. New endpoint: `POST /applications/{id}/analyze`
   - Reads the stored `JobDescription` field.
   - Prompts the model to return **JSON only**: matched skills list,
     seniority level, 2-3 sentence summary.
   - Parses the JSON, saves results to `AiExtractedSkills` on the record.
4. For the deployed (Lambda) version, swap the `IChatClient` to point at
   a cheap hosted model instead of Ollama, since Lambda can't run a
   persistent local model server.

## Cost notes

- Local dev via Ollama: $0, always.
- Deployed version calling a hosted API: roughly $0.0003–0.0008 per
  analysis using a budget-tier model — negligible at personal-project
  volume (cents per month even with heavy use).
- Guardrails: auth on the endpoint (already added in Phase 3), and a
  spend cap/alert on the AI provider's dashboard.

## Interview talking points from this phase

- Coding against an abstraction (`IChatClient`) so the AI provider is
  swappable — same idea as the repository interface in Phase 1, applied
  to a different dependency.
- Reasoning explicitly about cost per call and guardrails against abuse,
  not just "it works."

## Next

Phase 5 — ATS compatibility check.
