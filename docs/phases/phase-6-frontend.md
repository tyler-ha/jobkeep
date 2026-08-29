# Phase 6 — the front end

**Status: in progress.** Staged, because the phase is too big to be one runnable
unit and priority 2 says not to let it sprawl. Each step below ends in something
that runs.

| Step | | Status |
|---|---|---|
| 6.1 | Backend unblock — CORS, résumé reads, the skill-removal inverse | **Done** (2026-08-29) |
| 6.2 | Scaffold the React app; lift the canvas tokens into `tokens.css` | **Done** (2026-08-29) |
| 6.3 | The eight approved screens | Not started |
| 6.4 | Root `README.md`: architecture diagram, screenshots, phase story | Not started |

## Goal

A UI so the tracker is something you'd actually use day to day, plus a polished
README for the portfolio.

## Decisions already made

The "pick Blazor or React" step is **closed**. Confirmed with the user on
2026-08-28, along with the rest of the stack:

| | |
|---|---|
| Framework | **React** — the second marketable skill was the deciding factor |
| Drag and drop | **dnd-kit** |
| Icons | **lucide-react**, plus hand-drawn SVG for the ~8 that carry the identity |
| Component kit | **None.** Hand-rolled CSS |
| Build tool | **Vite** + **react-router** — confirmed 2026-08-29 (step 6.2) |

The user asked to be **asked before any new dependency is added**. Keep doing that.

### The visual direction — "Marked Up"

Approved and published as a design canvas: eight app screens at 1440×900. The
ATS-check board is interactive; the other seven are static.

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

---

## The eight screens, and what each one calls

Recorded here because until now the screen list existed **only in the canvas
artifact**, which lives outside the repo. If that link ever rots, this is what
was approved.

| Screen | Endpoints it uses |
|---|---|
| **Today** | `GET /applications` (recent), `GET /stats/funnel`, `GET /imports` (the review queue) |
| **Applications** | `GET /applications` (filter, sort, page), `POST /applications`, `PATCH` / `DELETE /applications/{id}` |
| **Pipeline** (board) | `GET /applications`, `PATCH /applications/{id}` — the drag is a status change, and it **can legitimately 400** |
| **Job post** (detail) | `GET /applications/{id}`, skills and requirements add/remove, `POST /applications/{id}/analyze`, `GET /applications/{id}/analysis` |
| **Résumés** | `GET /resumes`, `GET /resumes/{id}`, `POST` / `DELETE /resumes/{id}/skills/...` |
| **Import & confirm** | `POST /imports` (multipart), `GET /imports`, `GET` / `PUT /imports/{id}`, `POST .../reparse`, `POST .../confirm`, `DELETE /imports/{id}` |
| **ATS check** | `GET /resumes` (the picker), `PATCH /applications/{id}` with `{resumeId}`, `POST` / `GET /applications/{id}/ats-check`, `POST /resumes/{id}/skills` (the CV-centre drag) and its `DELETE` inverse |
| **Insights** | `GET /stats/skill-demand`, `/stats/funnel`, `/stats/companies` |

## The API contract — snapshot at 2026-08-29

**A snapshot, not a live document.** It is the baseline the front end was built
against, kept so a later change can be diffed against it. When it and the running
app disagree, the app is right and this table is history — regenerate from
`GET /swagger/v1/swagger.json` rather than trusting it.

```
GET    POST   /applications
GET    PATCH  DELETE  /applications/{id}
POST                  /applications/{id}/skills
       DELETE         /applications/{id}/skills/{skillName}
POST                  /applications/{id}/requirements
       DELETE         /applications/{id}/requirements/{requirementId}
POST                  /applications/{id}/analyze          (Phase 4, calls the model)
GET                   /applications/{id}/analysis
POST   GET            /applications/{id}/ats-check        (Phase 5)
GET                   /stats/skill-demand | /stats/funnel | /stats/companies
POST   GET            /imports                            (POST is multipart)
GET    PUT   DELETE   /imports/{id}
POST                  /imports/{id}/reparse
POST                  /imports/{id}/confirm
GET                   /resumes                            (6.1)
GET                   /resumes/{id}                       (6.1)
POST                  /resumes/{id}/skills
       DELETE         /resumes/{id}/skills/{skillName}     (6.1)
POST                  /graphql
```

Four things about it that each cost a debugging cycle if forgotten:

- **Update is `PATCH`, not `PUT`.** `PUT /applications/{id}` is a 405. Linking a
  résumé to an application is `PATCH` with `{"resumeId": "..."}`.
- **Enums serialize by name** — `"Interviewing"` over REST, `INTERVIEWING` over
  GraphQL. The real `ApplicationStatus` set is Applied, Interviewing, Offer,
  Rejected, Withdrawn. There is no "Saved" or "Screening"; do not invent extra
  columns for the Pipeline board.
- **The status lifecycle is enforced** (`Models/ApplicationStatusTransitions.cs`)
  and is deliberately permissive — but an `Offer` can only be reached from an
  active application. **The Pipeline drag must handle a 400 as a normal outcome**,
  not as an error state.
- **Everything except the file upload exists on GraphQL too.** Uploading is
  REST-only on purpose; `DocumentsModule.cs` argues why at length.

---

## Step 6.1 — backend unblock (done, 2026-08-29)

Three gaps that would each have stopped or dented the front end, closed before a
line of React was written.

- **CORS.** There was none anywhere in `src/`. A Vite dev server on `:5173`
  calling the API on `:5080` is a cross-origin request, so every fetch would have
  failed before a screen painted — and it fails in a way that reads like a React
  bug, which is why this was worth finding first rather than during. A named,
  Development-only policy with the origins in config; `Program.cs` carries the
  reasoning and what auth will have to revisit.
- **`GET /resumes` and `GET /resumes/{id}`.** Résumés had no read surface at all:
  `POST /resumes/{id}/skills` was the whole of `/resumes`, and a résumé was
  otherwise reachable only through the import that created it. Both the Résumés
  screen and the ATS-check screen's résumé picker need the list. Two slices,
  `ListResumes.cs` and `GetResume.cs`. The list deliberately omits `SourceText`
  and the detail includes it — the same split `ListImports.cs` / `GetImport.cs`
  already make, and the reason is in both files.
- **`DELETE /resumes/{id}/skills/{skillName}`.** `AddSkillToResume` shipped in
  Phase 5 with no inverse, so a skill dragged onto the CV by mistake on the
  ATS-check screen could not be taken off. The approved design ships that
  interaction in both directions; the API only had one.

No migration, no schema change, so `docs/diagrams/` is untouched.

## Step 6.2 — scaffold (done, 2026-08-29)

`web/` at the repo root: Vite 8, React 19, TypeScript 6, react-router 7. Vite
because `Cors:AllowedOrigins` already named `:5173`, and because it builds to
static files, which keeps the parked Phase 3 S3 plan viable unchanged. The dev
port is pinned with `strictPort` — a silent fallback to 5174 would fail every
preflight and read like a React bug.

**No dev-server proxy, on purpose.** Phase 6.1 added a real CORS policy so the
browser makes a genuine cross-origin request in development. A proxy would hide
that behind a same-origin illusion and the first deploy would be the first time
CORS was ever exercised. Verified end to end this session: preflight returns 204
with `Access-Control-Allow-Origin: http://localhost:5173`, and the Applications
screen renders the two seeded rows.

### What the token work actually found

The plan said "lift the canvas tokens into `tokens.css`". Doing it surfaced
something the brief had not recorded: **the palette is a nine-token ramp, not
three colours.** The artboards consistently use a dark shade and a pale tint of
each hue — 145 raw hex occurrences across the eight screens, none of them named.

| | base | dark (text) | tint (surface) |
|---|---|---|---|
| Blue | `#1A5CD6` | `#0F3E96` | `#E4EDFC` |
| Green | `#0E8A5F` | `#0A6446` | `#DFF3E9` |
| Amber | `#FFC53D` | `#7A5200` | `#FFF2CF` |

That produces one rule that makes AA automatic: **on a tinted surface the label
is always the `-dark`, never the base.** Dark-on-tint passes AA text everywhere
(blue 8.29, green 6.20, amber 6.21); `--sec` on `--sec-tint` is 3.76 and fails.
It also retires a problem the palette looked like it had — `--pop` cannot carry
text at 1.58, but `--pop-dark` reaches 6.92, so amber text was always available.

Two one-off strays (`#EFECE6`, `#EDEFF3`) were collapsed into `--ground`; both
sit at ~1.07 against it, differences no eye can resolve. One token was added
that the artboards did not have: `--rule-strong` `#958C7B`, the lightest warm
neutral clearing the 3.0 non-text threshold on both grounds, because `--rule` at
1.32 is decoration and cannot be a control's only border. The first value picked
for it was chosen by eye and measured 1.87 — worth recording, since it is
exactly the mistake the ramp exists to prevent.

### The one visual change from the artboards

The **3px coloured left border was removed**, on all eight instances. The design
skill's detector flags it as the most recognisable tell of generated UI, and it
was never on-identity here: the brand's device is a marker stroke, not a tab.

- On **Pipeline** it was redundant — the card sits in a status column whose
  header already states the status in the same colour, so the border re-encoded
  what position already said.
- On **ATS check** and **Job post** it was doing real work, marking text quoted
  verbatim from the ad. There it is replaced by an actual highlighter swipe
  (`.marked` in `base.css`), which is stronger than the flat tint it sat on, is
  the brand's own device, and keeps ink-on-amber at 11.25.

The artboards were **not** re-cut. They remain the approved record; the decision
lives in the code and in this note.

## Step 6.3 — the screens

Build **Applications** and **ATS check** first. They are the two the seeded demo
data exercises end to end, including the near-miss the drag interaction exists to
fix.

## Step 6.4 — the README

Architecture diagram, what each phase added and why, screenshots, and a live demo
link **if** there is one to give.

### On hosting

An earlier draft of this plan said "host the front end on S3 as a static site".
That is still the right answer, but it is **Phase 3 work, and Phase 3 is parked**
by a deliberate decision. So Phase 6 ships a front end that runs locally; a public
URL is gated behind un-parking the deploy, not behind anything in this phase.
Don't let the README promise a link the deployment story hasn't earned.

## Interview talking points from this phase

- Full-stack ownership: one person, one project, all layers — a strong "I built
  this end to end" narrative for Ownership and Deliver Results.
- **Rejecting your own design twice.** The first two palettes were competent and
  wrong for the category — they looked like a dev tool, not something that sits
  next to Seek and LinkedIn all day. A Customer Obsession story: the fix came from
  looking at what the user actually has open in the next tab.
- **Finding the CORS gap before writing the client, not during.** The failure it
  would have caused presents as a broken front end, and the fix is in the backend.
  Reading the failure mode ahead of time is cheaper than debugging it live.

## Next

[Phase 7](phase-7-feature-expansion.md) — not a feature list, but the record of
what changes about *building* a feature once there are two halves to build.
