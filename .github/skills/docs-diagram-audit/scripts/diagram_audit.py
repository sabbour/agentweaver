#!/usr/bin/env python3
"""Discover and validate the complete Agentweaver documentation diagram catalog."""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

DISPOSITIONS = {"unreviewed", "retain", "reuse", "merge", "remove", "redesign"}
STATUSES = {"queued", "in_progress", "blocked", "done"}
DIAGRAM_REFERENCE_RE = re.compile(
    r"(?P<target>(?:docs/diagrams/|(?:\.\./)+diagrams/|/diagrams/)"
    r"(?P<name>[A-Za-z0-9][A-Za-z0-9._-]*)\.png(?:[?#][^)\s\"'<>]*)?)"
)
ALT_RE = re.compile(r"!\[(?P<alt>[^\]]*)\]\((?P<target>[^)\s]+)")


def relative(path: Path, root: Path) -> str:
    return path.relative_to(root).as_posix()


def area_for(path: Path, docs_root: Path) -> str:
    if not path.is_relative_to(docs_root):
        return "shared"
    rel = path.relative_to(docs_root)
    return rel.parts[0] if len(rel.parts) > 1 else "root"


def existing_by_name(report_path: Path) -> tuple[dict[str, dict], dict[str, dict], dict]:
    if not report_path.exists():
        return {}, {}, {}
    data = json.loads(report_path.read_text(encoding="utf-8"))
    diagrams = {entry["name"]: entry for entry in data.get("diagrams", [])}
    concepts = {entry["id"]: entry for entry in data.get("concepts", [])}
    return diagrams, concepts, data.get("ground_truth", {})


def discover(repo: Path, report_path: Path) -> dict:
    docs = repo / "docs"
    diagram_root = docs / "diagrams"
    source_root = diagram_root / "src"
    prior_diagrams, prior_concepts, prior_ground_truth = existing_by_name(report_path)

    sources: dict[str, list[str]] = {}
    if source_root.exists():
        for path in sorted(source_root.iterdir()):
            if path.suffix.lower() not in {".drawio", ".json"}:
                continue
            if path.name.endswith("-spec.schema.json"):
                continue
            sources.setdefault(path.stem, []).append(relative(path, repo))

    images = {
        path.stem: relative(path, repo)
        for path in sorted(diagram_root.glob("*.png"))
    }
    hashes = {
        path.name.removesuffix(".hash.txt"): relative(path, repo)
        for path in sorted(diagram_root.glob("*.hash.txt"))
    }
    references: dict[str, list[dict]] = {}
    referenced_areas: dict[str, set[str]] = {}
    broken_references: list[str] = []
    pages = [
        page for page in docs.rglob("*.md")
        if not any(part in {"node_modules", ".vitepress"} for part in page.relative_to(docs).parts)
        and not page.is_relative_to(diagram_root / "reviews")
    ]
    pages.extend(repo / name for name in ("README.md", "AGENTS.md", "CONTRIBUTING.md", "RELEASING.md")
                 if (repo / name).is_file())
    for markdown in sorted(pages):
        for line_number, line in enumerate(markdown.read_text(encoding="utf-8").splitlines(), 1):
            alt_by_target = {
                match.group("target"): match.group("alt")
                for match in ALT_RE.finditer(line)
            }
            matches = [(match.group("target"), match.group("name"))
                       for match in DIAGRAM_REFERENCE_RE.finditer(line)]
            for target in re.findall(r"\[[^\]]*\]\(([^)\s]+\.png(?:[?#][^)\s]*)?)\)", line):
                resolved = (markdown.parent / target.split("#", 1)[0].split("?", 1)[0]).resolve()
                if resolved.parent == diagram_root.resolve() and target not in {t for t, _ in matches}:
                    matches.append((target, resolved.stem))
            for target, name in matches:
                normalized = target.split("#", 1)[0].split("?", 1)[0]
                reference = {
                    "path": relative(markdown, repo),
                    "line": line_number,
                    "alt": alt_by_target.get(target, ""),
                    "target": target,
                }
                references.setdefault(name, []).append(reference)
                referenced_areas.setdefault(name, set()).add(area_for(markdown, docs))
                if normalized.startswith("docs/"):
                    resolved = (repo / normalized).resolve()
                elif normalized.startswith("/diagrams/"):
                    resolved = (docs / normalized.lstrip("/")).resolve()
                else:
                    resolved = (markdown.parent / normalized).resolve()
                if not resolved.exists():
                    broken_references.append(f"{reference['path']}:{line_number} -> {target}")

    names = sorted(set(sources) | set(images) | set(hashes) | set(references))
    diagrams = []
    for name in names:
        prior = prior_diagrams.get(name, {})
        areas = sorted(referenced_areas.get(name, set()))
        owner = prior.get("owner_area") or (areas[0] if len(areas) == 1 else "shared")
        diagrams.append(
            {
                "name": name,
                "concept_id": prior.get("concept_id", name),
                "source_paths": sources.get(name, []),
                "image_path": images.get(name),
                "hash_path": hashes.get(name),
                "references": references.get(name, []),
                "owner_area": owner,
                "disposition": prior.get("disposition", "unreviewed"),
                "target": prior.get("target"),
                "status": prior.get("status", "queued"),
                "stable_output_path": prior.get("stable_output_path", True),
                "path_change_reason": prior.get("path_change_reason"),
                "legacy_assessment": prior.get("legacy_assessment", ""),
                "tombstone": False,
            }
        )

    current_names = {entry["name"] for entry in diagrams}
    for name, prior in sorted(prior_diagrams.items()):
        if name in current_names:
            continue
        if prior.get("disposition") not in {"remove", "reuse", "merge"} and not prior.get("tombstone"):
            continue
        tombstone = dict(prior)
        tombstone["tombstone"] = True
        tombstone.setdefault("source_paths", [])
        tombstone.setdefault("image_path", None)
        tombstone.setdefault("hash_path", None)
        tombstone.setdefault("references", [])
        tombstone.setdefault("stable_output_path", False)
        tombstone.setdefault("path_change_reason", "Historical artifact removed or consolidated")
        diagrams.append(tombstone)
    diagrams.sort(key=lambda entry: entry["name"])

    concept_ids = sorted({entry["concept_id"] for entry in diagrams})
    concepts = []
    for concept_id in concept_ids:
        prior = prior_concepts.get(concept_id, {})
        member_entries = [entry for entry in diagrams if entry["concept_id"] == concept_id]
        members = sorted(entry["name"] for entry in member_entries)
        owners = {entry["owner_area"] for entry in member_entries}
        concepts.append(
            {
                "id": concept_id,
                "title": prior.get("title", concept_id.replace("-", " ").title()),
                "disposition": prior.get("disposition", "unreviewed"),
                "target": prior.get("target"),
                "canonical_diagram": prior.get("canonical_diagram", members[0] if members else None),
                "member_diagrams": members,
                "owner_area": prior.get("owner_area", owners.pop() if len(owners) == 1 else "shared"),
                "status": prior.get("status", "queued"),
                "rationale": prior.get("rationale", ""),
                "evidence": prior.get("evidence", []),
                "pitch_skill_completed": prior.get("pitch_skill_completed", False),
                "iterate_skill_completed": prior.get("iterate_skill_completed", False),
                "tombstone": bool(member_entries) and all(entry.get("tombstone") for entry in member_entries),
            }
        )

    current_concept_ids = {entry["id"] for entry in concepts}
    for concept_id, prior in sorted(prior_concepts.items()):
        if concept_id in current_concept_ids:
            continue
        if prior.get("disposition") not in {"remove", "reuse", "merge"} and not prior.get("tombstone"):
            continue
        tombstone = dict(prior)
        tombstone["tombstone"] = True
        tombstone.setdefault("member_diagrams", [])
        tombstone.setdefault("canonical_diagram", None)
        concepts.append(tombstone)
    concepts.sort(key=lambda entry: entry["id"])

    source_names = set(sources)
    image_names = set(images)
    hash_names = set(hashes)
    referenced_names = set(references)
    return {
        "version": 1,
        "scope": "docs-only",
        "ground_truth": {
            "repository_sources": prior_ground_truth.get("repository_sources", []),
            "current_docs": prior_ground_truth.get("current_docs", []),
            "legacy_diagrams_role": "assessment-only",
        },
        "concepts": concepts,
        "diagrams": diagrams,
        "integrity": {
            "orphan_sources": sorted(source_names - image_names | (source_names - referenced_names)),
            "orphan_images": sorted(image_names - source_names | (image_names - referenced_names)),
            "orphan_hashes": sorted(hash_names - source_names | (hash_names - image_names)),
            "broken_references": sorted(broken_references),
            "ambiguous_sources": sorted(name for name, paths in sources.items() if len(paths) > 1),
        },
    }


def validate(report: dict, final: bool = False) -> list[str]:
    errors: list[str] = []
    if report.get("version") != 1:
        errors.append("version must be 1")
    if report.get("scope") != "docs-only":
        errors.append("scope must be docs-only")
    ground_truth = report.get("ground_truth", {})
    if ground_truth.get("legacy_diagrams_role") != "assessment-only":
        errors.append("legacy diagrams must be assessment-only")

    diagrams = report.get("diagrams")
    concepts = report.get("concepts")
    if not isinstance(diagrams, list) or not isinstance(concepts, list):
        return errors + ["concepts and diagrams must be arrays"]
    diagram_names = [entry.get("name") for entry in diagrams]
    concept_ids = [entry.get("id") for entry in concepts]
    if len(diagram_names) != len(set(diagram_names)):
        errors.append("diagram names must be unique")
    if len(concept_ids) != len(set(concept_ids)):
        errors.append("concept ids must be unique")
    diagram_set = set(diagram_names)
    concept_set = set(concept_ids)

    for entry in diagrams:
        name = entry.get("name", "<unknown>")
        disposition = entry.get("disposition")
        if disposition not in DISPOSITIONS:
            errors.append(f"{name}: invalid disposition")
        if entry.get("status") not in STATUSES:
            errors.append(f"{name}: invalid status")
        if entry.get("concept_id") not in concept_set:
            errors.append(f"{name}: concept_id does not exist")
        target = entry.get("target")
        if disposition in {"reuse", "merge"}:
            if not target or target not in diagram_set or target == name:
                errors.append(f"{name}: {disposition} requires a different existing diagram target")
            else:
                target_entry = next(item for item in diagrams if item.get("name") == target)
                if target_entry.get("disposition") == "remove":
                    errors.append(f"{name}: historical target {target} is removed")
        if not entry.get("stable_output_path") and not entry.get("path_change_reason"):
            errors.append(f"{name}: intentional path change requires path_change_reason")
        if entry.get("tombstone") and disposition not in {"remove", "reuse", "merge"}:
            errors.append(f"{name}: tombstone must be remove, reuse, or merge")
        if final and (disposition == "unreviewed" or entry.get("status") != "done"):
            errors.append(f"{name}: final report requires reviewed, completed diagrams")

    for concept in concepts:
        concept_id = concept.get("id", "<unknown>")
        disposition = concept.get("disposition")
        if disposition not in DISPOSITIONS:
            errors.append(f"{concept_id}: invalid concept disposition")
        target = concept.get("target")
        if disposition in {"reuse", "merge"}:
            if not target or target not in concept_set or target == concept_id:
                errors.append(f"{concept_id}: {disposition} requires a different existing concept target")
            else:
                target_entry = next(item for item in concepts if item.get("id") == target)
                if target_entry.get("disposition") == "remove":
                    errors.append(f"{concept_id}: historical target {target} is removed")
        if concept.get("tombstone") and disposition not in {"remove", "reuse", "merge"}:
            errors.append(f"{concept_id}: tombstone must be remove, reuse, or merge")
        canonical = concept.get("canonical_diagram")
        survives = disposition in {"retain", "redesign"}
        if survives and canonical not in diagram_set:
            errors.append(f"{concept_id}: surviving concept requires a canonical diagram")
        if final:
            if disposition == "unreviewed" or concept.get("status") != "done":
                errors.append(f"{concept_id}: final report requires a reviewed, completed concept")
            if survives:
                if not concept.get("pitch_skill_completed"):
                    errors.append(f"{concept_id}: docs-diagram-pitch not completed")
                if not concept.get("iterate_skill_completed"):
                    errors.append(f"{concept_id}: docs-diagram-iterate not completed")
                if not concept.get("evidence"):
                    errors.append(f"{concept_id}: ground-truth evidence is required")

    if final:
        if not ground_truth.get("repository_sources") or not ground_truth.get("current_docs"):
            errors.append("final report requires repository and current-doc ground-truth records")
        for category, values in report.get("integrity", {}).items():
            if values:
                errors.append(f"integrity.{category} must be empty in a final report")
    return errors


def summary(report: dict) -> str:
    concept_counts = Counter(entry["disposition"] for entry in report["concepts"])
    diagram_counts = Counter(entry["disposition"] for entry in report["diagrams"])
    owner_counts = Counter(entry["owner_area"] for entry in report["concepts"])
    lines = [
        "# Documentation diagram audit",
        "",
        f"- Concepts: {len(report['concepts'])}",
        f"- Diagrams: {len(report['diagrams'])}",
        "- Ground truth: repository sources and current written documentation",
        "- Legacy diagrams: assessment only",
        "",
        "## Ground-truth sources",
        "",
        "### Repository",
        "",
    ]
    lines.extend(
        f"- `{source}`" for source in report.get("ground_truth", {}).get("repository_sources", [])
    )
    if not report.get("ground_truth", {}).get("repository_sources"):
        lines.append("- Not recorded")
    lines.extend(["", "### Current written docs", ""])
    lines.extend(
        f"- `{source}`" for source in report.get("ground_truth", {}).get("current_docs", [])
    )
    if not report.get("ground_truth", {}).get("current_docs"):
        lines.append("- Not recorded")
    lines.extend([
        "",
        "## Concept dispositions",
        "",
    ])
    lines.extend(f"- {key}: {concept_counts.get(key, 0)}" for key in sorted(DISPOSITIONS))
    lines.extend(["", "## Diagram dispositions", ""])
    lines.extend(f"- {key}: {diagram_counts.get(key, 0)}" for key in sorted(DISPOSITIONS))
    lines.extend(["", "## Area ownership", ""])
    lines.extend(f"- {owner}: {owner_counts[owner]}" for owner in sorted(owner_counts))
    lines.extend(["", "## Canonical concepts", ""])
    for concept in sorted(report["concepts"], key=lambda item: item["id"]):
        lines.append(
            f"- `{concept['id']}`: {concept['disposition']} -> "
            f"`{concept.get('canonical_diagram') or concept.get('target') or 'none'}` "
            f"({concept['owner_area']})"
        )
    consolidations = [
        entry for entry in report["concepts"]
        if entry["disposition"] in {"reuse", "merge"}
    ]
    lines.extend(["", "## Consolidations", ""])
    lines.extend(
        f"- `{entry['id']}` {entry['disposition']} -> `{entry['target']}`"
        for entry in consolidations
    )
    if not consolidations:
        lines.append("- None")
    path_changes = [
        entry for entry in report["diagrams"]
        if not entry.get("stable_output_path", True)
    ]
    lines.extend(["", "## Intentional path changes", ""])
    lines.extend(
        f"- `{entry['name']}`: {entry.get('path_change_reason') or 'reason missing'}"
        for entry in path_changes
    )
    if not path_changes:
        lines.append("- None")
    lines.extend(["", "## Integrity", ""])
    for category, values in report["integrity"].items():
        lines.append(f"- {category}: {len(values)}")
    blocked = [
        f"{entry['id']}: {entry['rationale']}"
        for entry in report["concepts"]
        if entry["status"] == "blocked"
    ]
    lines.extend(["", "## Blockers", ""])
    lines.extend(f"- {item}" for item in blocked)
    if not blocked:
        lines.append("- None")
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    discover_parser = commands.add_parser("discover")
    discover_parser.add_argument("--repo", type=Path, default=Path.cwd())
    discover_parser.add_argument("--output", type=Path, required=True)
    validate_parser = commands.add_parser("validate")
    validate_parser.add_argument("report", type=Path)
    validate_parser.add_argument("--final", action="store_true")
    summary_parser = commands.add_parser("summary")
    summary_parser.add_argument("report", type=Path)
    summary_parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    try:
        if args.command == "discover":
            repo = args.repo.resolve()
            output = args.output if args.output.is_absolute() else repo / args.output
            report = discover(repo, output)
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            print(f"wrote {len(report['diagrams'])} diagrams and {len(report['concepts'])} concepts")
            return 0
        report = json.loads(args.report.read_text(encoding="utf-8"))
        if args.command == "validate":
            errors = validate(report, final=args.final)
            if errors:
                print("\n".join(errors), file=sys.stderr)
                return 1
            print("audit report is valid")
            return 0
        output = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(summary(report), encoding="utf-8")
        print(f"wrote {output}")
        return 0
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
