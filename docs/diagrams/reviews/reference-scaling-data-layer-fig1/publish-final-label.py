"""Publish pass five only after its enlarged and A5 PNG inspections."""
import copy
import json
import shutil
from pathlib import Path
from xml.etree import ElementTree as ET

folder = Path(__file__).resolve().parent
root = folder.parents[3]
name = "reference-scaling-data-layer-fig1"
manifest_path = folder / "iteration-manifest.json"
manifest = json.loads(manifest_path.read_text())
assert manifest["final_pass"] == 4
previous = folder / f"{name}-pass-04.drawio"
current = folder / f"{name}-pass-05.drawio"
before, after = ET.parse(previous), ET.parse(current)
geometry = after.find(".//mxCell[@id='temp-ref']/mxGeometry")
offset = geometry.find("mxPoint[@as='offset']")
assert offset.attrib == {"x": "0", "y": "25", "as": "offset"}
geometry.remove(offset)
offset.tail = None
assert ET.tostring(before.getroot()).replace(b" ", b"").replace(b"\n", b"") == ET.tostring(after.getroot()).replace(b" ", b"").replace(b"\n", b"")
latest = copy.deepcopy(manifest["passes"][-1])
latest.update(number=5, drawio=f"{name}-pass-05.drawio", png=f"{name}-pass-05.png",
              change_record=f"{name}-pass-05.md", overlap_defects=0)
for arrow in latest["arrow_trace"]:
    if arrow["id"] == "temp-ref":
        arrow["result"] = "corrected"
manifest["passes"][-1]["overlap_defects"] = 1
manifest["passes"].append(latest)
manifest["final_pass"] = 5
record = """# Scaling: pass 05 final correction

Final enlarged inspection exposed the temporary-ref label's proximity to the
unrelated fetch elbow. Moved only that label down 25 logical pixels. No node,
edge, port, relationship, text, color or other geometry changed; the publisher
asserts XML equivalence after removing that one label-offset point.

Opened the official Desktop 31.4.5 PNG enlarged and the separate A5 print PNG.
The fetch elbow is unobstructed; the temporary-ref label now sits on its own
lower return segment, above the arrow into Azure Files. It clears both the
adjacent heading and body text. Orientation, overlap and arrow defects: zero.
Earlier source/PNG passes remain unchanged.

## Final arrow trace

Retraced every connector in the final image. The unheaded shared-access bus
joins both callers and fans out to both persistent services. A2A is bidirectional;
execution is one-way into pod-local scratch. Fetch runs from shared Files to
the local checkout; temporary-ref publication returns to Files, never GitHub.
There is no sandbox-to-database edge, unintended junction, crossing, or
semantic revision-loop rail.

| ID | Source to target | Relationship and evidence | Result |
| --- | --- | --- | --- |
"""
for arrow in latest["arrow_trace"]:
    record += f"| `{arrow['id']}` | `{arrow['source']}` to `{arrow['target']}` | {arrow['relationship']}; `{arrow['evidence']}` | {arrow['result']} |\n"
(folder / latest["change_record"]).write_text(record, encoding="utf-8", newline="\n")
prior_record = folder / f"{name}-pass-04.md"
with prior_record.open("a", encoding="utf-8", newline="\n") as handle:
    handle.write("\n## Final-inspection addendum\n\nThe later enlarged publication inspection found one label-placement overlap risk: "
                 "`temp ref` sat alongside the unrelated fetch elbow. This supersedes the earlier zero-overlap "
                 "assessment. Pass five corrects the offset; this pass's source and PNG remain unchanged.\n")
manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
shutil.copyfile(current, root / "docs/diagrams/src" / f"{name}.drawio")
shutil.copyfile(folder / latest["png"], root / "docs/diagrams" / f"{name}.png")
publication_path = root / "docs/diagrams/reviews/reference-a2a-fig1/publication.json"
publication = json.loads(publication_path.read_text())
for item in publication["redesigned"]:
    item["final_pass"] = 5 if item["name"] == name else 4
publication_path.write_text(json.dumps(publication, indent=2) + "\n", encoding="utf-8", newline="\n")
print("Published scaling pass five; earlier source/PNG passes preserved.")
