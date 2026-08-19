# Phase 5 — ATS compatibility check

**Status: Not started**

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

## Cost notes

Same cost profile as Phase 4 — this is one more small, structured AI
call per check, fractions of a cent.

## Interview talking points from this phase

- Being deliberately skeptical of a flashy but low-value feature (the
  fake "score") in favor of the version that's actually useful — a good
  product-thinking story, not just a technical one.

## Next

Phase 6 — simple front end.
