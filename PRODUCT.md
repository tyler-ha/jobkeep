# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

The backend is an existing codebase and answers for itself: ASP.NET Core on
`net10.0`, PostgreSQL via EF Core, REST + GraphQL (HotChocolate) on one Lambda-
targeted deployable. See `docs/architecture.md`.

The **front end is greenfield**, and the stack was confirmed with the user:

- **React** — chosen 2026-08-28. The deciding factor was that it is a second
  marketable skill, not a technical comparison against Blazor.
- **Vite** + **react-router** — confirmed 2026-08-29. Vite because
  `src/appsettings.Development.json` already allows `http://localhost:5173` in
  `Cors:AllowedOrigins`, and because it builds to static files, which keeps the
  parked deploy plan (S3 static hosting, $0; now Phase 10) viable unchanged. Accepted
  trade-off: no SSR, and routing is an explicit dependency rather than free.
- **dnd-kit** for drag and drop, **lucide-react** for icons, and **no component
  kit** — CSS is hand-rolled.

**Standing rule: ask the user before adding any new dependency.** This has been
asked and honoured every time so far and is not a formality.

## Users

**One user: the project's author.** A developer with beginner-to-intermediate C#
skills, actively learning AWS, preparing for a .NET job search in the Melbourne
market roughly a year out. There is no second user, no multi-tenancy, and no auth
today — this is recorded as a deliberate known gap, not an oversight.

The job being done splits in two, and they pull in different directions:

1. **Track applications day to day.** Record an ad, tag its skills and
   requirements, move it through the status pipeline, find it again later, and
   see what it all adds up to.
2. **Serve as portfolio evidence.** Every significant decision must be one the
   author can *defend out loud* in an interview, including the trade-off
   accepted. The repo also doubles as source material for behavioural (Leadership
   Principle style) stories — see the STAR log in the root README.

**The tiebreak, confirmed 2026-08-29: when daily usefulness and portfolio impact
conflict on a screen, the tool wins.** Optimise for actually tracking
applications in it — density, speed, keyboard paths. A screen that demos well but
would not be used daily is the wrong answer. This resolves an ambiguity CLAUDE.md
names but never settles.

The realistic usage scene is a second browser tab, open beside Seek, LinkedIn and
Indeed, during an active search. That scene already drove one design decision:
two brighter "tech-startup" palettes were rejected because they looked like a dev
tool rather than something that sits next to those sites all day.

## Product Purpose

Keep the state of a job search in one place, and answer questions the author
cannot answer from memory or a spreadsheet: which skills the market is actually
asking for, where applications are stalling, and whether a given résumé is
missing anything a given ad requires.

Success is that the tool is genuinely used during a real job search, and that the
build stands up as evidence of full-stack ownership.

## Positioning

The mechanism a neighbouring product could not truthfully copy is **how the
résumé-vs-ad gap check is computed**: three of its four stages are a SQL set
difference over a shared `skills` table — exact, instant, and free. A language
model is used **only** where a query genuinely cannot answer, which is free-text
requirement coverage.

Two consequences follow, and both are the point:

- The check **degrades** during a model outage rather than failing. The degraded
  warning is *stored*, because an unstored one would let a later read of an empty
  `UnmetRequirements` claim every requirement was met.
- The gap matches skill **rows**, not skill **text**. Verified against a real
  Melbourne ad, this reported `.NET` as missing when the CV said `C#`. That is a
  known, recorded limitation with a shipped correction path (drag the skill onto
  the CV), not a bug to rediscover.

The stated non-position: the dual REST + GraphQL surface is a **portfolio**
choice, not an industry norm. No comparable product ships a public API. Do not
let any copy imply otherwise.

## Operating Context

- **Local-first.** Postgres in Docker, Ollama (`llama3.2:3b`) for the model, both
  free. The app auto-migrates on startup in Development only.
- Backend on `http://localhost:5080`; Swagger at `/swagger` and the GraphQL Nitro
  IDE at `/graphql`, both Development-only. Front end will serve on `:5173`.
- **Deployment is parked, not blocked** (Phase 10, formerly Phase 3). The plan is complete, costs
  $0/month, and nothing about it expires. The front end therefore ships running
  locally; a public URL is gated behind un-parking the deploy.
- Work proceeds in **phases that each end in something runnable**, because the
  author has a history of abandoning projects when scope goes fuzzy. Phase 6 is
  the front end, staged 6.1–6.4 for exactly that reason.

## Capabilities and Constraints

**Built and working** (the API contract is frozen in
`docs/phases/phase-6-frontend.md` at the `checkpoint/backend-complete` tag):
applications CRUD with company dedup, posting skills and structured
requirements, a filter/sort/page list, three `GROUP BY` analytics slices, AI
extraction from an ad, document import with a confirm step, résumé storage and
reads, and the ATS check. 228 tests green.

**Constraints that bind future work:**

- **Cost stays near-zero. Nothing in the deployed architecture may bill per
  hour.** This is a hard rule, produced by the deploy phase's decision to drop RDS for
  Neon's free tier. Never propose always-on infrastructure without flagging cost
  explicitly. The AWS account has **no free tier left**, so "t3.micro is free"
  advice does not apply.
- **Update is `PATCH`, not `PUT`.** `PUT /applications/{id}` is a 405.
- **Enums serialize by name** — `"Interviewing"` over REST, `INTERVIEWING` over
  GraphQL. The real status set is Applied, Interviewing, Offer, Rejected,
  Withdrawn. There is no "Saved" or "Screening"; do not invent pipeline columns.
- **The status lifecycle is enforced and deliberately permissive.** Closed states
  are not terminal — a user may move a job back out of Rejected, because Huntr
  and Teal both allow it. The one surviving invariant is that **an Offer can only
  be reached from an active application**. Therefore the Pipeline drag **must
  treat a 400 as a normal outcome**, not an error state.
- **Everything except file upload exists on GraphQL too.** Upload is REST-only on
  purpose.
- A module may **read** another module's tables; only a **write** needs a
  contract (architecture decision 17).

**Known and recorded — do not re-discover:** no auth, no health check. Three
items that used to sit on this line are fixed: case-sensitive dedup and the
missing `Status` / `DateApplied` indexes went in Phase 7, and `compose.yaml`
landed 2026-09-01. The rest are in the gap register in `docs/architecture.md`.

## Brand Commitments

Name: **JobKeep**. The earlier visual direction, "Marked Up" (recruiter blue /
hiring green / marker amber, with a hand-drawn highlighter-stroke device), was
**superseded 2026-09-04** at the user's explicit request — a job search is
stressful, and the tool should read as encouraging rather than municipal. The
highlighter-stroke identity device was dropped outright rather than
recoloured.

The palette went through two rounds the same day. First the user asked for it
to be chosen for them (*"engaging, clear, bright... encourage"*), which
produced a violet/emerald/gold direction. The user then reviewed four further
candidate directions (shown as mini nav-rail previews) and **chose a fifth
instead: the "HealthRate" reference image** shown earlier in the session — a
cream-and-lime health dashboard. What follows is that final pick; the violet
round is superseded in turn, not layered underneath it.

Recorded here because the user made this binding the same way the previous
direction was — **not a standing invitation to re-derive it again per
screen.** A future change of this scope should again be a deliberate,
user-confirmed decision, not incremental drift.

- Palette — teal (`--pri`, the primary/action colour), moss green (`--sec`,
  "growth" / Offer), lime (`--pop`, "encourage", the one held-moment colour),
  on a warm cream ground with white cards: primary `#0B6E63`, secondary
  `#4A7C2F`, pop `#C7D92E`, ground `#F7F2E6`. Ink `#16181D`, muted `#5E6470`,
  rule `#E4E0D8`, alert `#D93A3A` — the neutrals and alert are unchanged from
  the original "Marked Up" values; `--rule-strong` was re-measured and
  darkened to `#8A8171` because this ground is warmer/darker than the
  intermediate violet-palette ground and the old border value fell under the
  3.0 non-text threshold against it. Tokens are named `--pri` / `--sec` /
  `--pop`.
- One field beyond the three-hue ramp: the nav rail is a committed solid dark
  teal (`--nav-ground` `#0A2B27`), not tinted — the one bold, page-scale color
  move in this redesign, deliberately bounded to the chrome so the task
  surface next to it stays quiet and scannable. The wordmark and the active
  nav item are the one lime moment in the chrome.
- Teal and moss may take whole surfaces; **lime may not** — one held moment
  per screen, plus the same two functional uses as before (the hot drop zone,
  and the tile that just landed).
- **A missing skill never uses the alert red.** Missing is a task, not an
  error, and that tone rule runs through the copy — unchanged by either
  recolour.
- Type: Archivo (variable, `wdth 118`) display, Onest body, IBM Plex Mono for
  anything the parser counts. Google Fonts only — **unchanged**. Operate
  surfaces are well served by workhorse type; recolouring did not require a
  new voice.

**Lift exact token values from `web/src/styles/tokens.css` rather than
re-deriving colours** — the canvas artboards under "Evidence on Hand" below
describe the original superseded direction, and neither does the intermediate
violet round documented in `tokens.css`'s own history comment.

## Evidence on Hand

- **Design canvas** (the *original* "Marked Up" deliverable, 8 screens —
  **superseded 2026-09-04**; kept as history, not as current evidence):
  `https://claude.ai/code/artifact/4592a539-9306-42c3-b6ce-9f3536eca60d`
  Draft doc (the rules in prose):
  `https://claude.ai/code/artifact/717252de-9f29-4e5a-8d09-9ae691a31bb4`
  Sources: `%TEMP%\jobkeep-canvas-v2\` — 14 `.dc.html` artboards + `canvas.json`.
  No replacement canvas was generated for the new palette — see Brand
  Commitments; `web/src/styles/tokens.css` is the current source of truth.
- **Seeded demo data in the dev database**, which is real working evidence and
  the right thing to build against: application `a1f74664` (REA Group, Senior
  Backend Engineer, Melbourne); résumé `c4d9af56` ("demo-cv", 5 skills); résumé
  `b91896e3` ("tyler-cv-2025", the real CV, 8 skills). The ATS check on these
  returns 4 matched and `.NET` missing, and that gap has been verified to clear
  when the skill is dragged on and to return when removed.
- `docs/token-log.md` — a measured build-cost ledger, generated from transcripts.
- `docs/diagrams/schema-erd.svg` and `architecture.svg`, derived from EF migration
  scripts rather than from reading model classes.

**Absences future work must not fabricate:** there are no users, no testimonials,
no customers, no pricing, no benchmarks, no uptime record, and **no public
deployment or demo URL**. The Phase 6 doc explicitly warns against a README that
promises a link the deployment story has not earned.

**A real privacy constraint:** résumé `b91896e3` contains the author's actual
personal data. Never paste résumé detail responses into anything published, and
never put the real email or address in a mockup — published mockups use
`tyler.ha@example.com` deliberately.

## Product Principles

1. **The tool wins the tiebreak.** When daily usefulness and portfolio impact
   disagree, build the one that gets used. The portfolio value is a consequence
   of having built something real, not a substitute for it.
2. **A decision that cannot be explained out loud is worth less than a simpler
   one that can.** Prefer the defensible choice over the impressive-sounding one,
   and write the trade-off down beside the code.
3. **Use a model only where a query cannot answer.** It is exact, instant, free,
   and it degrades instead of failing.
4. **Every stage ends in something runnable.** Split work that is getting large
   rather than letting a phase sprawl.
5. **Near-zero cost is a design constraint, not a preference.** It has already
   changed the database, the deployment shape, and the AI provider.

## Accessibility & Inclusion

**Required bar: WCAG 2.2 AA**, confirmed 2026-08-29. Chosen as a defensible
interview claim and a real constraint, not decoration.

Contrast ratios were re-measured against the current palette on 2026-09-04 —
twice that day, once for the violet round and again for the teal/lime round
that replaced it (see Brand Commitments). The neutrals are carried from the
original "Marked Up" measurement except `--rule-strong`, re-measured and
darkened for this warmer ground; the alert red is unchanged. What follows are
the usage rules AA imposes on the palette as it now stands.

| Token | on white | on `#F7F2E6` | Ruling |
|---|---|---|---|
| `--pri` `#0B6E63` | 6.13 | — | Passes AA text everywhere. White on teal = 6.13, so a filled button with a white label is safe. |
| `--sec` `#4A7C2F` | 4.98 | — | Passes AA text at the base, unlike the two earlier palettes' secondary — this moss green can carry a filled button label directly. |
| `--pop` `#C7D92E` | 1.57 | 1.38 | **Never carries text, and never carries a boundary or state on its own** (below the 3.0 non-text threshold). But **ink on lime = 11.33**, its strongest pairing yet — lime works as a *ground* under dark ink. |
| `--ink` `#16181D` | 17.76 | 15.61 | Safe everywhere. |
| `--muted` `#5E6470` | 5.94 | 5.22 | Passes AA text. Safe for secondary copy. |
| `--rule` `#E4E0D8` | 1.32 | 1.16 | Decorative hairline only. **Cannot be the sole boundary of an input or control** — those need something ≥3.0. |
| `--rule-strong` `#8A8171` | 3.85 | 3.38 | The border a control may rely on. Darkened from the violet round's `#958C7B` (2.92 on this ground — a fail) because this cream ground is warmer/darker than that round's bright ivory. |
| `--alert` `#D93A3A` | 4.55 | 4.00 | Passes on white, **fails on the warm ground**. White on red = 4.55, so a filled alert is fine; red text on the page ground is not. |
| `--nav-ground` `#0A2B27` | — (background only) | | The one committed field: a solid dark-teal nav rail. White text on it is 15.14; see the ramp note below for the label colors used on it. |

### The palette is really a nine-token ramp, plus one committed field

Same structure as both earlier rounds: each brand hue is a **base**, a
**dark** (for text) and a **tint** (for surfaces) — the underlying problem
didn't change, only the hues did.

| | base | dark (text) | tint (surface) |
|---|---|---|---|
| Teal | `--pri` `#0B6E63` | `--pri-dark` `#084B44` | `--pri-tint` `#DCF3EF` |
| Moss | `--sec` `#4A7C2F` | `--sec-dark` `#2F5A1E` | `--sec-tint` `#E4F0D9` |
| Lime | `--pop` `#C7D92E` | `--pop-dark` `#6B7318` | `--pop-tint` `#F3F6D8` |

**The rule that makes AA automatic: on a tinted surface, the label is always the
`-dark`, never the base.** Every dark-on-tint pair passes AA text comfortably —
teal 8.62, moss 6.83, lime 4.64. The base hues do not (moss's base is the one
exception that also passes on white, but --sec-dark is still the safer,
correct choice on a tint).

This also answers the lime problem above: `--pop` cannot carry text at 1.57, but
**`--pop-dark` `#6B7318` reaches 5.13 on white.** Lime text is available; it is
just never the base token.

One field sits outside the ramp: `--nav-ground` `#0A2B27` (the nav rail) with
`--nav-muted` `#A9C2B0` (7.95 on it) for inactive labels, white for hover, and
`--pop` itself for the wordmark and the active item's background (9.66 on
`--nav-ground`, paired with `--ink` at 11.33). This is a background this
specific component owns, not a fourth ramp hue other components should reach
for.

Two consequences that are design work, not compliance paperwork:

- **The hot drop zone cannot be signalled by lime alone** — it fails the 3.0
  non-text threshold. It needs a second, non-colour cue (shape, outline, motion,
  or ink). The same applies to the just-landed tile.
- **The dnd-kit drag needs a full keyboard path.** dnd-kit supports this, but it
  is a build obligation, not a freebie. The ATS-check skill drag and the Pipeline
  status drag are both affected, and the Pipeline one must also announce the 400
  rejection accessibly.

No other product-specific accessibility requirement has been established (there
is one user, and no assistive-technology need has been stated).
