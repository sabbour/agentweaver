"""Bounded review-artifact authoring; never writes a canonical or shared source."""

import argparse
import copy
from pathlib import Path
from xml.etree import ElementTree as ET

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
NAME = "coordinator-internals-fig4"
TOKENS = {
    "neutral": ("#e7e1dc", "#635c57"),
    "lavender": ("#d2ccf8", "#3f3682"),
    "teal": ("#a6e9ed", "#00666d"),
    "green": ("#9fd89f", "#0e700e"),
    "marigold": ("#f9e2ae", "#835b00"),
}
NODES = [
    ("eligible", "Eligible children", "Dependency-ordered branches", "No partial failed plan", "INPUT", "teal", "process", 24, 112),
    ("integrate", "Build integration", "Persist aggregate tree + diff", "Conflicts may auto-resolve", "GIT", "lavender", "process", 292, 112),
    ("gates", "Authored gates", "Run applicable checks in order", "GREEN / YELLOW continue", "CHECK", "lavender", "rhombus", 560, 112),
    ("complete", "Complete + Scribe", "Merged result remains merged", "Scribe failure is nonfatal", "RESULT", "green", "ellipse", 24, 280),
    ("merge", "Guarded merge", "Integration into origin branch", "Conflict is not success", "MERGE", "green", "process", 292, 280),
    ("human", "Durable human review", "Persist branch, tree and owner", "No wall-clock timeout", "WAIT", "marigold", "cylinder3", 560, 280),
    ("steer", "Explicit steering", "Resume, fresh dispatch or park", "Human feedback resets budget", "DECIDE", "marigold", "rhombus", 560, 432),
]
EDGES = [
    ("input", "eligible", "integrate", "assemble", "", []),
    ("check", "integrate", "gates", "snapshot", "", []),
    ("finish-gates", "gates", "merge", "gates complete", "exitX=0;exitY=0.65;entryX=1;entryY=0.25;", [(536, 179), (536, 306)]),
    ("review", "gates", "human", "RED / human gate", "exitX=0.30;exitY=1;entryX=0.30;entryY=0;", []),
    ("approve", "human", "gates", "approve: continue", "exitX=0.76;exitY=0;entryX=0.76;entryY=1;", []),
    ("merged", "merge", "complete", "merged", "", []),
    ("revise", "gates", "steer", "REVISE", "exitX=1;exitY=0.5;entryX=1;entryY=0.5;", [(783, 163), (783, 483)]),
    ("changes", "human", "steer", "changes", "exitX=0.30;exitY=1;entryX=0.30;entryY=0;", []),
    ("escalate", "steer", "human", "Proceed: park", "exitX=0.76;exitY=0;entryX=0.76;entryY=1;", []),
    ("revision", "steer", "integrate", "explicit revision", "exitX=0;exitY=0.65;entryX=0.5;entryY=0;", [(548, 498), (548, 92), (397, 92)]),
]


def cell(root, identifier, value, style, x, y, w, h, parent="1"):
    c = ET.SubElement(root, "mxCell", id=identifier, value=value, style=style, vertex="1", parent=parent)
    ET.SubElement(c, "mxGeometry", x=str(x), y=str(y), width=str(w), height=str(h), attrib={"as": "geometry"})
    return c


def text(root, identifier, value, x, y, w, h, size=12, color="#272320", bold=False, parent="1", mono=False):
    return cell(root, identifier, value,
                f"text;html=0;fillColor=none;strokeColor=none;whiteSpace=wrap;align=left;verticalAlign=middle;"
                f"fontFamily={'Cascadia Code' if mono else 'Segoe UI'};fontSize={size};fontColor={color};fontStyle={1 if bold else 0};",
                x, y, w, h, parent)


def document(upgrade):
    template = ET.parse(REPO / "docs" / "diagrams" / "drawio" / "fluent-template.drawio")
    # Reuse the established template envelope, not its sample technology claims.
    outer = copy.deepcopy(template.getroot())
    outer.set("agent", "GPT-6 Astra grounded collective review")
    diagram = outer.find("diagram")
    diagram.set("id", NAME)
    diagram.set("name", "Collective assembly and durable review")
    model = diagram.find("mxGraphModel")
    model.set("pageWidth", "794")
    model.set("pageHeight", "559")
    model.set("pageScale", "1")
    model.set("dx", "794")
    model.set("dy", "559")
    root = model.find("root")
    root.clear()
    ET.SubElement(root, "mxCell", id="0")
    ET.SubElement(root, "mxCell", id="1", parent="0")
    text(root, "title", "Collective assembly and durable review", 24, 16, 740, 30, 24, bold=True)
    text(root, "subtitle", "Workflow-authored checks; safety escalation waits for a human, not RaiBlocked.", 24, 49, 740, 24, 13, "#635c57")
    if upgrade:
        cell(root, "aggregate-tier", "", "rounded=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;", 14, 98, 766, 123)
        cell(root, "decision-tier", "", "rounded=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;", 14, 266, 766, 123)
    for identifier, title, subtitle, meta, badge, tone, shape, x, y in NODES:
        bg, fg = TOKENS[tone]
        if upgrade and identifier == "human":
            shape = "cylinder"
        cell(root, identifier, "", "rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#fdfbf8;strokeColor=#ece7e3;strokeWidth=1;shadow=1;", x, y, 210, 102)
        cell(root, identifier + "-accent", "", f"rounded=1;arcSize=100;fillColor={fg};strokeColor=none;", 0, 0, 5, 102, identifier)
        cell(root, identifier + "-icon", "", f"shape={shape};fillColor={bg};strokeColor={fg};strokeWidth=1.5;", 14, 14, 22, 22, identifier)
        text(root, identifier + "-title", title, 14, 41, 185, 21, 14, bold=True, parent=identifier)
        text(root, identifier + "-sub", subtitle, 14, 63, 185, 17, 11, "#635c57", parent=identifier)
        text(root, identifier + "-meta", meta, 14, 81, 185, 17, 10, "#746d68", parent=identifier)
        cell(root, identifier + "-badge", badge, f"rounded=1;arcSize=100;fillColor={bg};strokeColor=none;fontFamily=Segoe UI;fontSize=10;fontStyle=1;fontColor={fg};",
             132, 12, 66, 22, identifier)
    for identifier, source, target, label, ports, points in EDGES:
        if upgrade and identifier == "revision":
            ports = "exitX=0.5;exitY=1;entryX=0.5;entryY=0;"
            points = [(665, 546), (8, 546), (8, 86), (397, 86)]
        loop = identifier in {"revision", "revise", "changes"}
        color = "#d39300" if loop else "#746d68"
        style = (f"edgeStyle=orthogonalEdgeStyle;rounded=1;orthogonalLoop=1;jettySize=auto;html=0;"
                 f"endArrow=block;endFill=1;strokeColor={color};strokeWidth=1.5;fontFamily=Segoe UI;"
                 "fontSize=10;fontColor=#3f3935;labelBackgroundColor=#fdfbf8;jumpStyle=arc;jumpSize=6;"
                 + ("dashed=1;dashPattern=8 6;" if loop else "") + ports)
        c = ET.SubElement(root, "mxCell", id=identifier, value=label, style=style, edge="1", parent="1", source=source, target=target)
        geo = ET.SubElement(c, "mxGeometry", relative="1", attrib={"as": "geometry"})
        if upgrade and identifier == "revision":
            geo.set("x", "-0.5")
            geo.set("y", "10")
        if points:
            arr = ET.SubElement(geo, "Array", attrib={"as": "points"})
            for px, py in points:
                ET.SubElement(arr, "mxPoint", x=str(px), y=str(py))
    if upgrade:
        cell(root, "scope-note", "", "rounded=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;", 24, 418, 496, 116)
        text(root, "scope-title", "Boundaries that matter", 38, 425, 464, 23, 14, bold=True)
        text(root, "eligibility-note", "Quiescent is not eligible: failed children block partial assembly.", 38, 450, 464, 18, 11, "#635c57")
        text(root, "gate-note", "Non-code plans omit Build & Test; preview failure alone is not a veto.", 38, 471, 464, 18, 11, "#635c57")
        text(root, "recovery-note", "Recovered review reuses its snapshot. Missing metadata is exceptional.", 38, 492, 464, 18, 11, "#635c57")
        text(root, "decline-note", "Human decline ends the run; advisory steering does not reset children.", 38, 513, 464, 18, 11, "#635c57")
    else:
        text(root, "scope-note", "Scope: collective assembly, not a standalone or child workflow.\nHuman decline ends the run. Advisory steering makes no reset.", 24, 426, 496, 72, 12, "#635c57")
    return ET.ElementTree(outer)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("phase", choices=["pitch", "pass-01"])
    args = parser.parse_args()
    output = HERE / f"{NAME}-{args.phase}.drawio"
    if output.exists():
        raise SystemExit(f"Refusing to overwrite saved review artifact: {output}")
    tree = document(args.phase == "pass-01")
    ET.indent(tree, space="  ")
    tree.write(output, encoding="utf-8", xml_declaration=True)
    print(output)
