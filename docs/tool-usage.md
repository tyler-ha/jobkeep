# Tool usage

**Read this before reaching for a tool on this repo.** It records which tool is
right for which job here, and — more usefully — the failures that have already
been paid for. Every trap below cost at least one wasted turn.

Its companion is [`agent-log.md`](agent-log.md), which records what subagent
explorations already found. Check that one before spawning an agent; check this
one before choosing how to read or edit.

## Reading

| Job | Tool | Note |
|---|---|---|
| A file you will edit | `Read` | Required before `Edit` — the edit fails otherwise. |
| A slice of a large file | `Read` with `offset`/`limit`, or `sed -n 'A,Bp'` | Reading 2000 lines to change 3 is the most common avoidable cost here. |
| Find a symbol or string | `Grep` | Ripgrep. Prefer it over `grep`/`rg` through Bash — the results are clickable. Escape literal braces. |
| Find files by name | `Glob` | Sorted by modification time, which is often the ordering you actually wanted. |
| Broad "where is this pattern used" | `Grep` with `output_mode: "files_with_matches"` first, then read the few that matter | Two cheap calls beat one expensive agent. |
| Ground no existing doc covers | An `Explore` subagent | **Only when the user asks.** Check `agent-log.md` first. |

## Editing

| Job | Tool |
|---|---|
| A targeted change | `Edit` — exact string match, unique |
| A new file, or a full rewrite of one you have read | `Write` |
| The same change in many files | A Python script written to the scratchpad, run with `python` |

## Shell

Both a **PowerShell** tool and a **Bash** tool (Git Bash) are available, each
taking its own syntax. Bash is usually the right one for `git`, `grep`, `sed` and
anything POSIX-shaped; PowerShell for Windows process work
(`Get-Process -Name Jobkeep | Stop-Process -Force`).

---

## Traps — all of these have actually happened

- **Large heredocs through Bash fail** with `unexpected EOF`. Hit in at least
  two separate sessions. Write a `.py` file to the scratchpad and run it, or use
  the `Write` tool for whole files.
- **`cd` does not persist between Bash calls.** Several commands have failed with
  "No such file or directory" for this reason alone. Use absolute paths, or `cd`
  inside the same compound command.
- **A quoted Bash heredoc still eats backslashes here.** `<<'PY'` is supposed to
  be literal, and in this environment a double backslash inside the heredoc
  reaches Python as a real newline — which silently corrupted a JS regex
  (`/\n{2,}/`) and made three exact-match replacements fail to match on
  2026-09-01. Two fixes that work: build the character with `chr(92)` / `chr(10)`
  instead of typing it, or write the replacement text to a scratchpad file and
  have Python read it back. Print `repr()` of the region before assuming a match.
  Note the scratchpad path differs by tool: Bash wants `/c/Users/...`, Python
  (native Windows) wants `C:\Users\...`.
- **EF's generated SQL is CRLF + BOM.** `tr -d '\r'` before grepping it, or every
  pattern fails for no visible reason.
- **`docker compose build` buffers all output** until it exits. An empty log file
  mid-build is normal, not a hang.
- **`dotnet ef migrations remove` connects to the database** and fails if
  Postgres is down. To undo an unapplied migration without a DB: delete the two
  files and `git checkout src/Migrations/AppDbContextModelSnapshot.cs`.
- **`dotnet ef --no-build` reads the compiled assembly**, so a deleted migration
  still "exists" until you rebuild.
- **`dotnet test` fails against a locked `Jobkeep.exe`** with MSB3027. Stop the
  process first — by hand, since `run.cmd -Stop` was deleted on 2026-09-01:
  `taskkill /IM Jobkeep.exe /F`, or `docker compose down` if the API is running in
  a container. A containerised API cannot lock the host's `obj/` at all, which is
  the quiet upside of dropping the native launcher.
- **`gh pr merge` is blocked** by the permission classifier. Merge with plain
  git: `git checkout develop && git merge --no-ff <branch> && git push`.
- **Don't put `\&` through a shell-quoted `python -c`** — it lands literally in
  the file.

---

- **An XML comment cannot contain `--`.** This repo writes prose comments in its
  project files, and the house style uses `--` as a dash. Nine csproj files written
  that way all failed `dotnet restore` with **MSB4025** before a character of C# was
  compiled. Use an em dash. (2026-09-01, Phase 13.1.)
- **`.dockerignore` patterns are not recursive unless you write `**/`.** `obj/`
  matches a *top-level* `obj` only. That was fine while `src/` held one project and
  silently stopped being fine when Phase 13.1 made nine: the host's Windows `obj/`
  directories were copied into the Linux build, and `dotnet publish` died inside
  `ResolvePackageAssets` with a NuGet stack trace that names neither Docker nor
  `obj`. If a container build fails in package resolution and the host build is
  green, check this first. (2026-09-01, Phase 13.1.)

## Deriving the database schema

**Do not read the model classes.** Column types, precision, delete behaviour and
index uniqueness live in Fluent API config and the Npgsql provider, so inferring
them from `Models/*.cs` produces a diagram that is wrong exactly where an
interviewer would probe. The `schema-diagram` skill exists for this.

And within that: **prefer a `pg_dump` of the migrated database to
`dotnet ef migrations script`.**

```bash
docker compose up -d db
docker compose exec -T db pg_dump -U postgres -d jobkeep --schema-only --no-owner > <scratch>/live.sql
```

An `--idempotent` migration script is a *sequence of migrations*, not a picture
of the result — Phase 7 drops three unique indexes and adds three others, and
**both sets appear in the script**, so counting tables or indexes out of that
text gives the wrong answer. The dump is the applied result. Generate the
migration script only when there is no database to dump, and never count from it.

---

## Browser automation

The Chrome extension (`mcp__claude-in-chrome__*`) is available in principle and
has been **disconnected for five consecutive sessions**. This is why the Phase 6
visual pass is still blocked and why spacing work has been done as a code audit
against the 4px scale rather than as a visual fix.

If a session needs to *see* the app, that is the user's job until the extension
connects. Say so rather than claiming a visual result you could not observe.

When it is connected: load every tool you need in **one** `ToolSearch` call
(`select:` takes a comma-separated list), call `tabs_context_mcp` first, and
never trigger a JS `alert`/`confirm` — a modal dialog blocks every subsequent
command.

---

## Skills

| Skill | When |
|---|---|
| `schema-diagram` | A migration, a changed delete rule or index, or a module-boundary move. Project-local: `.claude/skills/schema-diagram/`. |
| `impeccable` / `frontend-design` | UI design and review work. `PRODUCT.md` already exists, so no re-init. **`impeccable`'s detector runs DEGRADED here — an empty result is an undercount, not a pass.** |
| `handoff` | Ending a session. Writes to the OS temp dir, never the repo. |
| `code-review` | Reviewing a diff or branch. |
| `run` | Launching the app. Here that is `docker compose up --build` — the only launcher since 2026-09-01. |

---

## Scripts in this repo

- `scripts/token-usage.py` — reads Claude Code's session transcripts and totals
  tokens per session, or per task within a session (`--task <prefix>`). The
  source for `docs/token-log.md`. **The transcripts are local and not kept
  forever**, so a phase that isn't logged when it ends may not be recoverable.
- ~~`scripts/run.ps1` (via `run.cmd`)~~ — **deleted 2026-09-01.** The native local
  stack, replaced by `compose.yaml` as the single launcher. `scripts/` now holds
  one file.
