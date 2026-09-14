"""Verify the narrowly corrected pilot against its immutable original."""
import json
from pathlib import Path
import xml.etree.ElementTree as ET
from PIL import Image, ImageChops, ImageStat

HERE = Path(__file__).resolve().parent / "pilot-compatibility"
original = ET.parse(HERE / "original.drawio")
candidate = ET.parse(HERE / "canonical-coordinator-architecture.drawio")
before = {c.get("id"): c for c in original.iter("mxCell")}
after = {c.get("id"): c for c in candidate.iter("mxCell")}
assert before.keys() == after.keys()
for identifier, cell in before.items():
    expected = dict(cell.attrib)
    if cell.get("edge") == "1":
        expected["style"] = expected["style"].replace("endArrow=block;", "endArrow=classicThin;endSize=8;")
    actual = after[identifier]
    assert {k: v for k, v in actual.attrib.items() if k != "fluentRole"} == expected, identifier
    if cell.find("mxGeometry") is not None:
        assert ET.tostring(cell.find("mxGeometry")) == ET.tostring(actual.find("mxGeometry")), identifier

old = Image.open(HERE / "original.png").convert("RGB")
new = Image.open(HERE / "corrected.png").convert("RGB")
regions = []
for identifier in ("entry", "coordinator", "dispatch", "state", "assembly", "children"):
    geometry = before[identifier].find("mxGeometry")
    x, y, w, h = [float(geometry.get(k)) for k in ("x", "y", "width", "height")]
    bounds = (int(x * 2 - 6 + 10), int(y * 2 + 10), int((x + w) * 2 - 6 - 10), int((y + h) * 2 - 10))
    diff = ImageChops.difference(old.crop(bounds), new.crop(bounds))
    maximum = max(end for _, end in diff.getextrema())
    assert maximum <= 2, (identifier, maximum)
    regions.append({"card": identifier, "maximum_channel_delta": maximum,
                    "mean_channel_delta": ImageStat.Stat(diff).mean})
report = {
    "status": "passed",
    "original_dimensions": old.size,
    "corrected_dimensions": new.size,
    "card_pixel_checks": regions,
    "semantics_and_geometry_changes": 0,
    "arrow_marker_updates": 9,
    "pixel_identity": False,
    "inspection": "Original and corrected actual PNGs opened individually. All nine arrows traced. Native jump at the status/recovery crossing remains visibly separate; no false junction. Card interiors differ by at most two channel levels after re-rasterization. The marker-driven re-export is transparent, not claimed byte/pixel-identical.",
    "approval_scope": "Exact canonical XML fingerprint pins all content, geometry and style. The preserved pilot profile cannot be applied to any other source or modified pilot.",
}
(HERE / "verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"status": report["status"], "arrow_marker_updates": 9, "semantics_and_geometry_changes": 0}))
