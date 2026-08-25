#!/usr/bin/env python3
"""Aggregate Claude Code token usage for this project, per session.

Claude Code writes one JSONL transcript per session under
~/.claude/projects/<slug>/, and every assistant record carries a `usage` block.
This reads those transcripts and totals them, so docs/token-log.md is derived
from what actually happened rather than estimated after the fact.

The project has been renamed several times (JobAppilcationTracker -> JobTracker
-> JobKeep), and Claude Code slugs the *working directory*, so one project's
history is spread across several slugs — hence PROJECT_SLUG_PATTERNS below.

Usage:
    python scripts/token-usage.py               # per-session markdown table
    python scripts/token-usage.py --json        # raw rows, for further processing
    python scripts/token-usage.py --task 18c78f32   # sub-task breakdown of one session
"""

import json
import os
import sys
import glob
import re
from collections import defaultdict

# Every working directory this project has lived in, slugified the way Claude
# Code does it (drive/path separators -> dashes).
PROJECT_SLUG_PATTERNS = [
    "C--Users-minhh-Documents-Hobby-Claude-JobAppilcationTracker*",  # sic: original typo
    "C--Users-minhh-Documents-Hobby-Claude-JobTracker*",
    "C--Users-minhh-Documents-Hobby-Claude-JobKeep*",
]

PROJECTS_DIR = os.path.join(os.path.expanduser("~"), ".claude", "projects")


def summarize_session(path):
    """Return a usage summary for one transcript, or None if it has no usage."""
    totals = defaultdict(int)
    models, branches = set(), []
    first_prompt, first_ts, last_ts = None, None, None
    sidechain_out = 0

    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue

            ts = rec.get("timestamp")
            if ts:
                first_ts = ts if first_ts is None or ts < first_ts else first_ts
                last_ts = ts if last_ts is None or ts > last_ts else last_ts

            if rec.get("gitBranch"):
                branches.append(rec["gitBranch"])

            # The first thing the human actually typed — the best label for
            # what the session was for.
            if (first_prompt is None and rec.get("type") == "user"
                    and rec.get("promptSource") == "typed"):
                content = rec.get("message", {}).get("content")
                if isinstance(content, str):
                    first_prompt = content.strip()

            if rec.get("type") != "assistant":
                continue
            usage = rec.get("message", {}).get("usage") or {}
            if not usage:
                continue

            model = rec.get("message", {}).get("model")
            if model:
                models.add(model)

            out = usage.get("output_tokens", 0) or 0
            totals["input"] += usage.get("input_tokens", 0) or 0
            totals["cache_write"] += usage.get("cache_creation_input_tokens", 0) or 0
            totals["cache_read"] += usage.get("cache_read_input_tokens", 0) or 0
            totals["output"] += out
            totals["turns"] += 1
            # Subagent work is billed the same but is worth seeing separately.
            if rec.get("isSidechain"):
                sidechain_out += out

    if not totals["turns"]:
        return None

    totals["total"] = (totals["input"] + totals["cache_write"]
                       + totals["cache_read"] + totals["output"])
    # Fresh input actually sent up, ignoring cache replay of the same context.
    totals["fresh_in"] = totals["input"] + totals["cache_write"]

    branch = max(set(branches), key=branches.count) if branches else ""
    return {
        "session": os.path.splitext(os.path.basename(path))[0],
        "slug": os.path.basename(os.path.dirname(path)),
        "start": (first_ts or "")[:16].replace("T", " "),
        "end": (last_ts or "")[:16].replace("T", " "),
        "branch": branch,
        "models": ", ".join(sorted(m.split("-2")[0] for m in models)) or "?",
        "prompt": re.sub(r"\s+", " ", (first_prompt or ""))[:70],
        "sidechain_output": sidechain_out,
        **totals,
    }


def by_task(path):
    """Break one session down by typed user prompt.

    Each thing the human types starts a task; every assistant turn after it
    accrues to that task until the next one. This is the finest granularity the
    transcripts support, and it is what makes a per-sub-task number possible
    rather than just a per-session one.
    """
    tasks, cur = [], None
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue

            if rec.get("type") == "user" and rec.get("promptSource") == "typed":
                content = rec.get("message", {}).get("content")
                if isinstance(content, str):
                    cur = {"prompt": re.sub(r"\s+", " ", content.strip())[:90],
                           "at": (rec.get("timestamp") or "")[11:16],
                           "turns": 0, "fresh_in": 0, "cache_read": 0,
                           "output": 0, "total": 0}
                    tasks.append(cur)
                    continue

            if rec.get("type") != "assistant" or cur is None:
                continue
            u = rec.get("message", {}).get("usage") or {}
            if not u:
                continue
            fin = (u.get("input_tokens", 0) or 0) + (u.get("cache_creation_input_tokens", 0) or 0)
            cr = u.get("cache_read_input_tokens", 0) or 0
            out = u.get("output_tokens", 0) or 0
            cur["turns"] += 1
            cur["fresh_in"] += fin
            cur["cache_read"] += cr
            cur["output"] += out
            cur["total"] += fin + cr + out
    return tasks


def find_session(prefix):
    for pattern in PROJECT_SLUG_PATTERNS:
        for d in glob.glob(os.path.join(PROJECTS_DIR, pattern)):
            for f in glob.glob(os.path.join(d, prefix + "*.jsonl")):
                return f
    return None


def collect():
    rows = []
    for pattern in PROJECT_SLUG_PATTERNS:
        for d in glob.glob(os.path.join(PROJECTS_DIR, pattern)):
            for f in glob.glob(os.path.join(d, "*.jsonl")):
                row = summarize_session(f)
                if row:
                    rows.append(row)
    return sorted(rows, key=lambda r: r["start"])


def human(n):
    if n >= 1_000_000:
        return "%.1fM" % (n / 1_000_000)
    if n >= 1_000:
        return "%.0fk" % (n / 1_000)
    return str(n)


def main():
    # --task <session-prefix>: sub-task breakdown within one session.
    if "--task" in sys.argv:
        prefix = sys.argv[sys.argv.index("--task") + 1]
        path = find_session(prefix)
        if not path:
            sys.exit("no transcript starting with %r" % prefix)
        print("| Started | Turns | Fresh in | Cache read | Output | Total | Prompt |")
        print("|---|---:|---:|---:|---:|---:|---|")
        for t in by_task(path):
            if not t["turns"]:
                continue
            print("| %s | %d | %s | %s | %s | **%s** | %s |" % (
                t["at"], t["turns"], human(t["fresh_in"]), human(t["cache_read"]),
                human(t["output"]), human(t["total"]), t["prompt"]))
        return

    rows = collect()
    if "--json" in sys.argv:
        print(json.dumps(rows, indent=2))
        return

    print("| Started | Branch | Turns | Fresh in | Cache read | Output | Total | Session |")
    print("|---|---|---:|---:|---:|---:|---:|---|")
    for r in rows:
        print("| %s | `%s` | %d | %s | %s | %s | **%s** | `%s` |" % (
            r["start"], r["branch"] or "-", r["turns"], human(r["fresh_in"]),
            human(r["cache_read"]), human(r["output"]), human(r["total"]),
            r["session"][:8]))

    t = lambda k: sum(r[k] for r in rows)
    print("| | | **%d** | **%s** | **%s** | **%s** | **%s** | %d sessions |" % (
        t("turns"), human(t("fresh_in")), human(t("cache_read")),
        human(t("output")), human(t("total")), len(rows)))


if __name__ == "__main__":
    main()
