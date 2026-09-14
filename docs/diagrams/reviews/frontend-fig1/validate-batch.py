import hashlib
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import jsonschema
from PIL import Image

ROOT = Path(__file__).resolve().parents[4]
NAMES = [
    "frontend-fig1", "frontend-fig2", "frontend-fig3", "frontend-fig6",
    "frontend-fig7", "mcp-server-fig2", "project-generation-model-settings-fig1",
    "project-skills-fig1", "projects-fig1", "projects-fig2",
    "repo-blueprint-suggestions-fig1",
]
SKILL = ROOT / ".github/skills/docs-diagram-iterate"
SCHEMA = json.loads((SKILL / "references/iteration-manifest.schema.json").read_text(encoding="utf-8"))
PLAN = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json").read_text(encoding="utf-8"))
ALLOWED = PLAN["exclusive_asset_paths"]


def owned(file):
    relative = file.relative_to(ROOT).as_posix()
    assert any(relative == p or (p.endswith("/") and relative.startswith(p)) for p in ALLOWED), relative
    return file


def digest(file):
    return hashlib.sha256(file.read_bytes()).hexdigest()


reports = []
for name in NAMES:
    review = owned(ROOT / "docs/diagrams/reviews" / name / "validation.json").parent
    model = json.loads((review / "content-model.json").read_text(encoding="utf-8"))
    manifest_path = review / "iteration-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    jsonschema.validate(manifest, SCHEMA)
    validation = subprocess.check_output(
        [sys.executable, "-B", str(SKILL / "scripts/validate_iteration_manifest.py"), str(manifest_path)],
        text=True,
    ).strip()
    artifacts = [manifest["pitch"], *manifest["passes"]]
    for artifact in artifacts:
        for key in ("drawio", "png", "change_record"):
            assert owned(review / artifact[key]).is_file(), artifact[key]
        record = review / artifact["change_record"]
        text = record.read_text(encoding="utf-8")
        wrong = f"docs/deep-dive/{name}.md"
        target = f"docs/deep-dive/{re.sub(r'-fig\d+$', '', name)}.md"
        if "--repair-review-links" in sys.argv and wrong in text:
            text = text.replace(wrong, target)
            record.write_text(text, encoding="utf-8")
        assert target in text and (ROOT / target).is_file()
        assert artifact["png_inspected_print"] and artifact["png_inspected_enlarged"]
        png = review / artifact["png"]
        with Image.open(png) as image:
            assert image.width >= 1500 and image.height >= (1000 if "number" in artifact else 700)
            image.verify()
        with Image.open(png.with_name(png.stem + "-print.png")) as image:
            assert image.size == (794, 559)
            image.verify()
        graph = ET.parse(review / artifact["drawio"]).getroot()
        assert graph.tag == "mxfile" and graph.get("compressed") == "false"
        assert len(graph.findall("diagram")) == 1
        page = graph.find("diagram/mxGraphModel")
        assert page is not None and page.get("pageWidth") == "827" and page.get("pageHeight") == "583"
        assert page.get("pageScale") == "1" and page.get("background") == "#efeae7"
        cells = {c.get("id"): c for c in page.findall("root/mxCell")}
        assert len(cells) == len(page.findall("root/mxCell"))

        def box(cell):
            geometry = cell.find("mxGeometry")
            x, y, w, h = (float(geometry.get(k, "0")) for k in ("x", "y", "width", "height"))
            parent = cells.get(cell.get("parent"))
            if parent is not None and parent.get("vertex") == "1":
                px, py, _, _ = box(parent)
                x, y = x + px, y + py
            return x, y, w, h

        for cell in cells.values():
            if cell.get("vertex") != "1":
                continue
            x, y, w, h = box(cell)
            assert 0 <= x <= x + w <= 827 and 0 <= y <= y + h <= 583, (name, cell.get("id"), (x, y, w, h))
            if cell.get("id").endswith("-accent"):
                assert w == 5
        for point in page.findall(".//Array[@as='points']/mxPoint"):
            assert 0 <= float(point.get("x")) <= 827 and 0 <= float(point.get("y")) <= 583
    final = manifest["passes"][-1]
    edges = {k: v for k, v in cells.items() if v.get("edge") == "1"}
    trace = {t["id"]: t for t in final["arrow_trace"]}
    expected = {e["id"]: e for e in model["edges"]}
    assert len(trace) == len(final["arrow_trace"])
    assert edges.keys() == trace.keys() == expected.keys()
    for key, cell in edges.items():
        for field, attribute in (("source", "source"), ("target", "target"), ("relationship", "value")):
            assert trace[key][field] == cell.get(attribute)
        assert expected[key]["label"] == cell.get("value")
        assert trace[key]["evidence"] == expected[key]["evidence"]
        assert trace[key]["result"] == "clean"
        style = cell.get("style")
        assert "edgeStyle=orthogonalEdgeStyle;" in style and "endArrow=block;" in style
        assert "jumpStyle=arc;" in style and "dashed=1" not in style
        assert cell.get("source") in cells and cell.get("target") in cells
    for node in model["nodes"]:
        icon = cells[node["id"] + "-icon"]
        assert f"shape={node['shape']};" in icon.get("style")
        card = cells[node["id"]]
        assert "shadow=1;" in card.get("style") and "fillColor=#fdfbf8;" in card.get("style")
        for key in ("title", "subtitle", "detail", "meta", "pill"):
            assert cells[node["id"] + "-" + key].get("value")
    growth = json.loads(subprocess.check_output([
        sys.executable, "-B", str(SKILL / "scripts/check_xml_growth.py"),
        str(review / manifest["pitch"]["drawio"]), str(review / manifest["passes"][0]["drawio"]), "--json",
    ], text=True))
    assert growth["passed"] and growth["growth_ratio"] >= 9
    assert growth["baseline_meaningful_xml"] == manifest["passes"][0]["baseline_meaningful_xml"]
    assert growth["result_meaningful_xml"] == manifest["passes"][0]["result_meaningful_xml"]
    report = {
        "diagram": name,
        "manifest": manifest_path.relative_to(ROOT).as_posix(),
        "schema": "valid",
        "repository_manifest_validator": validation,
        "pitch_plus_passes": len(artifacts),
        "actual_open_inspections_recorded": len(artifacts) * 2,
        "one_uncompressed_A5_page_all_passes": True,
        "bounds_and_5_unit_accents": "valid",
        "native_symbols_and_card_hierarchy": "valid",
        "exact_arrow_ids_source_target_label_evidence": len(edges),
        "growth_ratio": growth["growth_ratio"],
        "final_source_sha256": digest(review / final["drawio"]),
        "inspected_final_png_sha256": digest(review / final["png"]),
        "publication": "not checked" if "--published" not in sys.argv else "valid",
    }
    if "--published" in sys.argv:
        source = owned(ROOT / f"docs/diagrams/src/{name}.drawio")
        generated = owned(ROOT / f"docs/diagrams/drawio/generated/{name}.drawio")
        png = owned(ROOT / f"docs/diagrams/{name}.png")
        stamp = json.loads(owned(ROOT / f"docs/diagrams/{name}.hash.txt").read_text(encoding="utf-8"))
        assert not (ROOT / f"docs/diagrams/src/{name}.json").exists()
        assert source.read_bytes() == generated.read_bytes() == (review / final["drawio"]).read_bytes()
        assert png.read_bytes() == (review / final["png"]).read_bytes()
        assert stamp["source"]["sha256"] == hashlib.sha256(source.read_text(encoding="utf-8").replace("\r\n", "\n").strip().encode()).hexdigest()
        assert stamp["drawio"]["sha256"] == digest(generated)
        assert stamp["png"]["sha256"] == digest(png)
        assert stamp["renderer"]["rendererVersion"] == "31.4.5"
        assert stamp["renderer"]["scale"] == 2 and stamp["renderer"]["border"] == 16
    owned(review / "validation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    reports.append(report)
    print(f"{name}: schema, manifest, A5, bounds, icons, {len(edges)} arrows, {growth['growth_ratio']:.5f}x growth: PASS")

owned(ROOT / "docs/diagrams/reviews/frontend-fig1/batch-validation.json").write_text(
    json.dumps({"diagrams": reports, "diagram_count": len(reports), "arrow_count": sum(r["exact_arrow_ids_source_target_label_evidence"] for r in reports)}, indent=2) + "\n",
    encoding="utf-8",
)
