#!/usr/bin/env python3
"""Discover, validate, select, and run batches from the docs diagram inventory."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

DISPOSITIONS = {"unreviewed", "retain", "reuse", "merge", "remove", "redesign"}
STATUSES = {"queued", "in_progress", "blocked", "done"}
IMAGE_RE = re.compile(
    r"(?:docs/diagrams/|(?:\.\./)+diagrams/|/diagrams/)"
    r"(?P<name>[A-Za-z0-9][A-Za-z0-9._-]*)\.png"
)


def relative_posix(path: Path, root: Path) -> str:
    return path.relative_to(root).as_posix()


def docs_area(markdown_path: Path, docs_root: Path) -> str:
    relative = markdown_path.relative_to(docs_root)
    return relative.parts[0] if len(relative.parts) > 1 else "root"


def read_existing(path: Path) -> dict[str, dict]:
    if not path.exists():
        return {}
    data = json.loads(path.read_text(encoding="utf-8"))
    return {entry["name"]: entry for entry in data.get("diagrams", [])}


def discover(repo: Path, output: Path) -> dict:
    docs_root = repo / "docs"
    sources_root = docs_root / "diagrams" / "src"
    existing = read_existing(output)
    sources: dict[str, tuple[str, str]] = {}
    if sources_root.exists():
        for source in sorted(sources_root.iterdir()):
            if source.suffix.lower() not in {".drawio", ".json"}:
                continue
            if source.name.endswith("-spec.schema.json"):
                continue
            name = source.stem
            if name in sources:
                raise ValueError(f"ambiguous source basename: {name}")
            sources[name] = (relative_posix(source, repo), source.suffix.lower()[1:])

    references: dict[str, set[str]] = {}
    areas: dict[str, set[str]] = {}
    for markdown in sorted(docs_root.rglob("*.md")):
        text = markdown.read_text(encoding="utf-8")
        for match in IMAGE_RE.finditer(text):
            name = match.group("name")
            references.setdefault(name, set()).add(relative_posix(markdown, repo))
            areas.setdefault(name, set()).add(docs_area(markdown, docs_root))

    names = sorted(set(sources) | set(references))
    entries = []
    for name in names:
        prior = existing.get(name, {})
        referenced_areas = sorted(areas.get(name, set()))
        default_owner = referenced_areas[0] if len(referenced_areas) == 1 else "shared"
        source_path, source_kind = sources.get(name, (None, "missing"))
        entries.append(
            {
                "name": name,
                "source_path": source_path,
                "source_kind": source_kind,
                "output_path": f"docs/diagrams/{name}.png",
                "references": sorted(references.get(name, set())),
                "referenced_areas": referenced_areas,
                "owner_area": prior.get("owner_area", default_owner),
                "disposition": prior.get("disposition", "unreviewed"),
                "target": prior.get("target"),
                "status": prior.get("status", "queued"),
                "rationale": prior.get("rationale", ""),
            }
        )
    return {"version": 1, "diagrams": entries}


def validate(data: dict) -> list[str]:
    errors: list[str] = []
    if data.get("version") != 1:
        errors.append("version must be 1")
    entries = data.get("diagrams")
    if not isinstance(entries, list):
        return errors + ["diagrams must be an array"]
    seen: set[str] = set()
    names = {entry.get("name") for entry in entries if isinstance(entry, dict)}
    for index, entry in enumerate(entries):
        prefix = f"diagrams[{index}]"
        if not isinstance(entry, dict):
            errors.append(f"{prefix} must be an object")
            continue
        name = entry.get("name")
        if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", name):
            errors.append(f"{prefix}.name is invalid")
            continue
        if name in seen:
            errors.append(f"duplicate diagram name: {name}")
        seen.add(name)
        expected_output = f"docs/diagrams/{name}.png"
        if entry.get("output_path") != expected_output:
            errors.append(f"{name}: output_path must remain {expected_output}")
        if entry.get("disposition") not in DISPOSITIONS:
            errors.append(f"{name}: invalid disposition")
        if entry.get("status") not in STATUSES:
            errors.append(f"{name}: invalid status")
        if not entry.get("owner_area"):
            errors.append(f"{name}: owner_area is required")
        disposition = entry.get("disposition")
        target = entry.get("target")
        if disposition in {"reuse", "merge"}:
            if not target or target == name or target not in names:
                errors.append(f"{name}: {disposition} requires a different existing target")
        if entry.get("status") == "done" and disposition == "unreviewed":
            errors.append(f"{name}: a completed entry cannot remain unreviewed")
        if entry.get("status") == "done" and disposition == "remove" and entry.get("references"):
            errors.append(f"{name}: removed diagram still has documentation references")
    return errors


def load_validated(path: Path) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    errors = validate(data)
    if errors:
        raise ValueError("\n".join(errors))
    return data


def selected_entries(data: dict, args: argparse.Namespace) -> list[dict]:
    names = set(args.name or [])
    areas = set(args.area or [])
    dispositions = set(args.disposition or [])
    statuses = set(args.status or [])
    result = []
    for entry in data["diagrams"]:
        if names and entry["name"] not in names:
            continue
        if areas and entry["owner_area"] not in areas:
            continue
        if dispositions and entry["disposition"] not in dispositions:
            continue
        if statuses and entry["status"] not in statuses:
            continue
        result.append(entry)
    missing = names - {entry["name"] for entry in result}
    if missing:
        raise ValueError(f"diagram names not selected: {', '.join(sorted(missing))}")
    return sorted(result, key=lambda entry: entry["name"])


def add_filters(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--name", action="append")
    parser.add_argument("--area", action="append")
    parser.add_argument("--disposition", action="append", choices=sorted(DISPOSITIONS))
    parser.add_argument("--status", action="append", choices=sorted(STATUSES))


def build_run_command(
    names: list[str],
    action: str,
    npm_command: str | None = None,
) -> list[str]:
    if action not in {"render", "check"}:
        raise ValueError(f"unsupported action: {action}")
    npm = npm_command or ("npm.cmd" if os.name == "nt" else "npm")
    script = "docs:render-diagrams" if action == "render" else "docs:check-diagrams"
    command = [npm, "run", script, "--"]
    for name in names:
        command.extend(["--spec", name])
    return command


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)

    discover_parser = commands.add_parser("discover")
    discover_parser.add_argument("--repo", type=Path, default=Path.cwd())
    discover_parser.add_argument("--output", type=Path, required=True)

    validate_parser = commands.add_parser("validate")
    validate_parser.add_argument("inventory", type=Path)

    select_parser = commands.add_parser("select")
    select_parser.add_argument("inventory", type=Path)
    add_filters(select_parser)
    select_parser.add_argument("--format", choices=["names", "json", "args"], default="names")

    run_parser = commands.add_parser("run")
    run_parser.add_argument("inventory", type=Path)
    run_parser.add_argument("--repo", type=Path, default=Path.cwd())
    run_parser.add_argument("--action", choices=["render", "check"], required=True)
    run_parser.add_argument("--dry-run", action="store_true")
    add_filters(run_parser)

    args = parser.parse_args()
    try:
        if args.command == "discover":
            repo = args.repo.resolve()
            output = args.output if args.output.is_absolute() else repo / args.output
            data = discover(repo, output)
            errors = validate(data)
            if errors:
                raise ValueError("\n".join(errors))
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
            print(f"wrote {len(data['diagrams'])} diagrams to {output}")
            return 0

        data = load_validated(args.inventory)
        if args.command == "validate":
            print(f"valid inventory: {len(data['diagrams'])} diagrams")
            return 0

        entries = selected_entries(data, args)
        names = [entry["name"] for entry in entries]
        if args.command == "select":
            if args.format == "json":
                print(json.dumps(entries, indent=2))
            elif args.format == "args":
                print(" ".join(f"--spec {name}" for name in names))
            else:
                print("\n".join(names))
            return 0

        if not names:
            raise ValueError("selection is empty")
        command = build_run_command(names, args.action)
        print(" ".join(command))
        if args.dry_run:
            return 0
        return subprocess.run(command, cwd=args.repo.resolve(), check=False).returncode
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
