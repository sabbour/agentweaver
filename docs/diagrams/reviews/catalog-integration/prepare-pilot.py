"""Prepare the user-authorized narrow pilot correction without public writes."""
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
DEST = HERE / "pilot-compatibility"
DEST.mkdir(exist_ok=True)
name = "canonical-coordinator-architecture"
source = ROOT / f"docs/diagrams/src/{name}.drawio"
for extension, original in (
    ("drawio", source),
    ("png", ROOT / f"docs/diagrams/{name}.png"),
    ("hash.txt", ROOT / f"docs/diagrams/{name}.hash.txt"),
):
    target = DEST / f"original.{extension}"
    if not target.exists():
        shutil.copyfile(original, target)

tree = ET.parse(DEST / "original.drawio")
tree.getroot().set("approvedSnapshot", "coordinator-pilot-preserved-v1")
model = tree.find("diagram/mxGraphModel")
model.set("pageWidth", "794")
model.set("pageHeight", "559")
model.set("pageScale", "1.05")
trace = []
bindings = {}
for cell in tree.iter("mxCell"):
    identifier = cell.get("id")
    if identifier in ("0", "1"):
        continue
    if cell.get("edge") == "1":
        role = "connector"
        cell.set("style", cell.get("style").replace("endArrow=block;", "endArrow=classicThin;endSize=8;"))
        trace.append({"id": identifier, "source": cell.get("source"), "target": cell.get("target"), "label": cell.get("value")})
    elif identifier in ("entry", "coordinator", "dispatch", "state", "assembly", "children"):
        role = "preserved-component"
    elif identifier.endswith(("-icon", "-symbol")):
        role = "preserved-native-symbol"
    elif identifier.endswith("-accent"):
        role = "accent"
    elif identifier.endswith("-title") or identifier == "title":
        role = "preserved-title"
    elif cell.get("value"):
        role = "preserved-label"
    else:
        role = "preserved-surface"
    cell.set("fluentRole", role)
    bindings[identifier] = role
candidate = DEST / f"{name}.drawio"
tree.write(candidate, encoding="utf-8", xml_declaration=True)
record = {
    "diagram": name,
    "canonical_xml_sha256": hashlib.sha256(ET.canonicalize(candidate.read_text(encoding="utf-8"), strip_text=True).encode("utf-8")).hexdigest(),
    "source_sha256": hashlib.sha256(candidate.read_bytes()).hexdigest(),
    "original_png_sha256": hashlib.sha256((DEST / "original.png").read_bytes()).hexdigest(),
    "scope": "User-authorized preservation of this approved pilot only; exact canonical XML fingerprint pins all copy, topology, geometry and style. No other diagram may use this profile.",
    "changes": ["A5 print metadata normalized without moving visible content", "Explicit per-cell role annotations", "Nine block markers changed to the calibrated classicThin size 8; all paths, card geometry and copy retained. See verification.json for measured raster differences."],
    "bindings": bindings,
    "arrow_trace": trace,
}
(DEST / "prepared-profile.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
print(candidate)
