"""Author the owned API-arbitration pitch without changing published assets."""

import copy
from pathlib import Path
from xml.etree import ElementTree as ET

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
NAME = "review-merge-fig5"
tree = ET.parse(REPO / "docs" / "diagrams" / "drawio" / "fluent-template.drawio")
outer = copy.deepcopy(tree.getroot())
outer.set("agent", "GPT-6 Astra grounded API arbitration")
diagram = outer.find("diagram")
diagram.set("id", NAME)
diagram.set("name", "Review API arbitration")
model = diagram.find("mxGraphModel")
model.set("pageWidth", "559")
model.set("pageHeight", "794")
model.set("pageScale", "1")
model.set("dx", "559")
model.set("dy", "794")
root = model.find("root")
root.clear()
ET.SubElement(root, "mxCell", id="0")
ET.SubElement(root, "mxCell", id="1", parent="0")


def vertex(identifier, value, style, x, y, width, height, parent="1"):
    item = ET.SubElement(root, "mxCell", id=identifier, value=value, style=style, vertex="1", parent=parent)
    ET.SubElement(item, "mxGeometry", x=str(x), y=str(y), width=str(width), height=str(height), attrib={"as": "geometry"})
    return item


def label(identifier, value, x, y, width, height, size=12, color="#272320", bold=False):
    vertex(identifier, value,
           f"text;html=0;whiteSpace=wrap;fillColor=none;strokeColor=none;align=left;verticalAlign=middle;"
           f"fontFamily=Segoe UI;fontSize={size};fontColor={color};fontStyle={1 if bold else 0};",
           x, y, width, height)


label("title", "Review API arbitration", 20, 16, 519, 30, 25, bold=True)
label("subtitle", "Standalone review: delivery and merge use different guards.", 20, 47, 519, 24, 13, "#635c57")
participants = [
    ("caller", "Caller", "Human decision", "ACTOR", "#e7e1dc", "#635c57", "umlActor", 20),
    ("api", "Review API", "Access + route", "API", "#d2ccf8", "#3f3682", "process", 155),
    ("state", "Run state", "Pending + CAS", "DATA", "#a6e9ed", "#00666d", "cylinder", 290),
    ("runtime", "Execution", "Workflow / merge", "RUN", "#9fd89f", "#0e700e", "process", 425),
]
centers = {}
for identifier, title, subtitle, badge, bg, fg, shape, x in participants:
    centers[identifier] = x + 57
    vertex(identifier, "", "rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#fdfbf8;strokeColor=#ece7e3;shadow=1;",
           x, 80, 114, 87)
    vertex(identifier + "-accent", "", f"rounded=1;arcSize=100;fillColor={fg};strokeColor=none;", 0, 0, 5, 87, identifier)
    vertex(identifier + "-icon", "", f"shape={shape};fillColor={bg};strokeColor={fg};strokeWidth=1.5;", 11, 10, 20, 20, identifier)
    vertex(identifier + "-badge", badge,
           f"rounded=1;arcSize=100;fillColor={bg};strokeColor=none;fontFamily=Segoe UI;fontSize=9;fontColor={fg};fontStyle=1;",
           62, 9, 42, 19, identifier)
    label(identifier + "-title", title, x + 11, 114, 97, 22, 14, bold=True)
    label(identifier + "-sub", subtitle, x + 11, 140, 99, 17, 10, "#635c57")
    # A dashed native UML lifeline is a vertex, not an invented message arrow.
    vertex(identifier + "-lifeline", "", "shape=line;direction=south;dashed=1;dashPattern=3 3;strokeColor=#b3aaa3;strokeWidth=1;",
           x + 56.5, 174, 1, 434)


def message(identifier, source, target, value, y, dashed=False):
    style = ("edgeStyle=none;html=0;rounded=0;endArrow=open;endFill=0;strokeColor=#746d68;strokeWidth=1.5;"
             "fontFamily=Segoe UI;fontSize=10;fontColor=#3f3935;labelBackgroundColor=#fdfbf8;"
             + ("dashed=1;dashPattern=4 3;" if dashed else ""))
    item = ET.SubElement(root, "mxCell", id=identifier, value=value, style=style, edge="1", parent="1")
    geometry = ET.SubElement(item, "mxGeometry", relative="1", y="9" if centers[source] < centers[target] else "-9", attrib={"as": "geometry"})
    ET.SubElement(geometry, "mxPoint", x=str(centers[source]), y=str(y), attrib={"as": "sourcePoint"})
    ET.SubElement(geometry, "mxPoint", x=str(centers[target]), y=str(y), attrib={"as": "targetPoint"})


def fragment(identifier, title, y, height):
    boundary = vertex(identifier, "", "rounded=1;arcSize=8;fillColor=#f8f4f1;strokeColor=#e2ddd9;strokeWidth=1;",
                      143, y, 396, height)
    root.remove(boundary)
    root.insert(2, boundary)
    label(identifier + "-title", title, 153, y + 4, 376, 17, 11, "#3f3935", True)


message("request", "caller", "api", "decision", 192)
message("read", "api", "state", "load run", 218)
message("state-result", "state", "api", "status + pending", 244, True)
label("access-note", "Project contributor access; projectless pending owner defense is additional.", 20, 261, 519, 28, 11, "#635c57")
fragment("deferred", "ALT A  No local workflow + durable pending request", 298, 77)
message("persist", "api", "state", "persist decision first", 340)
label("deferred-note", "Then request-changes / decline transition; approval is not merge CAS.", 156, 350, 369, 21, 10, "#635c57")
fragment("live", "ALT B  Live workflow + pending request", 387, 103)
message("cas", "api", "state", "changes / decline: CAS", 427)
message("consume", "api", "state", "consume pending", 454)
message("respond", "api", "runtime", "send workflow response", 480)
label("approval-note", "Approval consumes pending but performs no HTTP merge CAS.", 20, 502, 519, 20, 11, "#635c57")
fragment("merge", "LATER  Only when execution reaches merge", 533, 90)
label("lock-note", "Merge coordinator acquires the repository lock first.", 156, 556, 369, 18, 10, "#635c57")
message("merge-cas", "runtime", "state", "merge CAS", 590)
label("merge-note", "Then guarded Git operation; lock released on exit.", 156, 599, 369, 18, 10, "#635c57")
vertex("notes", "", "rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;",
       20, 628, 519, 143)
label("notes-title", "Replay and fallback are not the live path", 34, 638, 490, 23, 15, bold=True)
label("replay", "Matching terminal replay may return the existing result; other non-review states conflict.", 34, 667, 490, 29, 11, "#635c57")
label("missing", "Live workflow without pending: 409. No live workflow or pending: direct fallback.", 34, 702, 490, 29, 11, "#635c57")
label("fallback", "Direct approval validates artifacts and tree. Direct request-changes returns 409.", 34, 737, 490, 27, 11, "#635c57")

path = HERE / f"{NAME}-pitch-corrected.drawio"
if path.exists():
    raise SystemExit(f"Refusing to overwrite saved pitch: {path}")
ET.indent(outer, space="  ")
ET.ElementTree(outer).write(path, encoding="utf-8", xml_declaration=True)
print(path)
