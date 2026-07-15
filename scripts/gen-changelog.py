#!/usr/bin/env python3
"""One-off generator for CHANGELOG.md from git tag history.

Not a maintained release-automation script - just used to bootstrap the
initial CHANGELOG.md from existing tags/commits. Kept in scripts/ for
reference in case the changelog needs to be regenerated/rebuilt later.
"""
import subprocess
import re
from collections import OrderedDict

def run(args):
    return subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace").stdout

# tag, date (creatordate short)
raw = run(["git", "for-each-ref", "--sort=creatordate",
           "--format=%(refname:short)|%(creatordate:short)", "refs/tags"])
tags = [line.split("|") for line in raw.strip().splitlines() if line.strip()]

# Build ranges: (prev_tag_or_None, tag, date)
ranges = []
prev = None
for tag, date in tags:
    ranges.append((prev, tag, date))
    prev = tag

NOISE_PREFIXES = (
    "chore(squad)",
    "Merge branch",
    "Merge pull request",
)

def is_noise(subject: str) -> bool:
    s = subject.strip()
    for p in NOISE_PREFIXES:
        if s.startswith(p):
            return True
    return False

CATS = [
    ("Fixed", re.compile(r"^fix(\(|:| )", re.I)),
    ("Added", re.compile(r"^feat(\(|:| )", re.I)),
    ("Changed", re.compile(r"^(refactor|chore|perf|build|ci)(\(|:| )", re.I)),
    ("Docs", re.compile(r"^docs(\(|:| )", re.I)),
    ("Tests", re.compile(r"^test(\(|:| )", re.I)),
]

def categorize(subject: str) -> str:
    for name, pat in CATS:
        if pat.match(subject):
            return name
    return "Other"

out_lines = []
out_lines.append("# Changelog\n")
out_lines.append("All notable changes to Agentweaver are documented in this file, generated from the repository's git tag/commit history (`v0.7.0` through `v0.9.60`).\n")
out_lines.append(
    "Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/). Entries are grouped by "
    "release tag (newest first) and bucketed by commit-message prefix (`fix`, `feat`, `refactor`/`chore`, `docs`, "
    "`test`); merge commits and routine `chore(squad)` state-sync commits are omitted for readability. Regenerate "
    "with `python scripts/gen-changelog.py` if the history needs to be rebuilt.\n"
)

# newest first
for prev, tag, date in reversed(ranges):
    if prev is None:
        commit_range = tag
        log = run(["git", "log", "--no-merges", "--pretty=format:%s", commit_range])
    else:
        commit_range = f"{prev}..{tag}"
        log = run(["git", "log", "--no-merges", "--pretty=format:%s", commit_range])

    subjects = [s for s in log.splitlines() if s.strip()]
    subjects = [s for s in subjects if not is_noise(s)]
    # de-dup while preserving order
    seen = set()
    deduped = []
    for s in subjects:
        if s not in seen:
            seen.add(s)
            deduped.append(s)

    out_lines.append(f"\n## [{tag}] - {date}\n")
    if not deduped:
        out_lines.append("_No user-facing changes (internal/chore only)._\n")
        continue

    buckets = OrderedDict((name, []) for name, _ in CATS)
    buckets["Other"] = []
    for s in deduped:
        buckets[categorize(s)].append(s)

    for name in list(buckets.keys()):
        items = buckets[name]
        if not items:
            continue
        out_lines.append(f"### {name}")
        for it in items:
            out_lines.append(f"- {it}")
        out_lines.append("")

with open("CHANGELOG.md", "w", encoding="utf-8") as f:
    f.write("\n".join(out_lines))

print(f"Wrote CHANGELOG.md with {len(ranges)} releases")
