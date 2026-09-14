"""Read-only validation of the plan's published assets; write one owned result artifact."""
import hashlib
import importlib.util
import json
import sys
from pathlib import Path
from xml.etree import ElementTree as ET
import jsonschema
from PIL import Image

here = Path(__file__).resolve().parent
repo = here.parents[3]
plan = json.loads((repo / ".github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json").read_text())
skill = repo / ".github/skills/docs-diagram-iterate"
schema = json.loads((skill / "references/iteration-manifest.schema.json").read_text())
def module(name, file):
    spec = importlib.util.spec_from_file_location(name, file)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result
growth = module("growth", skill / "scripts/check_xml_growth.py")
manifest_validator = module("manifest_validator", skill / "scripts/validate_iteration_manifest.py")
retired = {"00-system-overview-fig7", "00-system-overview-fig8", "api-core-fig7"}
results = []
errors = []
def digest(file):
    return hashlib.sha256(file.read_bytes()).hexdigest()
def artifact(base, value):
    relative = Path(value.replace("\\", "/"))
    candidates = [base / relative, repo / relative]
    found = [item for item in candidates if item.is_file()]
    if not found:
        raise ValueError(f"missing artifact {value}")
    return found[0]

for name in plan["diagram_names"]:
    if name in retired:
        continue
    try:
        base = repo / "docs/diagrams/reviews" / name
        manifest = json.loads((base / "iteration-manifest.json").read_text(encoding="utf-8-sig"))
        jsonschema.Draft202012Validator(schema).validate(manifest)
        problems = manifest_validator.validate_manifest(manifest)
        if problems:
            raise ValueError("; ".join(problems))
        pitch = manifest["pitch"]
        first = manifest["passes"][0]
        assessment = growth.assess(
            artifact(base, pitch["drawio"]).read_text(encoding="utf-8-sig"),
            artifact(base, first["drawio"]).read_text(encoding="utf-8-sig"))
        if not assessment["passed"]:
            raise ValueError(f"growth failed: {assessment}")
        if abs(assessment["growth_ratio"] - first["growth_ratio"]) > 0.0001:
            raise ValueError("manifest growth differs from actual XML")
        for entry in [pitch, *manifest["passes"]]:
            drawio = artifact(base, entry["drawio"])
            png = artifact(base, entry["png"])
            artifact(base, entry["change_record"])
            with Image.open(png) as image:
                image.verify()
            raw = drawio.read_text(encoding="utf-8-sig")
            analysis = growth.analyze(raw)
            if any(analysis[k] for k in ("invisible_cells", "off_page_cells", "duplicate_cells", "metadata_padding")):
                raise ValueError(f"invalid visible XML: {drawio.name}")
            dimensions = sorted([analysis["page_width"], analysis["page_height"]])
            if abs(dimensions[0] - 583) > 1 or abs(dimensions[1] - 827) > 1:
                raise ValueError(f"not A5: {dimensions}")
        final = manifest["passes"][-1]
        final_xml = artifact(base, final["drawio"])
        cells = {c.get("id"): c for c in ET.parse(final_xml).iter("mxCell")}
        edges = {key for key, cell in cells.items() if cell.get("edge") == "1"}
        traced = {entry["id"] for entry in final["arrow_trace"]}
        if len(traced) != len(final["arrow_trace"]):
            raise ValueError("duplicate arrow trace IDs")
        if not traced.issubset(edges):
            raise ValueError(f"trace missing XML edges: {traced - edges}")
        untraced = []
        for edge_id in edges - traced:
            style = growth.parse_style(cells[edge_id].get("style", ""))
            if style.get("endArrow", "classic") != "none" or style.get("startArrow", "none") != "none":
                untraced.append(edge_id)
        if untraced:
            raise ValueError(f"untraced arrows: {untraced}")
        endpoint_mappings = []
        for entry in final["arrow_trace"]:
            edge = cells[entry["id"]]
            for endpoint in ("source", "target"):
                actual = edge.get(endpoint)
                expected = entry[endpoint]
                if actual == expected:
                    continue
                if actual not in cells or expected not in cells:
                    raise ValueError(f"{entry['id']} {endpoint}: unresolved {actual} -> {expected}")
                geometry = growth.geometry_for(cells[actual])
                expected_geometry = growth.geometry_for(cells[expected])
                if geometry is None or expected_geometry is None:
                    raise ValueError(f"{entry['id']} {endpoint}: missing endpoint geometry")
                actual_x = float(geometry.get("x", "0")) + float(geometry.get("width", "0")) / 2
                expected_x = float(expected_geometry.get("x", "0")) + float(expected_geometry.get("width", "0")) / 2
                if abs(actual_x - expected_x) > 1:
                    raise ValueError(f"{entry['id']} {endpoint}: activation does not align with participant")
                endpoint_mappings.append({"edge": entry["id"], "endpoint": endpoint,
                                          "activation": actual, "participant": expected})
        canonical = repo / f"docs/diagrams/src/{name}.drawio"
        published = repo / f"docs/diagrams/{name}.png"
        if digest(canonical) != digest(final_xml):
            raise ValueError("canonical source differs from final inspected pass")
        if digest(published) != digest(artifact(base, final["png"])):
            raise ValueError("canonical PNG differs from final inspected pass")
        if (repo / f"docs/diagrams/src/{name}.json").exists():
            raise ValueError("ambiguous JSON plus drawio source")
        results.append({"diagram": name, "passes": len(manifest["passes"]), "arrows": len(traced),
                        "growth_ratio": assessment["growth_ratio"], "schema": "valid",
                        "xml_a5_artifacts": "valid", "publication_matches_final": True,
                        "sequence_endpoint_mappings": endpoint_mappings})
    except (OSError, ValueError, ET.ParseError, jsonschema.ValidationError) as error:
        errors.append({"diagram": name, "error": str(error)})

for name in retired:
    for extension in ("json", "drawio"):
        if (repo / f"docs/diagrams/src/{name}.{extension}").exists():
            errors.append({"diagram": name, "error": "retired canonical source remains"})
    if (repo / f"docs/diagrams/{name}.png").exists():
        errors.append({"diagram": name, "error": "retired publication remains"})
for document in plan["document_paths"]:
    content = (repo / document).read_text(encoding="utf-8")
    for name in retired:
        if f"../diagrams/{name}.png" in content:
            errors.append({"document": document, "error": f"retired image consumer {name}"})

report = {"diagrams": results, "retired": sorted(retired), "errors": errors,
          "published_count": len(results), "post_pitch_pass_count": sum(r["passes"] for r in results),
          "arrow_count": sum(r["arrows"] for r in results)}
(here / "shard-validation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(json.dumps({k: v for k, v in report.items() if k != "diagrams"}, indent=2))
sys.exit(bool(errors))
