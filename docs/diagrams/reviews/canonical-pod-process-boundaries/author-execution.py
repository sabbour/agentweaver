"""Bounded execution-plan review artifact builder; never edits canonical assets."""
from __future__ import annotations

import argparse
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
MODELS = json.loads((HERE / "content-models.json").read_text(encoding="utf-8"))["diagrams"]
PLAN = json.loads((REPO / ".github/skills/docs-diagram-audit/reports/plan-deep-dive-execution.json").read_text(encoding="utf-8"))
PALETTE = json.loads((REPO / "docs/diagrams/drawio/design-system.json").read_text(encoding="utf-8"))
LIBRARY = ET.parse(REPO / "docs/diagrams/drawio/fluent-library.xml")
TEMPLATE = ET.parse(REPO / "docs/diagrams/drawio/fluent-template.drawio")
assert LIBRARY.getroot().tag == "mxlibrary"
assert PALETTE["drawioCliVersion"] == "31.4.5"
assert {m["name"] for m in MODELS} <= set(PLAN["diagram_names"] + PLAN["proposed_diagram_names"])
W, H = 793.70, 559.37  # ISO A5 landscape, draw.io's 96 px/in page units.
SHAPES = {
    "kubernetes": "mxgraph.kubernetes.pod",
    "azure": "mxgraph.azure2.key_vaults",
    "database": "cylinder3",
    "c4": "mxgraph.c4.person",
    "flowchart": "process",
    "network": "mxgraph.cisco19.router",
    "cloud": "cloud",
    "custom": "rounded",
}
TONES = {
    "kubernetes": "lavender", "azure": "teal", "database": "teal",
    "c4": "neutral", "flowchart": "green", "network": "marigold",
    "cloud": "lavender", "custom": "lavender",
}


def write_xml(root, destination):
    if destination.exists():
        raise FileExistsError(f"Do not overwrite an earlier review: {destination}")
    ET.indent(root)
    destination.write_text(ET.tostring(root, encoding="unicode"), encoding="utf-8")


def vertex(root, identity, value, bounds, style, parent="1"):
    cell = ET.SubElement(root, "mxCell", id=identity, value=value, style=style,
                         vertex="1", parent=parent)
    x, y, width, height = bounds
    ET.SubElement(cell, "mxGeometry", x=str(x), y=str(y), width=str(width),
                  height=str(height), **{"as": "geometry"})
    return cell


def text_style(size, color="#272320", bold=False, align="left"):
    return (f"text;html=0;whiteSpace=wrap;overflow=hidden;fillColor=none;"
            f"strokeColor=none;fontFamily=Segoe UI;fontSize={size};fontColor={color};"
            f"fontStyle={1 if bold else 0};align={align};verticalAlign=middle;"
            "spacing=0;spacingLeft=0;spacingRight=0;spacingTop=0;spacingBottom=0;")


def source(model, rich):
    tree = copy.deepcopy(TEMPLATE.getroot())
    tree.set("compressed", "false")
    tree.set("agent", "GPT-6 Astra execution-plan authoring")
    diagram = tree.find("diagram")
    diagram.set("id", model["name"])
    diagram.set("name", model["title"])
    graph = diagram.find("mxGraphModel")
    graph.set("pageWidth", str(W))
    graph.set("pageHeight", str(H))
    graph.set("pageScale", "1")
    graph.set("background", "#efeae7")
    root = graph.find("root")
    root.clear()
    ET.SubElement(root, "mxCell", id="0")
    ET.SubElement(root, "mxCell", id="1", parent="0")
    vertex(root, "title", model["title"], (24, 14, 746, 30), text_style(21, bold=True))
    vertex(root, "takeaway", model["takeaway"], (24, 48, 746, 35), text_style(11, "#635c57"))
    positions = {}
    for i, (identity, title, subtitle, metadata, kind) in enumerate(model["nodes"]):
        row, col = divmod(i, 3)
        x, y = 24 + col * 259, 102 + row * 128
        positions[identity] = (x, y, 228, 100)
        if not rich:
            vertex(root, identity, title, (x, y, 228, 100),
                   "rounded=1;whiteSpace=wrap;fontFamily=Segoe UI;fontSize=12;"
                   "fillColor=#fdfbf8;strokeColor=#e2ddd9;fontColor=#272320;")
            continue
        tone = PALETTE["badges"][TONES[kind]]
        vertex(root, identity, "", (x, y, 228, 100),
               "rounded=1;absoluteArcSize=1;arcSize=16;html=1;whiteSpace=wrap;"
               "fillColor=#fdfbf8;strokeColor=#e2ddd9;strokeWidth=1;shadow=1;")
        vertex(root, identity + "-accent", "", (0, 0, 5, 100),
               f"rounded=1;arcSize=100;fillColor={tone['foreground']};strokeColor=none;",
               identity)
        shape = SHAPES[kind]
        if kind == "custom":
            shape = "rectangle"
        icon_style = f"shape={shape};"
        if kind == "azure":
            image = {
                "identity": "identity/Managed_Identities.svg",
                "vault": "security/Key_Vaults.svg",
                "images": "containers/Container_Registries.svg",
            }[identity]
            icon_style = f"shape=image;image=img/lib/azure2/{image};"
        vertex(root, identity + "-icon", "", (14, 13, 25, 25),
               f"{icon_style}html=1;aspect=fixed;fillColor={tone['background']};"
               f"strokeColor={tone['foreground']};strokeWidth=1.4;", identity)
        vertex(root, identity + "-title", title, (47, 10, 168, 32),
               text_style(12, bold=True), identity)
        vertex(root, identity + "-subtitle", subtitle, (15, 46, 200, 20),
               text_style(10, "#635c57"), identity)
        vertex(root, identity + "-metadata", metadata, (15, 72, 147, 24),
               text_style(8.5, "#746d68").replace("Segoe UI", "Cascadia Code"), identity)
        vertex(root, identity + "-separator", "", (15, 69, 198, 0.7),
               "fillColor=#ece7e3;strokeColor=none;", identity)
        vertex(root, identity + "-badge",
               {"kubernetes": "K8S", "azure": "AZURE", "database": "DATA",
                "c4": "USER", "flowchart": "STEP", "network": "NET",
                "cloud": "VM", "custom": "AW"}[kind],
               (166, 79, 48, 15),
               f"rounded=1;arcSize=100;whiteSpace=wrap;html=0;"
               f"fillColor={tone['background']};strokeColor=none;"
               f"fontColor={tone['foreground']};fontFamily=Segoe UI;"
               "fontSize=7.5;fontStyle=1;align=center;verticalAlign=middle;",
               identity)
    for i, (start, end, verb) in enumerate(model["edges"], 1):
        style = ("edgeStyle=orthogonalEdgeStyle;rounded=1;orthogonalLoop=1;jettySize=8;"
                 "html=1;endArrow=block;endFill=1;strokeColor=#746d68;strokeWidth=1.5;"
                 "fontFamily=Segoe UI;fontSize=8;fontColor=#3f3935;"
                 "labelBackgroundColor=#fdfbf8;labelBorderColor=none;jumpStyle=arc;jumpSize=6;")
        edge = ET.SubElement(root, "mxCell", id=f"arrow-{i}", value=verb, style=style,
                             edge="1", parent="1", source=start, target=end)
        ET.SubElement(edge, "mxGeometry", relative="1", **{"as": "geometry"})
    notes_y = 502 if len(model["nodes"]) > 6 else 378
    for i, note in enumerate(model["notes"]):
        vertex(root, f"note-{i}", note, (24, notes_y + i * 15, 744, 14),
               text_style(9, "#635c57"))
    return tree


def render(destination, executable):
    output = destination.with_suffix(".png")
    if output.exists():
        raise FileExistsError(output)
    subprocess.run([str(executable), "--export", "--format", "png", "--border", "16",
                    "--scale", "2", "--output", str(output), str(destination)],
                   cwd=REPO, check=True, timeout=120)
    if not output.exists() or output.stat().st_size < 1000:
        raise RuntimeError(f"Missing/empty Desktop export: {output}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("stage", choices=["pitch", "pass-01"])
    parser.add_argument("--drawio", type=Path, required=True)
    parser.add_argument("--name", action="append")
    args = parser.parse_args()
    version = subprocess.check_output(
        ["powershell.exe", "-NoProfile", "-Command",
         f"(Get-Item -LiteralPath '{args.drawio}').VersionInfo.ProductVersion"],
        text=True).strip()
    version = ".".join(version.split(".")[:3])
    if version != "31.4.5":
        raise RuntimeError(f"Pinned draw.io 31.4.5 required, found {version}")
    results = []
    for model in MODELS:
        if args.name and model["name"] not in args.name:
            continue
        directory = REPO / "docs/diagrams/reviews" / model["name"]
        directory.mkdir(parents=True, exist_ok=True)
        output = directory / f"{model['name']}-{args.stage}.drawio"
        write_xml(source(model, args.stage == "pass-01"), output)
        render(output, args.drawio)
        record = {
            "diagram": model["name"], "stage": args.stage, "renderer": version,
            "page": {"width": W, "height": H, "orientation": "A5-landscape"},
            "source": output.name, "png": output.with_suffix(".png").name,
            "evidence": model["evidence"],
            "png_inspected_print": False, "png_inspected_enlarged": False,
            "symbols": {n[0]: ("custom:agentweaver" if n[4] == "custom" else "native:" + n[4])
                        for n in model["nodes"]},
            "publication_status": "draft-not-promoted",
        }
        output.with_suffix(".record.json").write_text(json.dumps(record, indent=2) + "\n")
        results.append(record)
    (HERE / f"{args.stage}-exports.json").write_text(json.dumps(results, indent=2) + "\n")


if __name__ == "__main__":
    main()
