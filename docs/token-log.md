# Token log

*Regenerated 2026-08-26 from session transcripts, at the end of Phase 2.6. The
final session below was still running when this was written, so its row
understates it — as every version of this file has. The running tally of that
correction is now **four for four**: Phase 2.2 was logged at 185 turns / 30.2M and
finished at 297 / 62.2M; Phase 2.3 at 198 / 33.1M, finished 260 / 52.4M; Phase 2.4
at 78 / 7.7M, finished 163 / 23.0M; **Phase 2.5 was logged last time at 86 / 7.7M
and finished at 148 / 18.1M**. All four are corrected below. Assume the Phase 2.6
row is low by a similar margin.*

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
  Across the whole project that is ~12.5M against a 302M gross — the ratio
  has held near 4% while the gross has doubled.

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
| **Phase 2.2** — tests + CI | 1 | 297 | 62.2M | 55 integration tests, GitHub Actions, `user-journeys.md`, and the doc updates. **The most expensive item in the project**, overtaking Phase 2. Logged mid-session at 185 turns / 30.2M and finished at 297 — the second half cost more than the first, which is point 3 below happening in one session. |
| **Phase 2.3** — query surface, and retiring the repository | 1 | 260 | 52.4M | Five slices, the repository and the Endpoints folder deleted, 31 new tests, A2/A3/A4/A7 closed, plus the doc and diagram updates. **Corrected:** logged last time at 198 turns / 33.1M from inside the running session, and it went on to 260 / 52.4M. Now the project's second most expensive item. |
| **Phase 2.4** — analytics | 1 | 163 | 23.0M | Three read-only slices, both surfaces, 15 tests, and the module-boundary decision (13). **Corrected:** logged last time at 78 turns / 7.7M from inside the running session, and it went on to 163 / 23.0M — the session ran on to roughly double the turns and triple the tokens after the row was written. Still the cheapest feature phase since 2.1, but not by the margin the last version claimed. |
| **Phase 2.5** — status lifecycle | 2 | 157 | 18.6M | One pure domain rule, the update slice, 31 new tests, plus the market check that set the transition table. **Corrected:** logged last time at 86 turns / 7.7M from inside the running session; it went on to 148 / 18.1M, and the phase's real total is 18.6M — a 2.7x correction (Phase 2.4's 3.0x is still the largest ratio). The session's tail was the two PRs and the handoff, not the phase work. Two sessions for one conversation because the work ran as a background job, which writes its own transcript alongside the foreground one; the 9-turn row is that foreground stub, not separate work. |
| **Phase 2.6** — .NET 10 upgrade | 1 | 114 | 10.1M | Four project/config files, no C# changed, plus a critical-CVE patch the restore surfaced and the doc corrections. **Provisional — logged from inside the session it measures, so it is low.** See the header; assume the same shape as the four corrections above. |
| **Phase 4** — AI job-description analyzer | 2 | 197 | 25.7M | `IChatClient` behind Ollama, two slices, `IPostingContract` (decision 14), 10 tests. **Now final**, and its story is the reason to distrust a phase's own status field: the code landed but the tests never ran (Docker was down), so the phase sat at *In progress* until Phase 4.5 ran them — 10/10 passed unchanged. |
| **Phase 4.5** — document import | 1 | 261+ | 52.0M+ | PDF/DOCX/text extraction, a model structuring step, a human confirm-and-fix cycle, 7 slices, the first migration since InitialCreate, 29 new tests, both diagrams redrawn. **Provisional and understated — logged from inside the session it measures.** Five phases running have been logged mid-session and corrected upward by 2–3x; assume the same here. Already the third most expensive item at the time of writing, and it ran to 261 turns, far past the 120k / ~55-turn rule. That is the finding, not an aside: the phase was scoped as one unit of runnable work and was not split, so the rule lost to the task. |
| Architecture record | 1 | 162 | 19.8M | Writing `architecture.md` — the decision record and gap register. |
| Schema + architecture diagrams | 1 | 162 | 19.9M | The `schema-diagram` skill and the two committed SVGs. |
| Security & data audit | 1 | 110 | 13.4M | `security-and-data-audit.md`, F1–F18. |
| Repo hygiene + architecture direction | 7 | 247 | 14.0M | `CLAUDE.md`, endpoint extraction, the rename to JobKeep, git config, and the change-triggered documentation policy (decision 12). |
| Docs audit + markdown skill | 2 | 74 | 5.6M | The phase-doc flow audit after the 2.2 renumber, and a markdown-audit skill built in a worktree. |
| Tooling / skills | 5 | 21 | 700k | Skill installs and short setup sessions. |
| | | | | |
| **Total** | **33** | **3148** | **427.7M** | Regenerated 2026-08-27 during Phase 4.5. The jump from 324.0M is Phase 4 and Phase 4.5 landing, not a restatement of earlier rows. |

### What the numbers say

Three things worth noticing, because they cut against intuition:

1. **The two most expensive items are both single long sessions, and neither is
   the biggest phase.** Phase 2.2 — *tests and CI*, which shipped no features —
   is the most expensive thing in the project at 62.2M over 297 turns. Phase 2.3
   is second at 52.4M over 260, and Phase 2 third at 45.1M, of which 44.6M came
   from one unbroken 325-turn session that built the schema, the migrations, both
   API surfaces and the DynamoDB-to-Postgres reversal in a single sitting.

   **This corrects what the 2026-08-25 version of this file said.** It claimed
   Phase 2 was the most expensive item, and it was wrong for a mundane reason:
   the Phase 2.2 row was written from inside the session it was measuring, at
   185 turns and 30.2M. That session ran another 112 turns and *doubled*. The
   number was not an estimate — it was a real measurement of an unfinished
   thing, which is a different way to be wrong and an easier one to miss.

   Compare scope honestly: Phase 2 delivered far more than 2.2 did. The per-turn
   figures in point 3 are where the difference actually comes from, and it isn't
   scope.

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
   | 160–210 turns | 120–170k |
   | 300+ turns | 140–210k |

   A 325-turn session cost 137k per turn and a 297-turn one cost 209k — roughly
   **five times** what the same turn costs in a short session. Total cost
   therefore grows closer to the square of session length than in proportion to
   it: 19 turns → 0.6M, 130 turns → 6.9M, 297 turns → 62.2M.

   The Phase 2.2 session is the cleanest demonstration, because it was measured
   twice. Its first 185 turns cost 30.2M (163k/turn); its next 112 cost 32.0M
   (286k/turn). Same session, same task, and the back half was **75% more
   expensive per turn** than the front half.

   Phase 2.3 then did it again, harder, because logging from inside a running
   session is apparently a rake this project keeps stepping on. Its first 198
   turns cost 33.1M (167k/turn — near-identical to 2.2's front half); its next
   **62 turns cost 19.3M**, or 311k/turn. The tail was **86% more expensive per
   turn**, and those 62 turns alone cost more than the entire architecture record.

   And then Phase 2.4 did it a **third** time, which is what turns a pattern into
   a finding. The last version of this file called 2.4 "the control" and reported
   it finished at 78 turns for 7.7M — a seventh of Phase 2.3, for comparable
   scope. It had not finished. It ran on to **163 turns / 23.0M**: its first 78
   turns cost 7.7M (99k/turn), its next **85 turns cost 15.3M**, or 180k/turn.
   The tail was **82% more expensive per turn** — sitting neatly between 2.2's
   75% and 2.3's 86%.

   So the honest summary is that the *same* mistake has now been made in three
   consecutive phases, and each time the correction landed in this paragraph
   rather than in the behaviour. The rake is not the measurement; it is that a
   session which has produced a runnable phase does not feel finished, so it
   keeps going, and the expensive turns are all after that point.

   The per-turn floor is also drifting. 99k/turn for 2.4's first 78 turns is well
   *above* the 55–65k the bracket table predicts for a session that length. The
   brackets were fitted on Phases 1–2.2, and what has grown since is the
   **standing** context every turn replays — a longer `CLAUDE.md`, more docs,
   more source. Read the brackets as a shape, not a forecast: ending a session
   early still buys a large saving, but it buys it against a rising baseline.

   Phase 2.5 was logged as the first phase where the session had *not* already
   run long — 83 turns for 6.8M, ~82k/turn — and that paragraph closed by asking
   whether the inevitable correction would be *smaller* than 2.2's, 2.3's and
   2.4's. It was not. It was the largest yet, by ratio: 86 turns / 7.7M became
   **148 turns / 18.1M**. Its first 86 turns cost 89k/turn; its next **62 turns
   cost 10.4M**, or 168k/turn — the tail **87% more expensive per turn**, which
   is the worst of the four only just: 2.3's 86% is inside the rounding.

   What the tail *was*, though, is the useful part. The phase work was finished
   at that 86-turn mark. The remaining 62 turns were housekeeping: committing a
   previous session's uncommitted Phase 2.4, opening two stacked PRs, and writing
   the handoff. So the extra 10.4M did not buy any of the phase's code or tests —
   it bought git operations, at 168k a turn, because they happened at the end of
   a long session instead of the start of a short one. That is the sharpest
   version of this finding the project has: **the work that gets pushed to the
   expensive end of a session is usually the cheap, mechanical work**, which is
   exactly the work a fresh session would have done for a fifth of the price.

   | Phase | Front | Back | Back half costs |
   |---|---|---|---|
   | 2.2 | 185 turns @ 163k/turn | 112 turns @ 286k/turn | **+75%** |
   | 2.3 | 198 turns @ 167k/turn | 62 turns @ 311k/turn | **+86%** |
   | 2.4 | 78 turns @ 99k/turn | 85 turns @ 180k/turn | **+82%** |
   | 2.5 | 86 turns @ 89k/turn | 62 turns @ 168k/turn | **+87%** |

   Four phases, four measurements, same answer within a 12-point spread. There is
   nothing left to establish about this pattern; the only open question is whether
   the practice changes.

   **Phase 2.6 is the first phase with a fresh-session handoff in front of it.**
   It began by reading a handoff doc rather than by continuing the Phase 2.5
   conversation, which is the `CLAUDE.md` rule being followed rather than
   described. Its row is provisional for the usual reason.

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
| 2026-08-25 11:40 | `phase-2.6/tests-and-ci` | 297 | 1.5M | 60.3M | 377k | **62.2M** | `6b5afb84` |
| 2026-08-25 13:23 | `claude/markdown-audit-skill-92286b` | 49 | 104k | 4.0M | 22k | **4.1M** | `a163ebef` |
| 2026-08-25 13:25 | `develop` | 25 | 139k | 1.3M | 20k | **1.5M** | `78adf777` |
| 2026-08-26 05:51 | `phase-2.3/list-queries` | 260 | 939k | 51.1M | 332k | **52.4M** | `e5f69267` |
| 2026-08-26 10:28 | `develop` | 23 | 174k | 1.5M | 19k | **1.7M** | `dfc3e109` |
| 2026-08-26 10:30 | `develop` | 163 | 533k | 22.2M | 204k | **23.0M** | `c0f17455` |
| 2026-08-26 11:10 | `develop` | 148 | 592k | 17.4M | 153k | **18.1M** | `8abb49a7` |
| 2026-08-26 11:10 | `develop` | 9 | 61k | 431k | 4k | **496k** | `a4832a88` |
| 2026-08-26 11:56 | `phase-2.6/dotnet10` | 211 | 360k | 26.0M | 176k | **26.5M** | `05a00171` |
| 2026-08-26 23:35 | `phase-2.6/dotnet10` | 82 | 1.0M | 8.5M | 133k | **9.7M** | `bf4d13ad` |
| 2026-08-27 05:38 | `phase-4/ai-analyzer` | 184 | 896k | 23.7M | 240k | **24.9M** | `797994f8` |
| 2026-08-27 09:30 | `worktree-phase-4.5-document-import` | 261 | 818k | 50.8M | 411k | **52.0M** | `1782fd4c` |
| 2026-08-27 09:31 | `phase-4/ai-analyzer` | 13 | 163k | 660k | 2k | **826k** | `d0c5f8ab` |
| | | **3148** | **13.1M** | **410.7M** | **3.9M** | **427.7M** | 33 sessions |

The `05a00171` row grew from 123 turns / 11.3M to 211 / 26.5M after it was
last written down — the sixth time a row logged from inside a live session has
had to be corrected upward. The `1782fd4c` row is Phase 4.5 and is being written
from inside that same live session, so it will be wrong in the same direction.

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

And Phase 2.2's, which is why `CLAUDE.md` now carries a documentation policy
(`architecture.md` decision 12):

| Turns | Total | Task |
|---:|---:|---|
| 141 | **19.8M** | write the tests — the feature work |
| 37 | **10.2M** | commit, and renumber the phase docs |
| 34 | **10.3M** | "audit again my docs phases and check if it follows standard flow" |
| 10 | **3.2M** | "check if i have skill to audit all the md file, and afterward push" |

13.5M of that is re-reading markdown that had not changed — as much as the entire
security audit cost to *write*. It is also the sharpest version of the
session-length point above: both sweeps ran after turn 240, at roughly 286k a
turn, so the same work in a fresh session would have cost about a third.

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
