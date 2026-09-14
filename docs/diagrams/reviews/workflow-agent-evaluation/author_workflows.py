"""Bounded authoring artifact for the nine explicitly assigned workflow diagrams."""
from pathlib import Path
import argparse
import copy
import html
import importlib.util
import json
import re
import subprocess
import xml.etree.ElementTree as ET

import yaml
from PIL import Image, ImageOps, ImageDraw

ROOT = Path(__file__).resolve().parents[4]
REVIEWS = ROOT / "docs/diagrams/reviews"
CLI = Path(r"C:\Users\asabbour\.copilot\session-state\f6a87a42-fc18-4c23-a5f5-3e1c5c41d367\files\drawio-cli\app\draw.io.exe")
NAMES = [
    "workflow-agent-evaluation", "workflow-bug-fix", "workflow-content-authoring",
    "workflow-incident-response", "workflow-infra-ops", "workflow-pm-discovery",
    "workflow-software-delivery", "canonical-default-workflow",
    "canonical-workflow-selection",
]
W, H = 583, 827
INK, MUTED, PAPER, CARD = "#272320", "#635c57", "#efeae7", "#fdfbf8"
TONES = {"agent": ("#3f3682", "#e8e3ff"), "gate": ("#825e00", "#fae6ad"),
         "action": ("#00666d", "#d8f0f1"), "done": ("#0e700e", "#dcf0da"),
         "terminal": ("#635c57", "#e9e3de")}


def metric_module():
    path = ROOT / ".github/skills/docs-diagram-iterate/scripts/check_xml_growth.py"
    spec = importlib.util.spec_from_file_location("growth", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def selection_model():
    code = "apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs"
    selector = "apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs"
    nodes = [
        ("available", "Load candidates", "process", "Project default ordered first", "registry.Available", "281–296"),
        ("explicit", "Explicit override?", "check", "Dialog value, else backlog pin", "must be available", "299–329"),
        ("conversation", "Conversational choice?", "check", "Revision feedback: use {id}", "must be available", "333–349"),
        ("count", "Candidate count", "check", "Only automatic selection", "0 / 1 / multiple", "359–375"),
        ("model", "Ask selection model", "prompt", "Goal + roles + process fit", "maximum 2 attempts", "378–387"),
        ("validate", "Usable candidate?", "check", "Parse / normalize / prose match", "reject unknown choices", "378–387"),
        ("selected", "Selected workflow", "terminal", "Emit selection + rationale", "workflow_selected", "387–389"),
        ("explicit-result", "Explicit choice", "terminal", "Emit selection", "not auto-selected", "311–321"),
        ("silent", "Silent choice", "terminal", "One: candidate", "Zero: project default", "362–374"),
        ("fallback", "Model fallback", "terminal", "default / standard", "then non-code-review", "378–387"),
        ("outer", "Outer fallback", "terminal", "Project default", "when catch permits", "391–405"),
    ]
    result = []
    for id_, title, type_, sub, meta, lines in nodes:
        evidence = f"{code}:{lines}"
        if id_ in ("validate", "fallback"):
            evidence += f"; {selector}:93–215"
        result.append(dict(id=id_, label=title, type=type_, role="selection",
                           detail=sub, metadata=meta, evidence=evidence))
    edges = [
        ("available", "explicit", ""),
        ("explicit", "explicit-result", "available"),
        ("explicit", "conversation", "absent / invalid"),
        ("conversation", "explicit-result", "available"),
        ("conversation", "count", "absent / invalid"),
        ("count", "silent", "0 or 1"),
        ("count", "model", "2+"),
        ("model", "validate", "response"),
        ("model", "fallback", "exception"),
        ("validate", "selected", "accepted"),
        ("validate", "model", "retry once"),
        ("validate", "fallback", "2 unusable"),
        ("fallback", "selected", "emit choice"),
        ("available", "outer", "outer catch"),
    ]
    return dict(title="Workflow selection", nodes=result,
                edges=[dict(source=a, target=b, label=c,
                            evidence=f"{code}:271–405; {selector}:93–215")
                       for a, b, c in edges],
                main=["available", "explicit", "conversation", "count", "model", "validate", "selected"],
                note="Trigger-agnostic • explicit choices precede singleton",
                footer="Post-decomposition Build & Test compatibility is a separate check (executor:407–494).")


def load_model(name):
    if name == "canonical-workflow-selection":
        return selection_model()
    if name == "canonical-default-workflow":
        source = ROOT / "apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs"
        text = source.read_text(encoding="utf-8")
        raw = text.split('"""')[1]
        doc = yaml.safe_load(raw)
    else:
        source = ROOT / ("packages/Agentweaver.Squad/Catalog/Resources/workflows/"
                         + name.removeprefix("workflow-").replace("-", "_") + ".yaml")
        raw = source.read_text(encoding="utf-8")
        doc = yaml.safe_load(raw)
    lines = source.read_text(encoding="utf-8").splitlines()
    relative = source.relative_to(ROOT).as_posix()
    nodes = []
    for node in doc["nodes"]:
        line = next(i + 1 for i, s in enumerate(lines) if re.search(r"- id:\s*" + re.escape(node["id"]) + r"\s*$", s))
        node = dict(node)
        node["evidence"] = f"{relative}:{line}"
        node["detail"] = {
            "prompt": "Agent task", "check": "Verdict routing", "peer_review": "Independent peer review",
            "build_test": "Build and test verification", "terminal": "Workflow endpoint",
            "merge": "Merge outcome routing", "scribe": "Record the run outcome",
            "open_pull_request": "Create or reuse pull request",
        }.get(node["type"], node["type"].replace("_", " ").capitalize())
        node["metadata"] = node.get("agent") or node.get("gate_kind") or node.get("role", "workflow")
        if name == "canonical-default-workflow" and node["id"] == "push-pr":
            node["label"] = "Publish / reuse PR"
        nodes.append(node)
    edges = []
    edge_start = next(i for i, s in enumerate(lines) if s.strip() == "edges:")
    cursor = edge_start
    for edge in doc["edges"]:
        line = next(i + 1 for i in range(cursor, len(lines))
                    if re.match(r"\s*- from:\s*" + re.escape(edge["from"]) + r"(?:\s|$)", lines[i]))
        cursor = line
        edges.append(dict(source=edge["from"], target=edge["to"], label=edge.get("when", ""),
                          evidence=f"{relative}:{line}–{line + (2 if edge.get('when') else 1)}"))
    main = [n["id"] for n in nodes if n["type"] != "terminal"]
    title = "Generic default workflow" if name.startswith("canonical") else doc["name"] + " workflow"
    note = "Authored graph • all YAML branches retained"
    footer = "Solid: advance / outcome    Dashed marigold: revision / return"
    if name == "workflow-agent-evaluation":
        footer = "Evaluation Runs and Collect Results are prompt steps, not fan-out / fan-in."
    if name == "canonical-default-workflow":
        note = "Built-in template • merge → PR publication → Scribe"
        footer = "PR publication does not prove git push; publication failure can still reach Scribe."
    return dict(title=title, nodes=nodes, edges=edges, main=main, note=note, footer=footer)


def frame(name):
    tree = ET.parse(ROOT / "docs/diagrams/drawio/fluent-template.drawio")
    # Load the reusable native library before deriving the Agentweaver card hierarchy.
    library = ET.parse(ROOT / "docs/diagrams/drawio/fluent-library.xml")
    assert "Agentweaver" in library.getroot().text
    root = tree.getroot()
    diagram = root.find("diagram")
    diagram.set("id", name)
    diagram.set("name", name)
    model = diagram.find("mxGraphModel")
    model.set("pageWidth", str(W))
    model.set("pageHeight", str(H))
    model.set("background", PAPER)
    graph = model.find("root")
    for child in list(graph):
        if child.get("id") not in ("0", "1"):
            graph.remove(child)
    return tree, graph


def vertex(graph, id_, label, box, style="", parent="1"):
    cell = ET.SubElement(graph, "mxCell", id=id_, value=label, style=style,
                         vertex="1", parent=parent)
    x, y, w, h = box
    ET.SubElement(cell, "mxGeometry", x=str(x), y=str(y), width=str(w), height=str(h), **{"as": "geometry"})
    return cell


def text(graph, id_, label, box, size=11, color=INK, bold=False, align="left", parent="1", mono=False):
    return vertex(graph, id_, label, box,
                   f"text;html=0;whiteSpace=wrap;fillColor=none;strokeColor=none;"
                   f"fontFamily={'Cascadia Code' if mono else 'Segoe UI'};fontSize={size};"
                   f"fontColor={color};fontStyle={1 if bold else 0};align={align};"
                   "verticalAlign=middle;spacing=0;", parent)


def positions(model):
    main = model["main"]
    step = min(100, 610 / max(1, len(main) - 1))
    boxes = {id_: (185, 105 + i * step, 215, 66) for i, id_ in enumerate(main)}
    if "selected" in main:
        boxes.update({"explicit-result": (466, 210, 102, 74), "silent": (466, 403, 102, 74),
                      "fallback": (466, 604, 102, 74), "outer": (466, 108, 102, 74)})
    else:
        for n in model["nodes"]:
            if n["id"] in boxes:
                continue
            if n["id"] == "done":
                boxes[n["id"]] = (466, 720, 102, 66)
            elif "safety" in n["id"]:
                boxes[n["id"]] = (466, 300, 102, 66)
            else:
                boxes[n["id"]] = (466, 520, 102, 66)
    return boxes


def node_tone(node):
    if node["id"] == "done" or node["id"] == "selected":
        return "done"
    if node["type"] == "terminal":
        return "terminal"
    if node["type"] in ("check", "peer_review", "build_test"):
        return "gate"
    if node["type"] in ("merge", "scribe", "open_pull_request"):
        return "action"
    return "agent"


def symbol(node):
    if node["type"] in ("check", "peer_review", "build_test"):
        return "rhombus"
    if node["type"] in ("scribe", "open_pull_request") or node["id"] in ("report", "publish", "postmortem"):
        return "shape=document"
    if node["type"] == "terminal":
        return "ellipse"
    return "shape=process"


def card(graph, node, box, index):
    id_ = node["id"]
    x, y, w, h = box
    accent, tint = TONES[node_tone(node)]
    compact = w < 150
    base = ("rounded=1;arcSize=16;fillColor=" + CARD + ";strokeColor=#e2ddd9;strokeWidth=1;"
            "shadow=1;whiteSpace=wrap;html=0;fontFamily=Segoe UI;fontSize=11;fontColor=" + INK + ";")
    vertex(graph, id_, "", box, base)
    vertex(graph, id_ + "-accent", "", (0, 0, 5, h),
           f"rounded=1;arcSize=100;fillColor={accent};strokeColor=none;", id_)
    if compact:
        vertex(graph, id_ + "-symbol", "", (10, 8, 12, 12),
               f"{symbol(node)};fillColor={tint};strokeColor={accent};strokeWidth=1.4;", id_)
        text(graph, id_ + "-title", node["label"], (10, 23, w - 16, 19), 11, bold=True, parent=id_)
        text(graph, id_ + "-subtitle", node["detail"], (10, 44, w - 16, 14), 8, MUTED, parent=id_)
        if h > 70:
            text(graph, id_ + "-metadata", node["metadata"], (10, 58, w - 16, 12), 7.5, MUTED, parent=id_)
        text(graph, id_ + "-ordinal", "END", (30, 8, w - 40, 12), 8, accent, True, parent=id_)
        return
    vertex(graph, id_ + "-icon-bed", "", (12, 12, 32, 32),
           f"rounded=1;arcSize=24;fillColor={tint};strokeColor=none;", id_)
    vertex(graph, id_ + "-symbol", "", (19, 19, 18, 18),
           f"{symbol(node)};fillColor={CARD};strokeColor={accent};strokeWidth=1.6;", id_)
    text(graph, id_ + "-title", node["label"], (52, 8, w - 61, 19),
         12 if len(node["label"]) < 23 else 11, bold=True, parent=id_)
    text(graph, id_ + "-subtitle", node["detail"], (52, 28, w - 61, 13), 9, MUTED, parent=id_)
    vertex(graph, id_ + "-divider", "", (52, 45, w - 63, 1),
           "fillColor=#ece7e3;strokeColor=none;", id_)
    text(graph, id_ + "-metadata", node["metadata"], (52, 49, w - 111, 12),
         8, "#746d68", parent=id_, mono=True)
    badge = {"agent": "TASK", "gate": "GATE", "action": "ACTION", "done": "DONE", "terminal": "END"}[node_tone(node)]
    vertex(graph, id_ + "-badge", badge, (w - 54, 48, 45, 14),
           f"rounded=1;arcSize=100;fillColor={tint};strokeColor=none;fontFamily=Segoe UI;"
           f"fontColor={accent};fontSize=7.5;fontStyle=1;align=center;verticalAlign=middle;", id_)
    text(graph, id_ + "-ordinal", f"{index:02}", (15, 49, 26, 12), 8, accent, True, "center", id_)

    vertex(graph, id_ + "-source-panel", "", (45, y + 5, 118, 51),
           "rounded=1;arcSize=10;fillColor=#f8f4f1;strokeColor=#e2ddd9;strokeWidth=0.7;")
    location = node["evidence"].split(";")[0].rsplit(":", 1)[-1]
    text(graph, id_ + "-source-location", f"SOURCE  {location}", (53, y + 10, 103, 11),
         7.5, "#746d68", True)
    text(graph, id_ + "-source-id", node["id"], (53, y + 22, 103, 14),
         8, "#3f3935", mono=True)
    text(graph, id_ + "-source-type", node["type"], (53, y + 38, 103, 12),
         8, accent)


def connector(graph, edge, i, boxes, main, detailed, return_index, outcome_index,
              source_outcome_index=0, target_index=0, target_count=1):
    a, b = edge["source"], edge["target"]
    ax, ay, aw, ah = boxes[a]
    bx, by, bw, bh = boxes[b]
    is_return = a in main and b in main and main.index(b) <= main.index(a)
    adjacent = a in main and b in main and main.index(b) == main.index(a) + 1
    is_side = b not in main
    points = []
    if adjacent:
        ports = "exitX=0.5;exitY=1;entryX=0.5;entryY=0;"
        label_pos = (ax + aw / 2 + 7, (ay + ah + by) / 2 - 6)
    elif a == "fallback" and b == "selected":
        ports = "exitX=0.5;exitY=1;entryX=1;entryY=0.7;"
        points = [(ax + aw / 2, 790), (436, 790), (436, by + bh * .7)]
        label_pos = (470, 776)
    elif is_return:
        rail = 10 + return_index * 6
        sy, ty = ay + ah + 10, by + bh + 10 + return_index * 1.2
        target_x = bx + 13 + return_index * 6
        ports = f"exitX=0;exitY=0.8;entryX={(target_x-bx)/bw};entryY=1;"
        points = [(174, ay + ah * .8), (174, sy), (rail, sy),
                  (rail, ty), (target_x, ty)]
        label_pos = (60, sy - 13)
    elif b == "outer":
        ports = "exitX=1;exitY=0.15;entryX=0;entryY=0.5;"
        points = [(447, ay + ah * .15), (447, by + bh / 2)]
        label_pos = (403, 103)
    else:
        # Distinct rails prevent overlapping outcome edges; native jumps mark crossings.
        rail = 414 + outcome_index * 5
        fraction = .25 + source_outcome_index * .45
        target_fraction = (target_index + 1) / (target_count + 1)
        sy = ay + ah * fraction
        ports = f"exitX=1;exitY={fraction};entryX=0;entryY={target_fraction};"
        if b in main:
            rail = 441
            ports = f"exitX=1;exitY={fraction};entryX=1;entryY={target_fraction};"
        points = [(rail, sy), (rail, by + bh * target_fraction)]
        label_pos = (403, sy - 25)
    style = "edgeStyle=orthogonalEdgeStyle;rounded=1;endArrow=block;endFill=1;"
    if detailed:
        style += (f"strokeColor={'#a97513' if is_return else '#746d68'};strokeWidth=1.3;"
                  "jettySize=10;orthogonalLoop=1;jumpStyle=arc;jumpSize=6;html=0;"
                  "fontFamily=Segoe UI;fontSize=9;labelBackgroundColor=#f8f4f1;"
                  "labelBorderColor=none;startArrow=none;")
        if is_return:
            style += "dashed=1;dashPattern=5 3;"
    cell = ET.SubElement(graph, "mxCell", id=f"edge-{i:02}", source=a, target=b,
                         value=edge["label"] if not detailed else "", style=style + ports, edge="1", parent="1")
    geo = ET.SubElement(cell, "mxGeometry", relative="1", **{"as": "geometry"})
    if points:
        array = ET.SubElement(geo, "Array", **{"as": "points"})
        for px, py in points:
            ET.SubElement(array, "mxPoint", x=str(px), y=str(py))
    if detailed and edge["label"]:
        label = edge["label"]
        width = min(116, max(35, len(label) * 4.9))
        if not adjacent and not is_return and b != "outer":
            # Side outcome labels use their own gutter, not a detached remote legend.
            width = 60
            label = label.replace("safety-failed", "safety-\nfailed").replace("no-changes", "no-\nchanges")
        label_cell = text(graph, f"edge-{i:02}-label", label,
                          (*label_pos, width, 24 if "\n" in label else 12), 9,
                          "#825e00" if is_return else "#3f3935")
        label_cell.set("style", label_cell.get("style").replace("fillColor=none", "fillColor=#f8f4f1"))
    return is_return, is_side


def draw(name, stage):
    model = load_model(name)
    folder = REVIEWS / name
    folder.mkdir(parents=True, exist_ok=True)
    tree, graph = frame(name)
    detailed = stage != "pitch"
    boxes = positions(model)
    if detailed:
        vertex(graph, "paper", "", (0, 0, W, H), f"fillColor={PAPER};strokeColor=none;")
        vertex(graph, "flow-surface", "", (173, 86, 239, 708),
               "rounded=1;arcSize=4;fillColor=#f8f4f1;strokeColor=#e2ddd9;strokeWidth=1;")
        text(graph, "eyebrow", "AGENTWEAVER  /  WORKFLOWS", (22, 14, 410, 17), 9, "#746d68", True)
        text(graph, "title", model["title"], (22, 33, 545, 30), 23, bold=True)
        text(graph, "subtitle", model["note"], (22, 65, 545, 16), 10, MUTED)
        text(graph, "returns-heading", "SOURCE / RETURN", (45, 87, 120, 14), 8, "#825e00", True)
        text(graph, "outcomes-heading", "OUTCOMES", (466, 87, 104, 14), 8, MUTED, True)
        text(graph, "footer", model["footer"], (18, 801, 549, 18), 8, MUTED)
    else:
        vertex(graph, "pitch-title", model["title"], (18, 25, 549, 36))
    for i, node in enumerate(model["nodes"], 1):
        if detailed:
            card(graph, node, boxes[node["id"]], i)
        else:
            vertex(graph, node["id"], node["label"], boxes[node["id"]], symbol(node))
    return_index = 0
    outcome_index = 0
    seen_sources = {}
    seen_targets = {}
    for i, edge in enumerate(model["edges"], 1):
        a, b = edge["source"], edge["target"]
        nonadjacent = not (a in model["main"] and b in model["main"]
                          and model["main"].index(b) <= model["main"].index(a) + 1)
        target_count = sum(1 for e in model["edges"] if e["target"] == b)
        back, side = connector(graph, edge, i, boxes, model["main"], detailed,
                               return_index, outcome_index, seen_sources.get(a, 0),
                               seen_targets.get(b, 0), target_count)
        if nonadjacent:
            seen_sources[a] = seen_sources.get(a, 0) + 1
        seen_targets[b] = seen_targets.get(b, 0) + 1
        return_index += int(back)
        outcome_index += int(side)
    path = folder / f"{name}-{stage}.drawio"
    assert not path.exists(), f"Never overwrite an inspected source: {path}"
    ET.indent(tree, space="  ")
    tree.write(path, encoding="utf-8", xml_declaration=True)
    return path, model


def export(path):
    dest = path.with_suffix(".png")
    completed = subprocess.run([str(CLI), "--export", "--format", "png", "--border", "16",
                                "--scale", "2", "--output", str(dest), str(path)],
                               cwd=ROOT, capture_output=True, text=True, timeout=120)
    if completed.returncode:
        raise RuntimeError(completed.stdout + completed.stderr)
    assert dest.exists()
    with Image.open(dest) as image:
        image.convert("RGB").resize((W, H), Image.Resampling.LANCZOS).save(
            path.with_name(path.stem + "-print.png"))
    print(dest.name, flush=True)


def ground(name, model):
    folder = REVIEWS / name
    evidence = dict(owner="astra-workflow-assets", disposition="retain" if name.startswith("workflow-") else "redesign",
                    scope="Only assigned source/raster/hash/generated cleanup/review artifacts; no inventory writes.",
                    model=model,
                    supplied_research=dict(
                        component_thread="Completed independently by parent; original output is in a prohibited temporary directory, not read here.",
                        flow_thread="Completed independently by parent; original output is in a prohibited temporary directory, not read here.",
                        assurance="Parent reports independent YAML tuple equality: 8/15/11/7/14/6/17 edges. CatalogWorkflowBindingTests gate theory includes evaluation; bindability theory does not.",
                        reconciled_facts="Catalog graphs retained, not legacy visuals. No authored merge/PR/Scribe in seven catalogs. Default has explicit merge -> PR -> Scribe. Selection is trigger agnostic; overrides precede singleton, 2 parse attempts, exception fallback immediate."),
                    visual_sources=[
                        "docs/diagrams/drawio/fluent-template.drawio",
                        "docs/diagrams/drawio/fluent-library.xml",
                        "apps/web/src/components/WorkflowGraphPanel.tsx:1–90",
                    ],
                    assets="Native diagrams.net flowchart process, decision, document and terminal shapes. No external logos. Native shape implementations distributed with pinned draw.io Desktop 31.4.5 (Apache-2.0 diagrams.net); repository Fluent chrome.",
                    classifications={n["id"]: "native:flowchart" for n in model["nodes"]})
    (folder / "evidence.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    (folder / "ownership.json").write_text(json.dumps(
        {"name": name, "owner": "astra-workflow-assets", "scope": "exclusive_asset_paths", "inventory_write": False},
        indent=2) + "\n", encoding="utf-8")


def correct(name, previous, stage):
    folder = REVIEWS / name
    source = folder / f"{name}-{previous}.drawio"
    dest = folder / f"{name}-{stage}.drawio"
    assert not dest.exists()
    tree = ET.parse(source)
    graph = tree.getroot().find("diagram/mxGraphModel/root")
    cells = {c.get("id"): c for c in graph.findall("mxCell")}
    model = load_model(name)
    boxes = positions(model)
    main = model["main"]
    return_groups = {}
    for i, edge in enumerate(model["edges"], 1):
        a, b = edge["source"], edge["target"]
        if a in main and b in main and main.index(b) <= main.index(a):
            return_groups.setdefault(b, []).append((i, edge))
        label = cells.get(f"edge-{i:02}-label")
        if label is not None and "\n" not in label.get("value", ""):
            if a in main and (b not in main or main.index(b) > main.index(a) + 1) and b != "outer":
                geo = label.find("mxGeometry")
                geo.set("y", str(float(geo.get("y")) + 12))
    for group_index, (target, group) in enumerate(return_groups.items()):
        rail = 20 + group_index * 14
        bx, by, bw, bh = boxes[target]
        ty = by + bh + (10 if group_index == 0 else 24)
        target_x = bx + 24 + group_index * 12
        max_source_y = max(boxes[e["source"]][1] for _, e in group)
        for i, edge in group:
            ax, ay, aw, ah = boxes[edge["source"]]
            sy = ay + ah + 10
            cell = cells[f"edge-{i:02}"]
            style = cell.get("style")
            style = re.sub(r"entryX=[^;]+;", f"entryX={(target_x-bx)/bw};", style)
            cell.set("style", style)
            geo = cell.find("mxGeometry")
            for child in list(geo):
                geo.remove(child)
            array = ET.SubElement(geo, "Array", **{"as": "points"})
            for x, y in [(174, ay + ah * .8), (174, sy), (rail, sy), (rail, ty), (target_x, ty)]:
                ET.SubElement(array, "mxPoint", x=str(x), y=str(y))
            if ay < max_source_y:
                vertex(graph, f"return-join-{i:02}", "", (rail - 2, sy - 2, 4, 4),
                       "ellipse;fillColor=#a97513;strokeColor=#a97513;strokeWidth=1;")
    if name == "canonical-workflow-selection":
        cell = cells["fallback-symbol"]
        cell.set("style", cell.get("style").replace("ellipse;", "shape=process;"))
        cells["fallback-ordinal"].set("value", "PICK")
    ET.indent(tree, space="  ")
    tree.write(dest, encoding="utf-8", xml_declaration=True)
    export(dest)


def parallel_returns(name, previous, stage):
    folder = REVIEWS / name
    source = folder / f"{name}-{previous}.drawio"
    dest = folder / f"{name}-{stage}.drawio"
    assert not dest.exists()
    tree = ET.parse(source)
    graph = tree.getroot().find("diagram/mxGraphModel/root")
    cells = {c.get("id"): c for c in graph.findall("mxCell")}
    for cell in list(graph):
        id_ = cell.get("id", "")
        if id_.startswith("return-join-"):
            graph.remove(cell)
        if "-source-" in id_:
            geo = cell.find("mxGeometry")
            geo.set("x", str(float(geo.get("x")) + 6))
            geo.set("width", str(float(geo.get("width")) - 6))
    model = load_model(name)
    boxes, main = positions(model), model["main"]
    for id_ in main:
        cells[id_].find("mxGeometry").set("height", "62")
        cells[id_ + "-accent"].find("mxGeometry").set("height", "62")
        cells[id_ + "-badge"].find("mxGeometry").set("y", "46")
        cells[id_ + "-metadata"].find("mxGeometry").set("y", "47")
        x, y, w, _ = boxes[id_]
        boxes[id_] = (x, y, w, 62)
    return_index = 0
    for i, edge in enumerate(model["edges"], 1):
        a, b = edge["source"], edge["target"]
        if not (a in main and b in main and main.index(b) <= main.index(a)):
            cell = cells[f"edge-{i:02}"]
            points = cell.findall("mxGeometry/Array/mxPoint")
            if a in main and points:
                match = re.search(r"exitY=([^;]+)", cell.get("style"))
                if match:
                    adjustment = 4 * float(match.group(1))
                    points[0].set("y", str(float(points[0].get("y")) - adjustment))
                    label = cells.get(f"edge-{i:02}-label")
                    if label is not None:
                        geo = label.find("mxGeometry")
                        geo.set("y", str(float(geo.get("y")) - adjustment))
            if b in main and points:
                match = re.search(r"entryY=([^;]+)", cell.get("style"))
                if match:
                    points[-1].set("y", str(float(points[-1].get("y")) - 4 * float(match.group(1))))
            continue
        ax, ay, aw, ah = boxes[a]
        bx, by, bw, bh = boxes[b]
        rail = 12 + return_index * 8
        sy = ay + ah + 10
        ty = by + bh + 10 + return_index * 2.5
        if a == "merge" and b == "review":
            ty += 10
        target_x = bx + 24 + return_index * 18
        cell = cells[f"edge-{i:02}"]
        cell.set("style", re.sub(r"entryX=[^;]+;", f"entryX={(target_x-bx)/bw};", cell.get("style")))
        cell.set("style", cell.get("style").replace("jettySize=10;", "jettySize=6;"))
        cells[f"edge-{i:02}-label"].find("mxGeometry").set("y", str(sy - 13))
        geo = cell.find("mxGeometry")
        for child in list(geo):
            geo.remove(child)
        array = ET.SubElement(geo, "Array", **{"as": "points"})
        for x, y in [(174, ay + ah * .8), (174, sy), (rail, sy), (rail, ty), (target_x, ty)]:
            ET.SubElement(array, "mxPoint", x=str(x), y=str(y))
        return_index += 1
    ET.indent(tree, space="  ")
    tree.write(dest, encoding="utf-8", xml_declaration=True)
    export(dest)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["pitch", "pass-01", "upgrade", "correct", "parallel-returns", "export", "copy-pass", "growth", "sheets"])
    parser.add_argument("--name", action="append", choices=NAMES)
    parser.add_argument("--stage")
    parser.add_argument("--previous")
    args = parser.parse_args()
    names = args.name or NAMES
    if args.action == "sheets":
        for i in range(0, len(names), 3):
            group = names[i:i + 3]
            sheet = Image.new("RGB", (W * len(group), H), PAPER)
            for j, name in enumerate(group):
                path = REVIEWS / name / f"{name}-{args.stage}-print.png"
                with Image.open(path) as image:
                    sheet.paste(image, (j * W, 0))
            dest = REVIEWS / group[0] / f"{args.stage}-print-contact.png"
            sheet.save(dest)
            print(dest.name, group, flush=True)
        return
    for name in names:
        folder = REVIEWS / name
        if args.action in ("pitch", "pass-01", "upgrade"):
            path, model = draw(name, args.stage if args.action == "upgrade" else args.action)
            if args.action == "pitch":
                ground(name, model)
            export(path)
        elif args.action == "export":
            export(folder / f"{name}-{args.stage}.drawio")
        elif args.action == "correct":
            correct(name, args.previous, args.stage)
        elif args.action == "parallel-returns":
            parallel_returns(name, args.previous, args.stage)
        elif args.action == "copy-pass":
            source = folder / f"{name}-{args.previous}.drawio"
            dest = folder / f"{name}-{args.stage}.drawio"
            assert not dest.exists()
            dest.write_bytes(source.read_bytes())
            export(dest)
        else:
            report = metric_module().assess(
                (folder / f"{name}-pitch.drawio").read_text(encoding="utf-8"),
                (folder / f"{name}-{args.stage or 'pass-01'}.drawio").read_text(encoding="utf-8"))
            (folder / "growth.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            print(name, json.dumps(report), flush=True)


if __name__ == "__main__":
    main()
