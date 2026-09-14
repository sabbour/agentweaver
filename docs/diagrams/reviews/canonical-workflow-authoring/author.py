"""Reproduce the grounded pitch and its sole visual-upgrade pass."""
from pathlib import Path
import argparse
import json
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
NAME = "canonical-workflow-authoring"
PALETTE = json.loads((REPO / "docs/diagrams/drawio/design-system.json").read_text())
ET.parse(REPO / "docs/diagrams/drawio/fluent-library.xml")
TONE = PALETTE["badges"]


def document():
    tree = ET.parse(REPO / "docs/diagrams/drawio/fluent-template.drawio")
    file = tree.getroot()
    diagram = file.find("diagram")
    diagram.set("id", NAME)
    diagram.set("name", "Workflow authoring — generation is not persistence")
    model = diagram.find("mxGraphModel")
    model.set("pageWidth", "1123")
    model.set("pageHeight", "1587")
    model.set("background", "#efeae7")
    root = model.find("root")
    for cell in list(root)[2:]:
        root.remove(cell)
    return tree, root


def vertex(root, id, value, x, y, w, h, style, parent="1"):
    cell = ET.SubElement(root, "mxCell", id=id, value=value, style=style,
                         vertex="1", parent=parent)
    ET.SubElement(cell, "mxGeometry", x=str(x), y=str(y), width=str(w),
                  height=str(h), **{"as": "geometry"})
    return cell


def text(root, id, value, x, y, w, h, size=24, color="#272320", bold=False,
         font="Segoe UI", align="left"):
    return vertex(root, id, value, x, y, w, h,
                  f"text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;"
                  f"fontFamily={font};fontSize={size};fontColor={color};"
                  f"fontStyle={1 if bold else 0};align={align};verticalAlign=middle;"
                  "spacing=0;")


def group(root, id, title, x, y, w, h, fill="#f8f4f1"):
    return vertex(root, id, title, x, y, w, h,
                  f"rounded=1;arcSize=3;fillColor={fill};strokeColor=#e2ddd9;"
                  "strokeWidth=1;align=left;verticalAlign=top;spacingTop=13;"
                  "spacingLeft=20;fontFamily=Segoe UI;fontSize=23;fontStyle=1;"
                  "fontColor=#3f3935;")


def edge(root, id, source, target, label="", points=(), exit=(.5, 1), entry=(.5, 0),
         revision=False, offset=(0, 0)):
    color = "#d39300" if revision else "#746d68"
    style = ("edgeStyle=orthogonalEdgeStyle;rounded=1;orthogonalLoop=1;"
             "jettySize=auto;html=1;endArrow=block;endFill=1;startArrow=none;"
             f"strokeColor={color};strokeWidth=2;fontFamily=Segoe UI;fontSize=21;"
             "fontColor=#3f3935;labelBackgroundColor=#fdfbf8;labelBorderColor=none;"
             f"exitX={exit[0]};exitY={exit[1]};entryX={entry[0]};entryY={entry[1]};"
             "exitDx=0;exitDy=0;entryDx=0;entryDy=0;jumpStyle=arc;jumpSize=8;")
    if revision:
        style += "dashed=1;dashPattern=8 6;"
    cell = ET.SubElement(root, "mxCell", id=id, value=label, style=style,
                         edge="1", parent="1", source=source, target=target)
    geom = ET.SubElement(cell, "mxGeometry", relative="1", **{"as": "geometry"})
    if points:
        array = ET.SubElement(geom, "Array", **{"as": "points"})
        for x, y in points:
            ET.SubElement(array, "mxPoint", x=str(x), y=str(y))
    if offset != (0, 0):
        ET.SubElement(geom, "mxPoint", x=str(offset[0]), y=str(offset[1]),
                      **{"as": "offset"})
    return cell


def pitch_card(root, id, title, subtitle, meta, badge, y, tone, shape):
    colors = TONE[tone]
    value = (f'<div style="font-size:38px;font-weight:600">{title}</div>'
             f'<div style="font-size:29px;color:#635c57;margin-top:20px">{subtitle}</div>'
             f'<div style="font-size:24px;color:#746d68;font-family:Cascadia Code;'
             f'margin-top:20px">{meta}</div>')
    vertex(root, id, value, 100, y, 923, 360,
           "rounded=1;arcSize=16;whiteSpace=wrap;html=1;fillColor=#fdfbf8;"
           "strokeColor=#ece7e3;strokeWidth=1;shadow=1;align=left;spacingLeft=125;"
           "spacingRight=30;fontFamily=Segoe UI;fontColor=#272320;")
    vertex(root, id+"-accent", "", 100, y, 5, 360,
           f"rounded=1;fillColor={colors['foreground']};strokeColor=none;")
    vertex(root, id+"-icon", "", 129, y+130, 62, 72,
           f"shape={shape};fillColor={colors['background']};"
           f"strokeColor={colors['foreground']};strokeWidth=2;")
    vertex(root, id+"-badge", badge, 760, y+20, 235, 38,
           f"rounded=1;arcSize=100;fillColor={colors['background']};strokeColor=none;"
           f"fontColor={colors['foreground']};fontFamily=Segoe UI;fontSize=22;fontStyle=1;")


def card(root, id, x, y, w, h, title, subtitle, meta, badge, tone, shape,
         detail=None):
    colors = TONE[tone]
    vertex(root, id, "", x, y, w, h,
           "rounded=1;arcSize=16;whiteSpace=wrap;html=1;fillColor=#fdfbf8;"
           "strokeColor=#ece7e3;strokeWidth=1;shadow=1;")
    vertex(root, id+"-accent", "", x, y, 5, h,
           f"rounded=1;arcSize=100;fillColor={colors['foreground']};strokeColor=none;")
    vertex(root, id+"-icon", "", x+18, y+18, 34, 38,
           f"shape={shape};fillColor={colors['background']};"
           f"strokeColor={colors['foreground']};strokeWidth=1.5;")
    text(root, id+"-title", title, x+66, y+14, w-82, 37, 28, bold=True)
    text(root, id+"-subtitle", subtitle, x+20, y+57, w-38, 31, 23, "#635c57")
    text(root, id+"-meta", meta, x+20, y+89, w-40, 27, 21, "#746d68",
         font="Cascadia Code")
    badge_w = max(90, len(badge)*12)
    vertex(root, id+"-badge", badge, x+w-badge_w-16, y+h-35, badge_w, 26,
           f"rounded=1;arcSize=100;whiteSpace=wrap;html=1;fillColor={colors['background']};"
           f"strokeColor=none;fontColor={colors['foreground']};fontFamily=Segoe UI;"
           "fontSize=18;fontStyle=1;align=center;verticalAlign=middle;")
    if detail:
        text(root, id+"-detail", detail, x+20, y+h-36, w-badge_w-52, 28, 20, "#635c57")


def pitch():
    tree, root = document()
    group(root, "authoring-boundary", "AUTHORING  /  Two deliberately separate operations",
          55, 230, 1013, 1180)
    text(root, "title", "Workflow authoring", 70, 60, 990, 68, 48, bold=True)
    text(root, "takeaway", "Generation produces a draft. Only explicit Save persists it.",
         70, 140, 980, 65, 30, "#635c57")
    pitch_card(root, "draft", "Generate & review",
               "Provider-gated generation checks YAML and runtime bindability.<br>"
               "One correction is allowed; unresolved errors stop generation.",
               "Human reviews the valid YAML draft.<br>No workflow file is saved.",
               "UNSAVED DRAFT", 330, "lavender", "process")
    pitch_card(root, "save", "Save & register",
               "Explicit Save validates again, then writes inside the project workspace.",
               "Registry sync reloads the definition.<br>"
               "A reload failure returns an error.",
               "PROJECT YAML", 960, "teal", "document")
    edge(root, "explicit-save", "draft", "save", "Human chooses Save", offset=(0, 0))
    return tree


def upgrade():
    tree, root = document()
    text(root, "title", "Workflow authoring", 50, 27, 1030, 57, 43, bold=True)
    text(root, "takeaway", "Generate a draft. Review it. Save deliberately.",
         50, 87, 1030, 37, 27, "#635c57")
    group(root, "generation-boundary", "1  GENERATE + REVIEW  /  No workflow file is saved",
          40, 137, 1043, 826)
    group(root, "persistence-boundary", "2  EXPLICIT SAVE  /  Project workspace + registry",
          40, 977, 1043, 580, "#e7e1dc")
    card(root, "request", 70, 195, 550, 145, "Authorize request",
         "Project ownership + AI execution plan", "POST …/workflows/generate",
         "GATED", "neutral", "process", "Description required")
    card(root, "context", 735, 195, 335, 145, "Prompt context",
         "Roles, schema, examples", "Project model override",
         "INPUT", "neutral", "document", "Catalog fallback")
    card(root, "generator", 70, 395, 550, 145, "Generate candidate",
         "CopilotWorkflowGenerator", "Model returns YAML, not a saved file",
         "AI DRAFT", "lavender", "component", "Create or edit")
    card(root, "validation", 70, 595, 550, 165, "Validate candidate",
         "WorkflowDefinitionLoader + binder dry-run",
         "Structure AND runtime bindability", "CHECK", "teal", "rhombus",
         "Strip fences; ensure id")
    card(root, "repair", 735, 595, 335, 165, "One correction",
         "Failed YAML + error", "Re-run same checks",
         "ONCE", "marigold", "process", "No third attempt")
    card(root, "generation-error", 735, 815, 335, 130, "Explicit error",
         "Second invalid result", "400 · not persisted",
         "STOP", "marigold", "mxgraph.flowchart.terminator")
    card(root, "review", 70, 815, 550, 130, "Review & edit draft",
         "Human edits YAML or the visual graph",
         "Valid draft stays unsaved", "HUMAN", "lavender", "document")
    card(root, "save-validation", 70, 1040, 550, 165, "Save: validate again",
         "Parse + structure + route id + binder",
         "PUT …/workflows/{workflowId}", "SAVE", "teal", "rhombus",
         "Ownership required")
    card(root, "save-error", 735, 1040, 335, 165, "Reject save",
         "Parse / id / bind error",
         "400 or 422 · no write", "STOP", "marigold", "mxgraph.flowchart.terminator",
         "Fix the draft")
    card(root, "write", 70, 1240, 550, 145, "Write project YAML",
         "Resolve the path inside the workspace",
         ".agentweaver/workflows/{id}.yaml", "FILE", "teal", "document",
         "Contained-path guard")
    card(root, "write-error", 735, 1240, 335, 145, "Write can fail",
         "Path guard or file I/O",
         "400 / 500 · stop here", "STOP", "marigold", "mxgraph.flowchart.terminator",
         "No success response")
    card(root, "registry", 70, 1420, 550, 125, "Sync → definition",
         "Extend allowed set if needed; reload", "Return saved detail on success",
         "200", "green", "component")
    card(root, "reload-error", 735, 1420, 335, 125, "Reload failure",
         "Written, not available",
         "422 / 500 · file may exist", "ERROR", "marigold", "document")
    edge(root, "e01", "request", "generator", "permitted", offset=(70, 0))
    edge(root, "e02", "context", "generator", "grounds prompt",
         [(685, 268), (685, 467)], exit=(0, .5), entry=(1, .5), offset=(0, 10))
    edge(root, "e03", "generator", "validation", "candidate YAML", offset=(90, 0))
    edge(root, "e04", "validation", "review", "valid; unsaved", offset=(80, 0))
    edge(root, "e05", "validation", "repair", "first invalid",
         exit=(1, .3), entry=(0, .3))
    edge(root, "e06", "repair", "generator", "one repair",
         [(1100, 677), (1100, 365), (650, 365), (650, 430)],
         exit=(1, .5), entry=(1, .24), revision=True, offset=(0, 15))
    edge(root, "e07", "validation", "generation-error", "invalid again",
         [(665, 727), (665, 880)], exit=(1, .8), entry=(0, .5), offset=(30, 0))
    edge(root, "e08", "review", "save-validation", "explicit Save", offset=(85, 0))
    edge(root, "e09", "save-validation", "save-error", "invalid",
         exit=(1, .5), entry=(0, .5))
    edge(root, "e10", "save-validation", "write", "checks pass", offset=(80, 0))
    edge(root, "e11", "write", "write-error", "failure",
         exit=(1, .5), entry=(0, .5))
    edge(root, "e12", "write", "registry", "write succeeded", offset=(95, 0))
    edge(root, "e13", "registry", "reload-error", "failure",
         exit=(1, .5), entry=(0, .5))
    return tree


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("phase", choices=["pitch", "pass-01"])
    args = parser.parse_args()
    out = HERE / f"{NAME}-{args.phase}.drawio"
    if out.exists():
        raise SystemExit(f"Refusing to overwrite preserved pass: {out}")
    tree = pitch() if args.phase == "pitch" else upgrade()
    ET.indent(tree, space="  ")
    tree.write(out, encoding="utf-8", xml_declaration=True)
    print(out)
