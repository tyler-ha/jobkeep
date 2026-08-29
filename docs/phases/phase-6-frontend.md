# Phase 6 — Simple front end

**Status: Design approved, build not started**

## Goal

A basic UI so the tracker is something you'd actually use day to day,
plus a polished README for the portfolio.

## Decisions already made

The "pick Blazor or React" step below is **closed**. Confirmed with the user
on 2026-08-28, along with the rest of the stack:

| | |
|---|---|
| Framework | **React** — the second marketable skill was the deciding factor |
| Drag and drop | **dnd-kit** |
| Icons | **lucide-react**, plus hand-drawn SVG for the ~8 that carry the identity |
| Component kit | **None.** Hand-rolled CSS |

The user asked to be **asked before any new dependency is added**. Keep doing that.

### The visual direction — "Marked Up"

Approved and published as a design canvas: eight app screens at 1440×900
(Today, Applications, Pipeline, Job post, Résumés, Import & confirm, ATS check,
Insights). The ATS-check board is interactive; the other seven are static.

- **Palette — the job-board family, not tech-startup.** Two brighter,
  tech-startup-flavoured attempts were rejected before this one. Primary
  `#1A5CD6` recruiter blue, secondary `#0E8A5F` hiring green, pop `#FFC53D`
  marker amber, on a warm off-white `#F7F5F1` ground with white cards.
  Ink `#16181D`, muted `#5E6470`, rule `#E4E0D8`, alert `#D93A3A`.
- **Blue and green may take whole surfaces; amber may not.** Amber gets one held
  moment per screen plus two functional ones — the hot drop zone, and the tile
  that just landed.
- **A missing skill never uses the alert red.** Missing is a task, not an error,
  and that tone rule runs through all the copy.
- **Type:** Archivo (variable, `wdth 118`) for display, Onest for body, IBM Plex
  Mono for anything the parser counts. Google Fonts only.
- **Icon family rule:** every hand-drawn icon carries exactly one amber
  highlighter stroke through the part that matters.

The canvas artboards are the visual spec — lift exact token values from them
rather than re-deriving colours.

## Plan

1. ~~Pick Blazor or React~~ — done, see above. Scaffold the React app and lift
   the design tokens into a `tokens.css`.
2. Pages needed, kept minimal:
   - List view of applications with status.
   - Add/edit form.
   - A view for a single application showing AI analysis + ATS check
     results once run.
3. Host the front end on S3 as a static site (pennies a month).
4. Write the final root `README.md`:
   - Architecture diagram.
   - What each phase added and why.
   - Live demo link if deployed.
   - Screenshots.

## Backend work this phase depends on

- **`POST /resumes/{id}/skills` — done** (`src/Modules/Documents/AddSkillToResume.cs`,
  shipped alongside Phase 5). It backs the CV-centre drag on the ATS-check
  screen: dragging a missing skill onto your résumé is exactly this call. Before
  it existed, `resume_skills` could only ever be written by the Phase 4.5 import
  cycle, so the whole interaction was un-backed.
- **Listing résumés has no surface yet.** There is no `GET /resumes`; résumés are
  only reachable through the import that created them. The Résumés screen needs
  one, and it is a slice, not a change to an existing one.

## Interview talking points from this phase

- Full-stack ownership: one person, one project, all layers — a strong
  "I built this end to end" narrative for Ownership and Deliver Results.
- **Rejecting your own design twice.** The first two palettes were competent and
  wrong for the category — they looked like a dev tool, not something that sits
  next to Seek and LinkedIn all day. Worth telling as a Customer Obsession story:
  the fix came from looking at what the user actually has open in the next tab.

## Next

None — polish, use it for real, and start pulling stories from the
build process into your STAR log (see root `README.md`).
