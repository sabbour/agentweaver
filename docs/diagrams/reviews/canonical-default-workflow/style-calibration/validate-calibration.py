"""Validate this one calibration and the public-asset freeze, without rendering."""
import hashlib
from html import unescape
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET
from PIL import Image

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
provenance = json.loads((HERE / "reference-provenance.json").read_text())
original_bytes = subprocess.check_output([
    "git", "show", f"{provenance['git_revision']}:docs/diagrams/canonical-default-workflow.png",
], cwd=ROOT)
assert original_bytes == (HERE / "original-react.png").read_bytes()
tree = ET.parse(HERE / "candidate.drawio")
assert len(tree.findall("diagram")) == 1
assert tree.find("diagram/mxGraphModel/root") is not None
cells = {c.get("id"): c for c in tree.iter("mxCell")}
geometry = json.loads((HERE / "confirmation-geometry.json").read_text())
for name, (x, y, w, h) in geometry["cards"].items():
    assert w == 400 and h == (148 if name == "Merge" else 116)
    g = cells[name].find("mxGeometry")
    assert float(g.get("width")) == w and float(g.get("height")) == h
    icon = cells[name + "-icon"].find("mxGeometry")
    assert float(icon.get("width")) == 38 and float(icon.get("height")) == 38
    title = cells[name + "-title"].get("value")
    assert "font-size:28px" in title and "font-weight:600" in title and "line-height:1.15" in title
    assert "Segoe UI" in title
    assert float(cells[name + "-surface"].find("mxGeometry").get("x")) == 5
    assert "arcSize=32" in cells[name + "-surface"].get("style")
for cell in cells.values():
    text = unescape(re.sub("<[^>]*>", "", cell.get("value", "")))
    assert "native-libraries:" not in text
    assert len(text) < 50, text
edges = {c.get("id"): c for c in tree.iter("mxCell") if c.get("edge") == "1"}
assert len(edges) == 11
for trace in geometry["arrow_trace"]:
    edge = edges[trace["id"]]
    assert edge.get("source") == trace["source"] and edge.get("target") == trace["target"]
    points = trace["points"]
    assert all(abs(a[0]-b[0]) < .01 or abs(a[1]-b[1]) < .01 for a,b in zip(points, points[1:]))
original = Image.open(HERE / "original-react.png").convert("RGB")
candidate = Image.open(HERE / "candidate.png").convert("RGB")
comparison = Image.open(HERE / "comparison.png").convert("RGB")
assert original.size == candidate.size == (2140, 2018)
assert comparison.size == (4280, 2018)
assert comparison.crop((0, 0, 2140, 2018)).tobytes() == original.tobytes()
assert comparison.crop((2140, 0, 4280, 2018)).tobytes() == candidate.tobytes()
pause = json.loads((HERE.parent.parent / "canonical-provider-admission/visual-pause.json").read_text())
for item in pause["files"]:
    assert hashlib.sha256(Path(item["path"]).read_bytes()).hexdigest() == item["sha256"], item["path"]
report = {
    "status": "mechanical-checks-passed-not-visual-approval",
    "reference_exact_git_blob": provenance["git_revision"],
    "candidate_rendered_dimensions": list(candidate.size),
    "comparison_dimensions": list(comparison.size),
    "comparison_panels_resized": False,
    "editable_uncompressed_drawio": True,
    "card_tokens": "400x116; compound Merge/PR phase 400x148; radius16; accent5; Fluent icons38",
    "typography": "Segoe UI 28/600/1.15; short subtitle20/1.2; edge labels18/600/1.15",
    "external_arrows_checked": len(edges),
    "phase_collapse": geometry["phase_collapse"],
    "public_asset_hashes_unchanged": len(pause["files"]),
    "renderer_product_version_observed": "31.4.5.0",
    "publication_a5_gate": "Not claimed: isolated original-aspect style-calibration artboard only.",
    "files": {name: hashlib.sha256((HERE / name).read_bytes()).hexdigest()
              for name in ("original-react.png", "candidate.drawio", "candidate.png", "comparison.png")},
}
(HERE / "validation.json").write_text(json.dumps(report, indent=2) + "\n")
print(json.dumps(report, indent=2))
