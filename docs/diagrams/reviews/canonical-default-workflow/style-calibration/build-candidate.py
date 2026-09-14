"""One non-public calibration candidate; all coordinates derive from the React reference."""
import html
import json
from pathlib import Path
import sys
from urllib.parse import quote
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
PHASE = sys.argv[1] if len(sys.argv) > 1 else "initial"
assert PHASE in ("initial", "confirmation", "strict", "final")
STRICT = PHASE in ("strict", "final")
SCALE = 2 if STRICT else 1.7
WIDTH, HEIGHT = 2140 / SCALE, 2018 / SCALE
INK, MUTED, LINE = "#272320", "#635c57", "#746d68"
PAPER, CARD, BORDER = "#efeae7", "#fdfbf8", "#ece7e3"
GREEN, TEAL = "#0e700e", "#00666d"
font = "Segoe UI"

document = ET.Element("mxfile", host="Electron", version="31.4.5")
diagram = ET.SubElement(document, "diagram", id="style-calibration", name="Style calibration")
model = ET.SubElement(diagram, "mxGraphModel", dx=str(WIDTH), dy=str(HEIGHT),
                      grid="0", page="0", pageScale="1",
                      pageWidth=str(WIDTH), pageHeight=str(HEIGHT),
                      background="#ffffff", math="0", shadow="0")
root = ET.SubElement(model, "root")
ET.SubElement(root, "mxCell", id="0")
ET.SubElement(root, "mxCell", id="1", parent="0")


def vertex(identifier, x, y, w, h, style, value="", parent="1"):
    cell = ET.SubElement(root, "mxCell", id=identifier, value=value,
                         style=style, vertex="1", parent=parent)
    ET.SubElement(cell, "mxGeometry", x=str(x), y=str(y), width=str(w), height=str(h),
                  attrib={"as": "geometry"})
    return cell


def rounded(fill, radius=16, stroke=BORDER, extra=""):
    return (f"rounded=1;absoluteArcSize=1;arcSize={radius * 2};"
            f"fillColor={fill};strokeColor={stroke};strokeWidth=1;shadow=0;{extra}")


vertex("canvas", 0, 0, WIDTH, HEIGHT, rounded(PAPER, 16, "none"))

# The raster reference fixes margins and centers. Card dimensions retain the requested tokens.
nodes = [
    ("Agent", "Agent work", 334, 162, GREEN),
    ("Rai", "RAI gate", 1126, 162, GREEN),
    ("SafetyFailed", "Terminal: safety failed", 334, 734, TEAL),
    ("Human", "Human review", 1126, 734, GREEN),
    ("Declined", "Terminal: declined", 334, 1229, TEAL),
    ("Merge", "Merge", 1126, 1229, GREEN),
    ("Scribe", "Scribe", 334, 1647, GREEN),
    ("Done", "Done", 1126, 1647, TEAL),
]
boxes = {}
for identifier, label, x, y, tone in nodes:
    height = 104 if STRICT else (148 if identifier == "Merge" else 116)
    boxes[identifier] = (x / SCALE, (y + 104) / SCALE - height / 2, 340 if STRICT else 400, height)


def port(identifier, side, fraction=.5):
    x, y, w, h = boxes[identifier]
    return {
        "top": (x + w * fraction, y),
        "bottom": (x + w * fraction, y + h),
        "left": (x, y + h * fraction),
        "right": (x + w, y + h * fraction),
    }[side]


def px(x, y):
    return x / SCALE, y / SCALE


routes = [
    ("e0", "Agent", "Rai", "right", "left", .5, .5, [], "", None),
    ("e1", "Rai", "Agent", "bottom", "bottom", .5, .5,
     [px(1466, 400), px(674, 400)], "revise", px(1290, 400)),
    ("e2", "Rai", "SafetyFailed", "bottom", "top", .5, .5,
     [px(1466, 443), px(674, 443)], "safety-failed", px(1070, 443)),
    ("e3", "Rai", "Scribe", "bottom", "top", .5, .5,
     [px(1466, 660), px(1855, 660), px(1855, 1576), px(674, 1576)],
     "no-changes", px(1660, 660)),
    ("e4", "Rai", "Human", "bottom", "top", .5, .5,
     [], "review", px(1466, 589)),
    ("e5", "Human", "Agent", "left", "bottom", .5, .43,
     [px(1070, 838), px(1070, 516), px(626.4, 516)],
     "request-changes", px(900, 516)),
    ("e6", "Human", "Declined", "bottom", "top", .68, .5,
     [px(1588.4, 1014), px(674, 1014)], "declined", px(1140, 1014)),
    ("e7", "Human", "Merge", "bottom", "top", .68, .5,
     [px(1588.4, 1085), px(1466, 1085)], "approved", px(1588.4, 1085)),
    ("e8", "Merge", "Human", "left", "bottom", .5, .445,
     [px(1070, 1333), px(1070, 1157), px(1428.6, 1157)],
     "blocked", px(1250, 1157)),
    ("e9", "Merge", "Scribe", "bottom", "top", .5, .5,
     [px(1466, 1507), px(674, 1507)], "merged", px(1070, 1507)),
    ("e10", "Scribe", "Done", "right", "left", .5, .5, [], "", None),
]

trace = []
labels = []
for identifier, source, target, exit_side, entry_side, exit_fraction, entry_fraction, bends, label, label_pos in routes:
    coords = {"top": lambda f: (f, 0), "bottom": lambda f: (f, 1),
              "left": lambda f: (0, f), "right": lambda f: (1, f)}
    ex, ey = coords[exit_side](exit_fraction)
    ix, iy = coords[entry_side](entry_fraction)
    style = (f"edgeStyle=none;rounded=1;curved=0;arcSize=10;html=1;"
             f"strokeColor={LINE};strokeWidth=1.8;endArrow=classicThin;endFill=1;endSize=8;"
             f"startArrow=none;exitX={ex};exitY={ey};exitDx=0;exitDy=0;exitPerimeter=0;"
             f"entryX={ix};entryY={iy};entryDx=0;entryDy=0;entryPerimeter=0;"
             "jumpStyle=arc;jumpSize=14;jumpDirection=0;")
    edge = ET.SubElement(root, "mxCell", id=identifier, value="", style=style,
                         edge="1", source=source, target=target, parent="1")
    geometry = ET.SubElement(edge, "mxGeometry", relative="1", attrib={"as": "geometry"})
    if bends:
        array = ET.SubElement(geometry, "Array", attrib={"as": "points"})
        for x, y in bends:
            ET.SubElement(array, "mxPoint", x=str(x), y=str(y))
    points = [port(source, exit_side, exit_fraction), *bends, port(target, entry_side, entry_fraction)]
    assert all(abs(a[0] - b[0]) < .01 or abs(a[1] - b[1]) < .01 for a, b in zip(points, points[1:])), identifier
    trace.append({"id": identifier, "source": source, "target": target, "label": label, "points": points})
    if label:
        labels.append((identifier, label, label_pos))

# Only semantic forks and the common Scribe continuation receive junctions.
for index, (x, y) in enumerate([(1466, 400), (1466, 443), (1466, 660),
                              (1588.4, 1014), (674, 1576)]):
    cx, cy = px(x, y)
    vertex(f"junction-{index}", cx-2.5, cy-2.5, 5, 5,
           f"ellipse;fillColor={LINE};strokeColor=none;")

for identifier, label, _, _, tone in nodes:
    x, y, w, h = boxes[identifier]
    vertex(identifier, x, y, w, h, "group;connectable=1;")
    # Extremely restrained two-layer shadows, not draw.io's dark default shadow.
    vertex(identifier+"-shadow-wide", 0, 3, w, h, rounded("#1c1814", 16, "none", "opacity=2;"), parent=identifier)
    vertex(identifier+"-shadow-near", 0, 1, w, h, rounded("#1c1814", 16, "none", "opacity=3;"), parent=identifier)
    vertex(identifier+"-accent", 0, 0, w, h, rounded(tone, 16, "none"), parent=identifier)
    vertex(identifier+"-surface", 5, 0, w-5, h, rounded(CARD), parent=identifier)
    icon_name = "BotRegular" if tone == GREEN else "WindowRegular"
    svg = (HERE / f"{icon_name}.svg").read_text(encoding="utf-8")
    ET.fromstring(svg)
    icon_size = 28 if STRICT else 38
    vertex(identifier+"-icon", 27, h/2-icon_size/2, icon_size, icon_size,
           "shape=image;aspect=fixed;imageAspect=0;image=data:image/svg+xml," + quote(svg, safe="") + ";",
           parent=identifier)
    subtitle = "Then publish / reuse PR" if identifier == "Merge" and not STRICT else None
    title_size = 20 if STRICT else 28
    title_height = title_size * 1.15
    text_inset = 71 if STRICT else 79
    text_height = 58.2 if subtitle else title_height
    top = h/2 - text_height/2 - (2.5 if STRICT else 4.5)
    title_html = (f'<div style="font-family:Segoe UI;font-size:{title_size}px;font-weight:600;'
                  f'line-height:1.15;color:{INK};white-space:nowrap;">{html.escape(label)}</div>')
    vertex(identifier+"-title", text_inset, top, w-text_inset-18, title_height,
           "text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=top;"
           f"fontFamily={font};fontSize={title_size};spacing=0;overflow=visible;whiteSpace=nowrap;",
           title_html, identifier)
    if subtitle:
        subtitle_html = (f'<div style="font-family:Segoe UI;font-size:20px;font-weight:400;'
                         f'line-height:1.2;color:{MUTED};white-space:nowrap;">{subtitle}</div>')
        vertex(identifier+"-subtitle", 79, top+34.2, w-79-18, 24,
               "text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=top;"
               f"fontFamily={font};fontSize=20;spacing=0;overflow=visible;",
               subtitle_html, identifier)

for identifier, label, (x, y) in labels:
    # Segoe label widths are measured with the same browser text engine before export.
    metrics = json.loads((HERE / "label-metrics.json").read_text())
    label_size = 16 if STRICT else 18
    w = metrics[label] * label_size / 18 + 20
    h = label_size * 1.15 + 12
    value = (f'<div style="font-family:Segoe UI;font-size:{label_size}px;font-weight:600;'
             f'line-height:1.15;color:#3f3935;white-space:nowrap;">{label}</div>')
    vertex(identifier+"-label", x-w/2, y-h/2, w, h,
           rounded(CARD, 6) + f"html=1;fontFamily={font};fontSize={label_size};align=center;"
           "verticalAlign=middle;spacing=0;whiteSpace=nowrap;", value)

if STRICT:
    lookup = {c.get("id"): c for c in root.findall("mxCell")}
    vertex("return-Agent", 674/SCALE-2.5, 400/SCALE-2.5, 5, 5,
           f"ellipse;fillColor={LINE};strokeColor=none;")
    lookup["junction-4"].set("id", "return-Scribe")
    for identifier, target, bends in (
        ("e1", "return-Agent", [px(1466,400)]),
        ("e5", "return-Agent", [px(1070,838),px(1070,516),px(626.4,516),px(626.4,400)]),
        ("e3", "return-Scribe", [px(1466,660),px(1855,660),px(1855,1576)]),
        ("e9", "return-Scribe", [px(1466,1507),px(674,1507)]),
    ):
        edge = lookup[identifier]
        edge.set("target", target)
        style = dict(part.split("=",1) for part in edge.get("style").split(";") if "=" in part)
        style.update(endArrow="none",entryX="0.5",entryY="0.5")
        edge.set("style", ";".join(f"{k}={v}" for k,v in style.items())+";")
        geometry = edge.find("mxGeometry")
        for child in list(geometry):
            geometry.remove(child)
        array = ET.SubElement(geometry,"Array",attrib={"as":"points"})
        for x,y in bends:
            ET.SubElement(array,"mxPoint",x=str(x),y=str(y))
    for target, side in (("Agent","1"),("Scribe","0")):
        edge = ET.SubElement(root,"mxCell",id=f"return-{target}-trunk",value="",edge="1",
                             source=f"return-{target}",target=target,parent="1",
                             style=f"edgeStyle=none;rounded=1;strokeColor={LINE};strokeWidth=1.8;"
                             f"endArrow=classicThin;endFill=1;endSize=8;exitX=0.5;exitY=0.5;"
                             f"exitPerimeter=0;entryX=0.5;entryY={side};entryPerimeter=0;")
        ET.SubElement(edge,"mxGeometry",relative="1",attrib={"as":"geometry"})
output = HERE / ("candidate-final.drawio" if PHASE == "final" else ("candidate-strict.drawio" if STRICT else ("candidate-initial.drawio" if PHASE == "initial" else "candidate.drawio")))
assert not output.exists(), f"Preserve prior artifacts: {output}"
ET.indent(document, space="  ")
ET.ElementTree(document).write(output, encoding="utf-8", xml_declaration=True)
(HERE / f"{PHASE}-geometry.json").write_text(json.dumps({
    "scale": SCALE, "canvas": [WIDTH, HEIGHT], "cards": boxes,
    "arrow_trace": trace, "phase_collapse": ("Legacy copy retained for visual calibration only; not a runtime publication."
                                             if STRICT else "Merge includes the explicit sequential PR-publication action."),
}, indent=2) + "\n")
print(output)
