"""Record measured, incomplete execution-plan work without claiming publication."""
from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

from PIL import Image

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
PLAN = json.loads((REPO / ".github/skills/docs-diagram-audit/reports/plan-deep-dive-execution.json").read_text())
MODELS = json.loads((HERE / "content-models.json").read_text())["diagrams"]
AUDIT = json.loads((REPO / ".github/skills/docs-diagram-audit/reports/deep-dive-execution.json").read_text())
ALLOWED = PLAN["exclusive_asset_paths"] + PLAN["document_paths"]


def write(path, contents):
    relative = path.relative_to(REPO).as_posix()
    if not any(relative == entry or (entry.endswith("/") and relative.startswith(entry)) for entry in ALLOWED):
        raise ValueError(f"Outside execution plan: {relative}")
    path.write_text(contents, encoding="utf-8")


def save(path, value):
    write(path, json.dumps(value, indent=2) + "\n")


def load_helper(name, filename):
    spec = importlib.util.spec_from_file_location(name, REPO / ".github/skills/docs-diagram-iterate/scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


growth = load_helper("execution_growth", "check_xml_growth.py")
manifests = load_helper("execution_manifest", "validate_iteration_manifest.py")
results = []
inspections = {
    item["diagram"]: item
    for item in json.loads((HERE / "pass-01-inspections.json").read_text())["inspections"]
}
assert set(inspections) == {model["name"] for model in MODELS}
for model in MODELS:
    name = model["name"]
    directory = HERE.parent / name
    stages = {}
    for stage in ("pitch", "pass-01"):
        source = directory / f"{name}-{stage}.drawio"
        png = source.with_suffix(".png")
        text = source.read_text(encoding="utf-8")
        root, graph = growth.parse_document(text, str(source))
        assert graph.get("pageWidth") == "793.7" and graph.get("pageHeight") == "559.37"
        cells = {cell.get("id"): cell for cell in root.iter("mxCell")}
        edges = [cell for cell in cells.values() if cell.get("edge") == "1"]
        assert all(edge.get("source") in cells and edge.get("target") in cells for edge in edges)
        with Image.open(png) as image:
            dimensions = image.size
            assert image.format == "PNG"
            image.verify()
        with Image.open(png) as image:
            image.load()
        record_path = source.with_suffix(".record.json")
        record = json.loads(record_path.read_text())
        record["png_inspected_print"] = stage == "pitch" or inspections[name]["png_inspected_print"]
        record["png_inspected_enlarged"] = stage == "pitch" or inspections[name]["png_inspected_enlarged"]
        record["symbols_status"] = "design-intent-only; resource semantics not approved"
        record["sha256"] = {
            "drawio": hashlib.sha256(source.read_bytes()).hexdigest(),
            "png": hashlib.sha256(png.read_bytes()).hexdigest(),
        }
        record["png_dimensions"] = dimensions
        record["xml_edge_count"] = len(edges)
        save(record_path, record)
        stages[stage] = record

    assessment = growth.assess(
        (directory / f"{name}-pitch.drawio").read_text(encoding="utf-8"),
        (directory / f"{name}-pass-01.drawio").read_text(encoding="utf-8"),
    )
    write(directory / f"{name}-pitch.md",
          f"# {name}: pitch inspection\n\n"
          f"Takeaway: {model['takeaway']}\n\n"
          "Audience: operators and contributors reading the execution deep dives.\n"
          "One editable A5 landscape page. Exported with draw.io Desktop 31.4.5.\n"
          "Opened enlarged and in the saved A5-scale contact sheets.\n"
          "Sparse generic cards are a sketch, not an approved native-notation composition.\n"
          "Missing hierarchy, relationship detail and process boundaries require redesign.\n\n"
          f"Evidence: `{model['evidence']}`.\n\n"
          "Fluent palette/template/library: repository-owned docs/diagrams/drawio assets.\n"
          "Native symbol proposals and source-backed content: content-models.json and the\n"
          "three research reports in the canonical-pod-process-boundaries review directory.\n")
    write(directory / f"{name}-pass-01.md",
          f"# {name}: incomplete first upgrade\n\n"
          "Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,\n"
          "pill badges and source-backed relationship detail. Pinned Desktop export succeeded.\n\n"
          f"Meaningful XML: {assessment['baseline_meaningful_xml']} -> "
          f"{assessment['result_meaningful_xml']} ({assessment['growth_ratio']}x; required 9x).\n"
          "Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.\n"
          "No baseline was replaced and no invisible/padding growth workaround was used.\n\n"
          "Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.\n"
          "Recorded first-upgrade findings:\n\n"
          + "".join(f"- {finding}\n" for finding in inspections[name]["findings"])
          + "\nThese findings are not a complete final arrow trace.\n\n"
          "Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.\n"
          "No final pass, final arrow trace, approved native-asset inventory or promotion.\n\n"
          f"Evidence: `{model['evidence']}`.\n")
    pitch = {
        "drawio": f"{name}-pitch.drawio", "png": f"{name}-pitch.png",
        "change_record": f"{name}-pitch.md",
        "png_inspected_print": True, "png_inspected_enlarged": True,
    }
    first = {
        "number": 1, "mode": "visual-upgrade",
        "drawio": f"{name}-pass-01.drawio", "png": f"{name}-pass-01.png",
        "change_record": f"{name}-pass-01.md",
        "png_inspected_print": inspections[name]["png_inspected_print"],
        "png_inspected_enlarged": inspections[name]["png_inspected_enlarged"],
        "all_arrows_traced": False,
        **{key: assessment[key] for key in (
            "baseline_meaningful_xml", "result_meaningful_xml", "growth_metric", "growth_ratio")},
    }
    manifest = {
        "diagram": name, "orientation": "A5-landscape", "pitch": pitch,
        "passes": [first], "final_pass": None,
    }
    save(directory / "iteration-manifest.incomplete.json", manifest)
    errors = manifests.validate_manifest(manifest)
    assert errors, "An incomplete manifest must not pass publication validation"
    results.append({
        "diagram": name, "publication_status": "blocked",
        "growth": assessment, "artifacts": stages, "manifest_errors": errors,
        "final_pass": None, "promoted": False,
    })

for stage in ("pitch", "pass-01"):
    save(HERE / f"{stage}-exports.json", [result["artifacts"][stage] for result in results])

links = []
for document in PLAN["document_paths"]:
    path = REPO / document
    for target in re.findall(r"\]\(([^)\s]+)(?:\s+[^)]*)?\)", path.read_text(encoding="utf-8")):
        target = target.split("#")[0]
        if not target or "://" in target or target.startswith(("/", "mailto:")):
            continue
        if target.startswith(("./", "../")):
            links.append({"document": document, "target": target,
                          "exists": (path.parent / target).resolve().exists()})

concepts = []
for document in AUDIT["documents"]:
    for concept in document["concepts"]:
        concepts.append({
            "id": concept["id"], "document": document["path"],
            "disposition": concept["disposition"], "representation": concept["representation"],
            "canonical_diagram": concept.get("canonical_diagram"), "target": concept.get("target"),
            "execution_status": "partial-docs-only; visual completion not certified",
        })
assert {item["id"] for item in concepts} == set(PLAN["concept_ids"])
save(HERE / "concept-progress.json", concepts)
save(HERE / "validation-results.json", {
    "status": "blocked", "research_agents": 3, "research_model": "gpt-6-astra",
    "renderer_version": "31.4.5", "survivor_drafts": len(results),
    "exported_pairs": len(results) * 2, "promoted_diagrams": 0,
    "first_upgrade_pngs_inspected_both_scales": sum(
        item["png_inspected_print"] and item["png_inspected_enlarged"]
        for item in inspections.values()
    ),
    "growth_passes": sum(item["growth"]["passed"] for item in results),
    "results": results, "relative_links": links,
    "missing_relative_links": [link for link in links if not link["exists"]],
})
ratios = [item["growth"]["growth_ratio"] for item in results]
print(json.dumps({
    "status": "blocked", "exported_pairs": len(results) * 2,
    "growth_range": [min(ratios), max(ratios)],
    "growth_passes": sum(item["growth"]["passed"] for item in results),
    "links_checked": len(links), "missing_links": sum(not link["exists"] for link in links),
    "concepts_recorded": len(concepts),
}, indent=2))
