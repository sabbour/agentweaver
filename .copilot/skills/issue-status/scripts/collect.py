#!/usr/bin/env python3
"""
collect.py — gather issue pipeline status for the issue-status skill.

Usage:
    python collect.py [--repo owner/repo] [--filter open|closed|all|bugs|features|chores]
                      [--squad member] [--deployed-tag SHA]

Outputs JSON array to stdout.
"""
import argparse
import json
import re
import subprocess
import sys
from datetime import datetime, timezone

SQUAD_LABEL_PREFIX = "squad:"
TYPE_LABEL_PREFIX = "type:"

def run(cmd, check=True):
    r = subprocess.run(cmd, shell=True, capture_output=True, text=True)
    if check and r.returncode != 0:
        return None
    return r.stdout.strip()

def gh(query):
    out = run(f"gh {query}", check=False)
    return out

def git(cmd):
    return run(f"git {cmd}", check=False)

def get_issues(repo, state, type_filter, squad_filter):
    label_filters = ""
    if type_filter in ("bugs",):
        label_filters += ' --label "type:bug"'
    elif type_filter in ("features",):
        label_filters += ' --label "type:feature"'
    elif type_filter in ("chores",):
        label_filters += ' --label "type:chore"'
    if squad_filter:
        label_filters += f' --label "squad:{squad_filter}"'

    gh_state = "open" if state == "open" else "closed" if state == "closed" else "all"
    raw = gh(f'issue list --repo {repo} --state {gh_state} --limit 40 --json number,title,labels,state,body,closedAt {label_filters}')
    if not raw:
        return []
    issues = json.loads(raw)

    # For "all" with date filter: include closed only from last 14 days
    if state == "all":
        cutoff = datetime.now(timezone.utc).timestamp() - (14 * 86400)
        filtered = []
        for iss in issues:
            if iss["state"] == "OPEN":
                filtered.append(iss)
            elif iss.get("closedAt"):
                closed_ts = datetime.fromisoformat(iss["closedAt"].replace("Z", "+00:00")).timestamp()
                if closed_ts >= cutoff:
                    filtered.append(iss)
        issues = filtered

    return issues

def get_prs(repo):
    raw = gh(f'pr list --repo {repo} --state all --limit 50 --json number,title,state,mergedAt,headRefName,commits')
    if not raw:
        return []
    return json.loads(raw)

def get_recent_commits(n=80):
    raw = git(f"log --oneline -{n}")
    if not raw:
        return []
    lines = []
    for line in raw.splitlines():
        parts = line.split(" ", 1)
        if len(parts) == 2:
            lines.append({"sha": parts[0], "msg": parts[1]})
    return lines

def get_deployed_sha(deployed_tag_arg):
    """Try to determine the currently deployed commit SHA."""
    if deployed_tag_arg:
        return deployed_tag_arg
    # Try reading from variables file
    try:
        with open("scripts/aks/00-variables.sh") as f:
            for line in f:
                m = re.search(r'IMAGE_TAG[=:]+"?([a-f0-9]{7,40})"?', line)
                if m:
                    return m.group(1)
    except Exception:
        pass
    return None

def is_deployed(commit_sha, deployed_sha):
    """Return True if commit_sha is an ancestor of (or equal to) deployed_sha."""
    if not commit_sha or not deployed_sha:
        return None
    if commit_sha.startswith(deployed_sha) or deployed_sha.startswith(commit_sha):
        return True
    result = run(f"git merge-base --is-ancestor {commit_sha} {deployed_sha}", check=False)
    return result is not None  # exit 0 = is ancestor

def find_commit_for_issue(number, commits):
    pattern = re.compile(rf'#\s*{number}\b', re.IGNORECASE)
    for c in commits:
        if pattern.search(c["msg"]):
            return c["sha"]
    return None

def find_pr_for_issue(number, prs):
    pattern = re.compile(rf'#\s*{number}\b', re.IGNORECASE)
    # Check by branch name or PR title
    branch_pattern = re.compile(rf'(?:issue[-/]?{number}|{number}[-/])', re.IGNORECASE)
    for pr in prs:
        if pattern.search(pr.get("title", "")) or branch_pattern.search(pr.get("headRefName", "")):
            return pr
    return None

def check_docs(issue_body, commit_sha, commits):
    """
    Returns: 'done', 'needed', 'not_assessed', or 'na'
    """
    if not issue_body:
        return "not_assessed"
    body_lower = issue_body.lower()
    has_disposition = "docs disposition" in body_lower
    if not has_disposition:
        return "not_assessed"
    # Does it say docs are needed?
    needs_docs = any(kw in body_lower for kw in [
        "docs-feature", "docs-sync", "update docs", "needs docs", "yes →"
    ])
    no_docs = any(kw in body_lower for kw in [
        "no docs needed", "no behavior change", "internal", "not needed"
    ])
    if no_docs and not needs_docs:
        return "done"  # explicitly justified as not needed
    if needs_docs:
        # Check if a docs commit exists nearby
        if commit_sha:
            idx = next((i for i, c in enumerate(commits) if c["sha"] == commit_sha), None)
            if idx is not None:
                nearby = commits[max(0, idx-3):idx+4]
                for c in nearby:
                    if any(kw in c["msg"].lower() for kw in ["docs", "doc(", "docs("]):
                        return "done"
        return "needed"
    return "not_assessed"

def extract_agents(labels):
    agents = []
    for lbl in labels:
        name = lbl.get("name", "")
        if name.startswith(SQUAD_LABEL_PREFIX):
            member = name[len(SQUAD_LABEL_PREFIX):]
            agents.append(member.capitalize())
    return agents

def determine_status(issue, pr, commit_sha):
    state = issue["state"]
    if state == "CLOSED":
        return ("closed" if not commit_sha else commit_sha[:7]), "done"

    if pr:
        pr_state = pr["state"]
        if pr_state == "MERGED":
            return "merged", "done"
        if pr_state == "OPEN":
            return "in review", "in_progress"

    if commit_sha:
        return "implementing", "in_progress"

    labels = [l["name"] for l in issue.get("labels", [])]
    agents = extract_agents(issue.get("labels", []))
    if "squad:smith" in labels and len([l for l in labels if l.startswith("squad:")]) == 1:
        return "RCA in progress", "rca"
    if "go:needs-research" in labels:
        return "needs research", "rca"
    if "squad:smith" in labels:
        return "RCA in progress", "rca"

    return "backlog", "backlog"

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", default="sabbour/agentweaver")
    parser.add_argument("--filter", default="all",
                        choices=["open", "closed", "all", "bugs", "features", "chores"])
    parser.add_argument("--squad", default=None)
    parser.add_argument("--deployed-tag", default=None)
    args = parser.parse_args()

    state = "all" if args.filter in ("all", "bugs", "features", "chores") else args.filter
    issues = get_issues(args.repo, state, args.filter, args.squad)
    prs = get_prs(args.repo)
    commits = get_recent_commits(100)
    deployed_sha = get_deployed_sha(args.deployed_tag)

    results = []
    for iss in issues:
        number = iss["number"]
        labels = iss.get("labels", [])
        agents = extract_agents(labels)
        commit_sha = find_commit_for_issue(number, commits)
        pr = find_pr_for_issue(number, prs)

        status_text, status_key = determine_status(iss, pr, commit_sha)
        deployed = is_deployed(commit_sha, deployed_sha) if commit_sha else None
        docs = check_docs(iss.get("body", ""), commit_sha, commits)

        type_labels = [l["name"] for l in labels if l["name"].startswith(TYPE_LABEL_PREFIX)]
        issue_type = type_labels[0].replace(TYPE_LABEL_PREFIX, "") if type_labels else "unknown"

        results.append({
            "number": number,
            "title": iss["title"],
            "type": issue_type,
            "agents": agents,
            "state": iss["state"].lower(),
            "status_text": status_text,
            "status_key": status_key,
            "commit": commit_sha,
            "pr_number": pr["number"] if pr else None,
            "pr_state": pr["state"].lower() if pr else None,
            "deployed": deployed,
            "docs": docs,
        })

    print(json.dumps(results, indent=2))

if __name__ == "__main__":
    main()
