"""Read-only scoped checks plus one owned execution/validation record."""

import hashlib
import importlib.util
import json
import re
import struct
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
PLAN = REPO / ".github" / "skills" / "docs-diagram-audit" / "reports" / "plan-deep-dive-orchestration.json"
REPORT = PLAN.with_name("deep-dive-orchestration.json")
plan = json.loads(PLAN.read_text(encoding="utf-8"))
report = json.loads(REPORT.read_text(encoding="utf-8"))
retired = [d for d in report["diagrams"] if d["disposition"] in {"remove", "merge"}]
survivors = [d["name"] for d in report["diagrams"] if d["disposition"] in {"retain", "redesign"}]
failures = []
links = 0
images = 0
for relative in plan["document_paths"]:
    path = REPO / relative
    raw = path.read_text(encoding="utf-8")
    text = re.sub(r"^```.*?^```\s*$", "", raw, flags=re.M | re.S)
    for match in re.finditer(r"(!?)\[[^\]]*\]\(([^)\s]+)(?:\s+[^)]*)?\)", text):
        target = match[2]
        if re.match(r"(?:[a-z]+:|#|/)", target, re.I):
            continue
        destination = target.split("#", 1)[0].split("?", 1)[0]
        if not destination:
            continue
        links += 1
        images += bool(match[1])
        resolved = path.parent / destination
        if not resolved.exists() and not resolved.with_suffix(".md").exists():
            failures.append(f"{relative}: missing {target}")
    for target in re.findall(r"\.\./diagrams/src/[\w-]+\.(?:json|drawio)", raw):
        if not (path.parent / target).exists():
            failures.append(f"{relative}: missing provenance {target}")
    for diagram in retired:
        if re.search(r"\.\./diagrams/(?:src/)?" + re.escape(diagram["name"]) + r"\.(?:png|json|drawio)", raw):
            failures.append(f"{relative}: still references retired {diagram['name']}")

retirements = []
for diagram in retired:
    name = diagram["name"]
    directory = REPO / "docs" / "diagrams" / "reviews" / name
    for relative, archive in [
        (f"docs/diagrams/src/{name}.json", "legacy.json"),
        (f"docs/diagrams/drawio/generated/{name}.drawio", "legacy.drawio"),
        (f"docs/diagrams/{name}.png", "legacy.png"),
        (f"docs/diagrams/{name}.hash.txt", "legacy.hash.txt"),
    ]:
        if (REPO / relative).exists() or not (directory / archive).exists():
            failures.append(f"Retirement incomplete: {relative}")
    retirements.append({"name": name, "disposition": diagram["disposition"], "target": diagram.get("target"), "archive": directory.relative_to(REPO).as_posix()})

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location(
    "growth", REPO / ".github" / "skills" / "docs-diagram-iterate" / "scripts" / "check_xml_growth.py")
growth = importlib.util.module_from_spec(spec)
spec.loader.exec_module(growth)
pitch = HERE / "coordinator-internals-fig4-pitch.drawio"
attempt = HERE / "coordinator-internals-fig4-pass-01.drawio"
measurement = growth.assess(pitch.read_text(encoding="utf-8"), attempt.read_text(encoding="utf-8"))
artifacts = []
review_api = HERE.parent / "review-merge-fig5"
for path in sorted([*HERE.iterdir(), *review_api.iterdir()]):
    if path.suffix not in {".drawio", ".png", ".md", ".txt"} or not path.is_file():
        continue
    data = path.read_bytes()
    item = {"path": path.relative_to(REPO).as_posix(), "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
    if path.suffix == ".png":
        if data[:8] != b"\x89PNG\r\n\x1a\n":
            failures.append(f"Invalid PNG signature: {path.name}")
        item["width"], item["height"] = struct.unpack(">II", data[16:24])
    if path.suffix == ".drawio":
        root = ET.fromstring(data)
        diagrams = root.findall("diagram")
        model = diagrams[0].find("mxGraphModel") if len(diagrams) == 1 else None
        if model is None or (model.get("pageWidth"), model.get("pageHeight")) not in {("794", "559"), ("559", "794")}:
            failures.append(f"Not one uncompressed A5 page: {path.name}")
        identifiers = [cell.get("id") for cell in root.iter("mxCell")]
        if len(identifiers) != len(set(identifiers)):
            failures.append(f"Duplicate XML cell IDs: {path.name}")
    artifacts.append(item)

build_log = HERE / "validation-build.txt"
build_passed = build_log.exists() and "build complete in" in build_log.read_text(encoding="utf-8")
execution = {
    "area": "deep-dive-orchestration",
    "status": "blocked",
    "model": "gpt-6-astra",
    "researchers": {
        "count": 3,
        "model": "gpt-6-astra",
        "reports": [
            "docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md",
            "docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md",
            "docs/diagrams/reviews/team-casting-fig1/research-supporting.md",
        ],
    },
    "documents_updated": plan["document_paths"],
    "retirements": retirements,
    "surviving_diagrams": survivors,
    "counts": {
        "planned_diagrams": 34, "documents_updated": 10,
        "removed": 2, "merged": 4, "surviving": 28,
        "pitches_exported_and_inspected": 2, "post_pitch_attempts": 1,
        "qualified_final_promotions": 0,
    },
    "renderer": {"product": "draw.io Desktop", "version": "31.4.5", "border": 16, "scale": 2},
    "growth": measurement,
    "first_sample_notification": {
        "diagram": "coordinator-internals-fig4",
        "absolute_png_path": str(HERE / "coordinator-internals-fig4-pass-01.png"),
        "status": "inspected post-pitch sample, not certified final",
        "continued_after_notification": True,
    },
    "additional_pitch": {
        "diagram": "review-merge-fig5",
        "manifest": "docs/diagrams/reviews/review-merge-fig5/pitch-manifest.json",
        "post_pitch_passes_completed": 0,
        "canonical_promoted": False,
    },
    "validation": {
        "relative_markdown_links_checked": links,
        "markdown_images_checked": images,
        "scoped_link_xml_png_retirement_failures": failures,
        "tests": {"command": "node --test scripts\\docs\\*.test.mjs", "passed": 27, "failed": 0, "log": "validation-tests.txt"},
        "legacy_drift": {"selected": 28, "passed": 28, "meaning": "Hash coherence only; not current factual/visual approval.", "log": "validation-drift.txt"},
        "docs_build": {"command": "npm run docs:build", "log": "validation-build.txt", "status": "passed" if build_passed else "not-confirmed"},
        "iteration_manifest": {"status": "failed", "reason": "Only one post-pitch pass; measured growth below 9x.", "log": "validation-iteration.txt"},
        "full_schema_publication_ready": False,
    },
    "blockers": [
        "Pass-1 semantic XML growth is 1.107985x, below the mandatory 9x; pitch baseline retained intact.",
        "Four post-pitch passes and final every-arrow trace are incomplete; no final promotion.",
        "All 28 surviving diagrams still lack fully qualified authored finals.",
        "Collective draft still needs complete group/metadata hierarchy and sharper normal-human versus recovered-escalation continuation semantics.",
        "Shared selection/default-workflow/memory canonicals require their owners' reconciliation; strict scope forbids changes here.",
    ],
    "scope": {
        "plan": PLAN.relative_to(REPO).as_posix(),
        "owned_report_updated": REPORT.relative_to(REPO).as_posix(),
        "runtime_or_workflow_behavior_changed": False,
        "shared_assets_or_coordinator_pilot_changed": False,
        "global_inventory_or_reconciliation_changed": False,
        "commits_created": 0,
    },
    "artifacts": artifacts,
}
(HERE / "execution-manifest.json").write_text(json.dumps(execution, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"links": links, "images": images, "retired": len(retirements), "surviving": len(survivors), "failures": failures}, indent=2))
raise SystemExit(1 if failures else 0)
