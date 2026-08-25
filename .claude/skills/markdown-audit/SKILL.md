---
name: markdown-audit
description: Audit a Markdown (.md) file for structure, clarity, completeness, syntax correctness, broken links, and consistency, checked against current industry-standard conventions for that document type (README, CHANGELOG, CONTRIBUTING, ADR, API reference, etc.). Always researches the relevant standard first via quick-research before auditing, then reports back with a terse findings list only — no long explanations. Use whenever the user asks to audit, review, lint, check, or clean up a markdown/.md file, a README, a CHANGELOG, docs, or similar — even if they don't say "audit" explicitly (e.g. "does this README look good", "check this doc for issues", "is this changelog formatted right").
---

# Markdown Audit

Two-phase workflow: **research, then audit.** Do not skip the research step —
grounding the audit in a real external standard, rather than personal
opinion, is the point of this skill.

## Step 1: Identify the document type

Look at the filename and content to classify it: README, CHANGELOG,
CONTRIBUTING, ADR/design doc, API reference, LICENSE notice, general guide,
etc. This determines what "correct" looks like.

## Step 2: Research the current standard (required, do this first)

Invoke the `quick-research` skill with a targeted query, e.g.:

- README → "current best-practice structure and sections for a project README.md"
- CHANGELOG → "Keep a Changelog format conventions"
- ADR → "current standard ADR / MADR template structure"
- CONTRIBUTING → "standard conventions for an open source CONTRIBUTING.md"

From the research output, extract only a short checklist of expected
sections/conventions for this doc type — don't carry the full research
report forward, and don't show it to the user. It's an input to Step 3, not
a deliverable.

## Step 3: Audit the file

Read the target file in full, then check it against:

- **Structure** — expected sections present, in a sensible order, matching the researched standard
- **Clarity** — ambiguous, redundant, or overly dense passages
- **Completeness** — info a reader of this doc type would expect but is missing (install steps, usage, license, etc.)
- **Markdown syntax** — unclosed code fences, broken tables, mismatched/skipped heading levels, malformed lists or links
- **Links/references** — relative links to files that don't exist, broken anchors, obviously dead URLs
- **Consistency** — heading style, terminology, tense, and formatting patterns within the file

## Step 4: Report — minimal explanation only

Output ONLY a terse findings list. No restating the file's content, no
praise, no long prose explanations of why something matters — one line per
issue, in this format:

```
[Category] Location — issue — fix
```

Example:

```
[Structure] Missing "Installation" section — add before "Usage" per standard-readme
[Syntax] L42 — code fence opened, never closed — close ``` after L47
[Link] L10 — "./docs/setup.md" does not exist — fix path or remove link
[Consistency] Headings mix "##" and "###" for same-level sections — normalize
```

Skip categories with no issues — don't write "None" lines. If the file is
clean, say so in one line and stop.
