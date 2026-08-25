# Token log

*Generated 2026-08-25 from session transcripts. The final session below was
still running when this was written, so its row understates it.*

**What this is:** how many tokens each phase of JobKeep actually cost to build
with Claude Code. Generated from the session transcripts, not estimated after
the fact — see "How this is produced" below.

**What this is *not*:** a bill. These are conversation tokens on a Claude Code
subscription, not metered API spend, so there is no dollar figure attached and
none should be inferred. The project's near-zero-cost priority is about *AWS and
AI-provider* spend (priority 1 in `CLAUDE.md`); this file is a different
measurement, kept because build effort is worth knowing and because the shape of
the numbers turned out to be interesting.

---

## Read this before reading the numbers

**Cache reads dominate, and they are not the same as fresh input.** Roughly 95%
of every total below is `cache_read` — the conversation context being replayed
into each turn. It is the cheapest class of token by a wide margin. The column
that reflects genuinely new work is **fresh in** (new input + cache writes)
plus **output**.

So read the table this way:

- **Total** — the honest gross number, and the one that grows with conversation
  length whether or not anything is being built.
- **Fresh in + Output** — closer to "how much thinking and writing happened".
  Across the whole project that is ~7.5M against a 145M gross.

A long session with few edits can outweigh a short session that shipped a
feature. That is a property of how agents work, not a measure of productivity.

---

## By phase

Sessions are grouped by what they were for. Some phases took several sessions;
some sessions covered work that isn't a numbered phase (tooling, the
architecture record, the audit), and those are listed separately rather than
folded into a phase they didn't belong to.

| Work | Sessions | Turns | Total | Notes |
|---|---:|---:|---:|---|
| **Phase 0** — shaping the project | 1 | 19 | 622k | The initial "what is this project" conversation. |
| **Phase 1** — local API, in-memory | 2 | 177 | 8.7M | Scaffolding ASP.NET Core + the first endpoints. |
| **Phase 2** — Postgres + GraphQL | 2 | 338 | 45.1M | **The most expensive thing in the project by far.** One 325-turn session accounts for 44.6M of it. |
| **Phase 2.1** — write surface as slices | 1 | 210 | 29.7M | Four slices, both API surfaces, live verification, doc updates. Grew after it was first logged — the session continued past the phase. |
| **Phase 2.2** — tests + CI | 1 | 185 | 30.2M | 55 integration tests, GitHub Actions, `user-journeys.md`, and the doc updates. Second-most expensive phase after Phase 2. Logged from inside the session it measures, so the real figure is a little higher than this. |
| Architecture record | 1 | 162 | 19.8M | Writing `architecture.md` — the decision record and gap register. |
| Schema + architecture diagrams | 1 | 162 | 19.9M | The `schema-diagram` skill and the two committed SVGs. |
| Security & data audit | 1 | 110 | 13.4M | `security-and-data-audit.md`, F1–F18. |
| Repo hygiene + architecture direction | 6 | 224 | 12.3M | `CLAUDE.md`, endpoint extraction, the rename to JobKeep, git config. |
| Tooling / skills | 5 | 21 | 700k | Skill installs and short setup sessions. |
| | | | | |
| **Total** | **21** | **1608** | **180.5M** | |

### What the numbers say

Three things worth noticing, because they cut against intuition:

1. **Phase 2 is the single most expensive item, and almost all of it is one
   session.** 45.1M total, of which 44.6M came from one unbroken 325-turn
   session — the schema, the migrations, both API surfaces and the
   DynamoDB-to-Postgres reversal, all in one sitting. That is the "don't let a
   phase sprawl" shape priority 2 in `CLAUDE.md` warns about, now with a number
   on it.

   Phase 2.1 by contrast cost 24.9M. That is *not* a like-for-like comparison —
   Phase 2 delivered far more — but the per-turn figures in point 3 show where
   the difference actually comes from, and it isn't scope.

2. **Documentation cost as much as code.** The architecture record, the diagrams
   and the security audit together are 53.1M — more than Phase 1 and Phase 2.1
   combined (33.6M), and more than any single numbered phase except Phase 2. For
   a project whose stated second audience is "the evidence", that is a defensible
   allocation rather than an overrun, but it should be a deliberate choice each
   time, not a habit.

3. **Cost is superlinear in session length — this is the real lever.** Every turn
   replays the conversation so far, so a session's *later* turns each cost far
   more than its early ones. Cost per turn, by how long the session ran:

   | Session length | Cost per turn |
   |---|---|
   | under ~40 turns | 30–40k |
   | ~90–130 turns | 55–65k |
   | 160+ turns | 120–140k |

   A 325-turn session cost 137k per turn — roughly **four times** what the same
   turn costs in a short session. Total cost therefore grows closer to the square
   of session length than in proportion to it: 19 turns → 0.6M, 130 turns → 6.9M,
   325 turns → 44.6M.

   The lever is *where a session ends*, not how hard the task is. That is an
   unplanned second argument for priority 2 in `CLAUDE.md` — "each phase should
   end in something runnable" was written to stop scope sprawl, and it turns out
   to be the cost control too.

---

## By session

The raw ledger. `Fresh in` = new input + cache writes; `Cache read` = context
replay; `Total` = all four counters summed.

| Started | Branch | Turns | Fresh in | Cache read | Output | Total | Session |
|---|---|---:|---:|---:|---:|---:|---|
| 2026-08-19 04:08 | `HEAD` | 19 | 106k | 505k | 12k | **622k** | `95b3ba7b` |
| 2026-08-19 04:16 | `HEAD` | 47 | 105k | 1.7M | 32k | **1.8M** | `4a6e9da8` |
| 2026-08-19 04:38 | `HEAD` | 130 | 435k | 6.4M | 127k | **6.9M** | `b7e3d7a3` |
| 2026-08-19 10:43 | `master` | 325 | 1.2M | 42.8M | 558k | **44.6M** | `e1e85646` |
| 2026-08-21 11:13 | `phase-2-postgres-graphql` | 13 | 56k | 419k | 11k | **485k** | `f4125104` |
| 2026-08-21 11:23 | `phase-2-postgres-graphql` | 12 | 36k | 322k | 7k | **365k** | `31ec45ad` |
| 2026-08-21 11:27 | `phase-2-postgres-graphql` | 88 | 232k | 5.2M | 167k | **5.6M** | `03241e5a` |
| 2026-08-21 12:47 | `phase-2-subphase-plans` | 39 | 73k | 1.4M | 25k | **1.5M** | `cb22027c` |
| 2026-08-24 03:46 | `phase-2-subphase-plans` | 8 | 22k | 221k | 4k | **247k** | `25f22a04` |
| 2026-08-24 06:55 | `phase-2-subphase-plans` | 6 | 65k | 129k | 2k | **197k** | `49a02f5c` |
| 2026-08-24 06:58 | `phase-2-subphase-plans` | 1 | 0 | 0 | 0 | **0** | `1deed7eb` |
| 2026-08-24 07:01 | `phase-2-subphase-plans` | 162 | 774k | 18.8M | 229k | **19.8M** | `64a1d9ae` |
| 2026-08-25 01:06 | `master` | 4 | 87k | 86k | 3k | **176k** | `cca26e3d` |
| 2026-08-25 01:14 | `master` | 2 | 78k | 0 | 2k | **80k** | `b2244af0` |
| 2026-08-25 01:27 | `master` | 34 | 184k | 1.7M | 26k | **1.9M** | `88a11136` |
| 2026-08-25 03:00 | `develop` | 162 | 727k | 18.9M | 224k | **19.9M** | `01934837` |
| 2026-08-25 07:07 | `develop` | 39 | 230k | 1.9M | 34k | **2.2M** | `2d1cc0ae` |
| 2026-08-25 08:46 | `develop` | 110 | 628k | 12.7M | 147k | **13.4M** | `5ebe70c1` |
| 2026-08-25 09:17 | `develop` | 210 | 617k | 28.9M | 238k | **29.7M** | `18c78f32` |
| 2026-08-25 11:24 | `phase-2.1/write-surface` | 12 | 88k | 603k | 6k | **697k** | `ba9412ac` |
| 2026-08-25 11:40 | `phase-2.2/tests-and-ci` | 185 | 1.3M | 28.6M | 278k | **30.2M** | `6b5afb84` |
| | | **1608** | **7.1M** | **171.2M** | **2.1M** | **180.5M** | 21 sessions |

---

## By sub-task

Session totals hide what happened inside a session. Each thing typed at the
prompt starts a task, and the script can break a session down that way:

```bash
python scripts/token-usage.py --task 18c78f32
```

Phase 2.1's session, for example:

| Started | Turns | Fresh in | Cache read | Output | Total | Task |
|---|---:|---:|---:|---:|---:|---|
| 09:18 | 145 | 505k | 16.5M | 160k | **17.1M** | Implement Phase 2.1 (rundown, four slices, verification, docs) |
| 09:48 | 42 | 78k | 7.7M | 59k | **7.8M** | Restructure `docs/`, add this token log |

This is the finest granularity the transcripts support. There is no marker in
them for "sub-task" beyond a human typing something new, so a single prompt that
produced a whole phase shows up as one row — as the first line above does.

---

## How this is produced

Claude Code writes one JSONL transcript per session under
`~/.claude/projects/<slugified-cwd>/`, and every assistant record carries a
`usage` block (`input_tokens`, `cache_creation_input_tokens`,
`cache_read_input_tokens`, `output_tokens`). `scripts/token-usage.py` reads
those and totals them.

```bash
python scripts/token-usage.py                  # per-session table
python scripts/token-usage.py --task <prefix>   # one session, split by task
python scripts/token-usage.py --json            # raw rows
```

**One catch worth knowing:** Claude Code slugs the *working directory*, so this
project's history is split across five slugs — it has been opened from both the
repo root and `src/`, and the project was renamed twice
(`JobAppilcationTracker` → `JobTracker` → `JobKeep`). The script globs all five;
if the project moves again, add the new pattern to `PROJECT_SLUG_PATTERNS`.

Transcripts are local and are not retained forever. If a long-term record
matters, update this file at the end of each phase rather than relying on
regenerating it later.

## Keeping it current

At the end of a phase, per `CLAUDE.md`: run the script, add a row to **By
phase**, and refresh **By session**. The numbers for a session that is still
running will keep moving — this file records a phase once it is done.
