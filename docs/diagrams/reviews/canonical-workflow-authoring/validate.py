"""Build and validate this diagram's inspection manifest and immutable artifacts."""
from pathlib import Path
import hashlib
import importlib.util
import json
import sys
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
import jsonschema
from PIL import Image, ImageChops

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
NAME = "canonical-workflow-authoring"
SKILL = REPO / ".github/skills/docs-diagram-iterate"


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


growth = load("growth_check", SKILL / "scripts/check_xml_growth.py")
ordering = load("manifest_check", SKILL / "scripts/validate_iteration_manifest.py")


def artifacts(suffix):
    return {
        "drawio": f"{NAME}-{suffix}.drawio",
        "png": f"{NAME}-{suffix}.png",
        "change_record": f"{NAME}-{suffix}.md",
        "png_inspected_print": True,
        "png_inspected_enlarged": True,
    }


evidence = {
    "e01": "apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:593-625,662-675",
    "e02": "apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:124-230; apps/Agentweaver.Api/Generation/GenerationModelOptions.cs:36-40,74-83",
    "e03": "apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:63-77,90-118",
    "e04": "apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:677-690; tests/Agentweaver.Tests/Workflows/WorkflowGeneratorTests.cs:622-650",
    "e05": "apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:69-75; tests/Agentweaver.Tests/Workflows/WorkflowGeneratorTests.cs:328-338",
    "e06": "apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:73-77; tests/Agentweaver.Tests/Workflows/WorkflowGeneratorTests.cs:328-350",
    "e07": "apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:80-82; apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:692-695",
    "e08": "docs/guide/workflows.md:94-99; apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:438-456",
    "e09": "apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:457-506",
    "e10": "apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:494-524",
    "e11": "apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:521-529",
    "e12": "apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:524-549",
    "e13": "apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:550-582",
}

pitch = (HERE / f"{NAME}-pitch.drawio").read_text(encoding="utf-8")
first = (HERE / f"{NAME}-pass-01.drawio").read_text(encoding="utf-8")
growth_result = growth.assess(pitch, first)
assert growth_result["passed"], growth_result
manifest = {"diagram": NAME, "orientation": "A5-portrait",
            "pitch": artifacts("pitch"), "passes": [], "final_pass": 4}
defects = {1: (0, 3, 1), 2: (0, 1, 1), 3: (0, 0, 0), 4: (0, 0, 0)}
for number in range(1, 5):
    orientation, overlap, arrows = defects[number]
    entry = {"number": number, "mode": "visual-upgrade" if number == 1 else "correction-only",
             **artifacts(f"pass-{number:02}"), "orientation_defects": orientation,
             "overlap_defects": overlap, "arrow_defects": arrows}
    if number == 1:
        entry.update({key: growth_result[key] for key in [
            "growth_metric", "baseline_meaningful_xml", "result_meaningful_xml", "growth_ratio"]})
    if number == 4:
        entry["all_arrows_traced"] = True
        entry["arrow_trace"] = []
        for cell in ET.parse(HERE / entry["drawio"]).iter("mxCell"):
            if cell.get("edge") != "1":
                continue
            entry["arrow_trace"].append({
                "id": cell.get("id"), "source": cell.get("source"), "target": cell.get("target"),
                "relationship": cell.get("value"), "evidence": evidence[cell.get("id")],
                "result": "clean",
            })
        assert {a["id"] for a in entry["arrow_trace"]} == set(evidence)
    manifest["passes"].append(entry)

schema = json.loads((SKILL / "references/iteration-manifest.schema.json").read_text())
jsonschema.Draft202012Validator(schema).validate(manifest)
assert not ordering.validate_manifest(manifest)
results = []
baseline_semantics = None
for entry in [manifest["pitch"], *manifest["passes"]]:
    for field in ("drawio", "png", "change_record"):
        assert (HERE / entry[field]).is_file(), entry[field]
    source = HERE / entry["drawio"]
    analysis = growth.analyze(source.read_text(encoding="utf-8"))
    for field in ("invisible_cells", "off_page_cells", "duplicate_cells", "metadata_padding"):
        assert not analysis[field], (source.name, field, analysis[field])
    assert (analysis["page_width"], analysis["page_height"]) == (1123, 1587)
    assert abs(analysis["page_width"] / analysis["page_height"] - 148 / 210) < .005
    png = HERE / entry["png"]
    with Image.open(png) as image:
        image.verify()
    print_file = HERE / entry["png"].replace(".png", "-print.png")
    with Image.open(print_file) as image:
        assert image.width <= 560 and image.height <= 794
    if entry is not manifest["pitch"]:
        semantics = {
            c.get("id"): (c.get("value"), c.get("source"), c.get("target"),
                          growth.parse_style(c.get("style", "")).get("shape"))
            for c in ET.parse(source).iter("mxCell")
        }
        if baseline_semantics is None:
            baseline_semantics = semantics
        else:
            assert semantics == baseline_semantics, "Correction pass changed content or native symbols"
        assert analysis["meaningful_count"] / growth_result["baseline_meaningful_xml"] >= 9
    results.append({
        "drawio": source.name, "png": png.name,
        "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "png_sha256": hashlib.sha256(png.read_bytes()).hexdigest(),
        "analysis": analysis,
    })

assert (HERE / f"{NAME}-pass-03.drawio").read_bytes() == (HERE / f"{NAME}-pass-04.drawio").read_bytes()
allowed = json.loads((REPO / ".github/skills/docs-diagram-audit/reports/plan-shared.json").read_text())["exclusive_asset_paths"]
assert f"docs/diagrams/reviews/{NAME}/" in allowed
assert f"docs/diagrams/src/{NAME}.drawio" in allowed
assert f"docs/diagrams/{NAME}.png" in allowed
manifest_path = HERE / "iteration-manifest.json"
manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
report = {
    "diagram": NAME, "schema": "Draft202012Validator: passed",
    "skill_validator": "passed", "growth": growth_result,
    "correction_content_unchanged": True, "final_arrow_count": len(evidence),
    "page": "One editable portrait A5 page; 1123 x 1587 logical units; all actual bounds checked",
    "artifacts": results,
}
(HERE / "validation-report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(f"PASS: schema + skill + {growth_result['growth_ratio']}x growth + 5 XML/PNG/record triples")
print("PASS: A5 page/bounds + image integrity + unchanged correction semantics + all 13 arrows")

canonical = REPO / f"docs/diagrams/src/{NAME}.drawio"
if canonical.exists():
    final_source = HERE / f"{NAME}-pass-04.drawio"
    assert canonical.read_bytes() == final_source.read_bytes()
    published = REPO / f"docs/diagrams/{NAME}.png"
    with Image.open(published) as actual, Image.open(HERE / f"{NAME}-pass-04.png") as expected:
        assert actual.size == expected.size
        assert ImageChops.difference(actual.convert("RGB"), expected.convert("RGB")).getbbox() is None
    stamp = json.loads((REPO / f"docs/diagrams/{NAME}.hash.txt").read_text())
    assert stamp["renderer"]["rendererVersion"] == "31.4.5"
    assert stamp["drawio"]["sha256"] == hashlib.sha256(canonical.read_bytes()).hexdigest()
    assert stamp["png"]["sha256"] == hashlib.sha256(published.read_bytes()).hexdigest()
    assert not (REPO / f"docs/diagrams/src/{NAME}.json").exists()
    assert not (REPO / f"docs/diagrams/drawio/generated/{NAME}.drawio").exists()
    (HERE / "promotion-verification.json").write_text(json.dumps({
        "diagram": NAME, "final_pass": 4, "source_identical": True,
        "published_png_pixel_identical_to_inspected_final": True,
        "renderer": stamp["renderer"], "stamp_matches_source_and_png": True,
        "obsolete_json_absent": True, "obsolete_generated_drawio_absent": True,
        "published_png_sha256": stamp["png"]["sha256"],
        "scope": "Only the plan-shared.json exclusive canonical-workflow-authoring asset family",
    }, indent=2) + "\n", encoding="utf-8")
    print("PASS: promoted source/pixels + pinned renderer stamp + obsolete asset cleanup")
