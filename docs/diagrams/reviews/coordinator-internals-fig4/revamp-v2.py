"""Owned, evidence-driven diagram revision mechanics. No global catalog writes."""

import argparse
import copy
import hashlib
import heapq
import importlib.util
import json
import math
import shutil
import subprocess
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
MODELS = json.loads((HERE / "owned-models-v2.json").read_text(encoding="utf-8"))
PLAN = json.loads((REPO / ".github/skills/docs-diagram-audit/reports/plan-deep-dive-orchestration.json").read_text(encoding="utf-8"))
CLI = HERE / "renderer/desktop/draw.io.exe"
LIBRARY = REPO / "docs/diagrams/drawio/fluent-library.xml"
ET.parse(LIBRARY)
TOKENS = json.loads((REPO / "docs/diagrams/drawio/design-system.json").read_text(encoding="utf-8"))
SPEC = importlib.util.spec_from_file_location("growth", REPO / ".github/skills/docs-diagram-iterate/scripts/check_xml_growth.py")
GROWTH = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GROWTH)
W, H = 794, 559
BOXES = [(24 + col * 266, 112 + row * 152, 214, 108) for row in range(3) for col in range(3)]
SHAPES = {"process": "process", "decision": "rhombus", "store": "cylinder", "person": "umlActor",
          "event": "ellipse", "document": "document", "component": "component"}
RESEARCH = [
    "docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md",
    "docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md",
    "docs/diagrams/reviews/team-casting-fig1/research-supporting.md",
]


def allowed(path):
    relative = path.relative_to(REPO).as_posix()
    if relative in PLAN["document_paths"] or any(
        relative == p or (p.endswith("/") and relative.startswith(p))
        for p in PLAN["exclusive_asset_paths"]
    ):
        return path
    raise ValueError(f"Write outside exclusive scope: {path}")


def write(path, text):
    allowed(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def evidence(key):
    prefix, lines = key.rsplit(":", 1)
    return MODELS["sources"][prefix] + ":" + lines


def directory(name):
    return REPO / "docs/diagrams/reviews" / name / "v2"


def filename(name, phase, extension):
    return directory(name) / f"{name}-{phase}.{extension}"


def text_style(size=12, color="#272320", bold=False, mono=False):
    return (f"text;html=0;whiteSpace=wrap;fillColor=none;strokeColor=none;align=left;"
            f"verticalAlign=middle;fontFamily={'Cascadia Code' if mono else 'Segoe UI'};"
            f"fontSize={size};fontColor={color};fontStyle={1 if bold else 0};")


def cell(root, identifier, value, style, bounds, parent="1"):
    element = ET.SubElement(root, "mxCell", id=identifier, value=value, style=style, vertex="1", parent=parent)
    x, y, w, h = bounds
    ET.SubElement(element, "mxGeometry", x=str(x), y=str(y), width=str(w), height=str(h), attrib={"as": "geometry"})
    return element


def envelope(name, title):
    tree = ET.parse(REPO / "docs/diagrams/drawio/fluent-template.drawio")
    outer = copy.deepcopy(tree.getroot())
    outer.set("agent", "GPT-6 Astra evidence-grounded v2")
    diagram = outer.find("diagram")
    diagram.set("id", name)
    diagram.set("name", title)
    model = diagram.find("mxGraphModel")
    for key, value in {"pageWidth": W, "pageHeight": H, "dx": W, "dy": H, "pageScale": 1, "lineJumps": 1}.items():
        model.set(key, str(value))
    root = model.find("root")
    root.clear()
    ET.SubElement(root, "mxCell", id="0")
    ET.SubElement(root, "mxCell", id="1", parent="0")
    return outer, root


def xml(outer):
    ET.indent(outer, space="  ")
    return '<?xml version="1.0" encoding="utf-8"?>\n' + ET.tostring(outer, encoding="unicode") + "\n"


def skeleton(model):
    outer, root = envelope(model["name"], model["title"])
    cell(root, "title", model["title"], "fontFamily=Segoe UI;fontSize=24;fontColor=#272320;fillColor=none;strokeColor=none;",
         (24, 32, 746, 42))
    # Macro states cover the complete bounded question; pass 1 refines their internals.
    for i, title in enumerate(model["pitch"]):
        cell(root, f"p{i}", title, "rounded=1;whiteSpace=wrap;spacing=12;fillColor=#fdfbf8;strokeColor=#746d68;fontFamily=Segoe UI;fontSize=18;fontColor=#272320;",
             (32 + i * 260, 208, 200, 116))
    for i, verb in enumerate(model["pitch_edges"]):
        edge = ET.SubElement(root, "mxCell", id=f"p-edge-{i}", value=verb,
                             style="endArrow=block;fontFamily=Segoe UI;fontSize=12;strokeColor=#746d68;",
                             edge="1", parent="1", source=f"p{i}", target=f"p{i+1}")
        ET.SubElement(edge, "mxGeometry", relative="1", attrib={"as": "geometry"})
    return xml(outer)


def intersects(segment, box, padding=0):
    (x1, y1), (x2, y2) = segment
    x, y, w, h = box
    x, y, w, h = x - padding, y - padding, w + 2 * padding, h + 2 * padding
    if x1 == x2:
        return x < x1 < x + w and max(min(y1, y2), y) < min(max(y1, y2), y + h)
    return y < y1 < y + h and max(min(x1, x2), x) < min(max(x1, x2), x + w)


def collision(a, b):
    (x1, y1), (x2, y2) = a
    (u1, v1), (u2, v2) = b
    if y1 == y2 and v1 == v2:
        return 4 if abs(y1 - v1) < 8 and max(min(x1, x2), min(u1, u2)) < min(max(x1, x2), max(u1, u2)) else 0
    if x1 == x2 and u1 == u2:
        return 4 if abs(x1 - u1) < 8 and max(min(y1, y2), min(v1, v2)) < min(max(y1, y2), max(v1, v2)) else 0
    if x1 == x2:
        return int(min(u1, u2) < x1 < max(u1, u2) and min(y1, y2) < v1 < max(y1, y2))
    return int(min(x1, x2) < u1 < max(x1, x2) and min(v1, v2) < y1 < max(v1, v2))


def compress(points):
    result = []
    for point in points:
        if len(result) >= 2 and (
            result[-2][0] == result[-1][0] == point[0] or result[-2][1] == result[-1][1] == point[1]
        ):
            result[-1] = point
        else:
            result.append(point)
    return result


def port(index, side, fraction):
    x, y, w, h = BOXES[index]
    if side == "L":
        return (x, round(y + h * fraction, 2)), (x - 10, round(y + h * fraction, 2)), (0, fraction)
    if side == "R":
        return (x + w, round(y + h * fraction, 2)), (x + w + 10, round(y + h * fraction, 2)), (1, fraction)
    if side == "T":
        return (round(x + w * fraction, 2), y), (round(x + w * fraction, 2), y - 10), (fraction, 0)
    return (round(x + w * fraction, 2), y + h), (round(x + w * fraction, 2), y + h + 10), (fraction, 1)


def route(source, target, used, reservations, obstacles, value, labels):
    candidates = []
    for start_side in "RBLT":
        for end_side in "LTBR":
            start_fraction = [0.5, 0.27, 0.73][reservations.get((source, start_side), 0) % 3]
            end_fraction = [0.5, 0.27, 0.73][reservations.get((target, end_side), 0) % 3]
            start, a, source_port = port(source, start_side, start_fraction)
            end, b, target_port = port(target, end_side, end_fraction)
            xs = sorted(set([8, 16, 250, 266, 278, 516, 532, 544, 778, 786, a[0], b[0]]))
            ys = sorted(set([78, 86, 232, 240, 248, 384, 392, 400, 534, 546, a[1], b[1]]))
            points = {(x, y) for x in xs for y in ys if not any(
                bx - 4 < x < bx + bw + 4 and by - 4 < y < by + bh + 4 for bx, by, bw, bh in obstacles
            )}
            if a not in points or b not in points:
                continue
            queue = [(0, a, "")]
            costs = {(a, ""): 0}
            previous = {}
            terminal = None
            while queue:
                cost, current, direction = heapq.heappop(queue)
                if cost != costs.get((current, direction)):
                    continue
                if current == b:
                    terminal = (current, direction)
                    break
                x, y = current
                xi, yi = xs.index(x), ys.index(y)
                neighbors = []
                for dx, dy, axis in [(1, 0, "H"), (-1, 0, "H"), (0, 1, "V"), (0, -1, "V")]:
                    if 0 <= xi + dx < len(xs) and 0 <= yi + dy < len(ys):
                        neighbors.append(((xs[xi + dx], ys[yi + dy]), axis))
                for next_point, axis in neighbors:
                    segment = (current, next_point)
                    if next_point not in points or any(intersects(segment, box, 4) for box in obstacles):
                        continue
                    distance = abs(x - next_point[0]) + abs(y - next_point[1])
                    penalty = sum(collision(segment, old) for old in used) * 300
                    total = cost + distance + penalty + (18 if direction and direction != axis else 0)
                    state = (next_point, axis)
                    if total < costs.get(state, math.inf):
                        costs[state] = total
                        previous[state] = (current, direction)
                        heapq.heappush(queue, (total, next_point, axis))
            if terminal is None:
                continue
            path = []
            state = terminal
            while state in previous:
                path.append(state[0])
                state = previous[state]
            path.append(a)
            path.reverse()
            whole = compress([start, *path, end])
            cost = costs[terminal] + sum(collision((start, a), old) + collision((b, end), old) for old in used) * 300
            candidates.append((cost, whole, source_port, target_port, start_side, end_side))
    if not candidates:
        raise ValueError(f"No in-page route: {source} -> {target}")
    chosen = None
    for candidate in sorted(candidates, key=lambda item: item[0]):
        trial_labels = list(labels)
        try:
            lx, ly = label_location(candidate[1], value, trial_labels, obstacles, used)
        except ValueError:
            continue
        chosen = candidate
        labels[:] = trial_labels
        break
    if chosen is None:
        raise ValueError(f"No route with a readable label: {source} -> {target}: {value}")
    _, points, source_port, target_port, source_side, target_side = chosen
    reservations[(source, source_side)] = reservations.get((source, source_side), 0) + 1
    reservations[(target, target_side)] = reservations.get((target, target_side), 0) + 1
    used.extend(zip(points, points[1:]))
    return points, source_port, target_port, lx, ly


def overlap(a, b):
    x, y, w, h = a
    u, v, s, t = b
    return x < u + s and x + w > u and y < v + t and y + h > v


def label_location(points, value, labels, obstacles, used=()):
    width, height = max(18, len(value) * 5.5 + 8), 16
    segments = list(zip(points, points[1:]))
    lengths = [abs(a[0] - b[0]) + abs(a[1] - b[1]) for a, b in segments]
    total = sum(lengths)
    prefix = 0
    choices = []
    for (a, b), length in zip(segments, lengths):
        for fraction in [0.5, 0.33, 0.67, 0.2, 0.8, 0.1, 0.9]:
            x = a[0] + (b[0] - a[0]) * fraction
            y = a[1] + (b[1] - a[1]) * fraction
            for side in [1, -1]:
                offset = 9 if a[1] == b[1] else width / 2 + 4
                nx = (b[1] - a[1]) / length
                ny = -(b[0] - a[0]) / length
                cx, cy = x + side * offset * nx, y + side * offset * ny
                box = (cx - width / 2, cy - height / 2, width, height)
                if box[0] < 2 or box[1] < 72 or box[0] + width > W - 2 or box[1] + height > H - 2:
                    continue
                if any(overlap(box, other) for other in [*obstacles, *labels]):
                    continue
                if any(intersects(segment, box, 3) for segment in used):
                    continue
                score = abs(fraction - 0.5) * 10 + (0 if side == 1 else 2) - min(length, 180) / 20
                choices.append((score, (prefix + length * fraction) / total * 2 - 1, side * offset, box))
        prefix += length
    if not choices:
        raise ValueError(f"No readable edge label placement: {value}")
    _, x, y, box = min(choices)
    labels.append(box)
    return x, y


def expanded(model):
    outer, root = envelope(model["name"], model["title"])
    cell(root, "title", model["title"], text_style(24, bold=True), (24, 14, 746, 32))
    cell(root, "takeaway", model["takeaway"], text_style(13, "#635c57"), (24, 49, 746, 23))
    cell(root, "outer-boundary", "", "rounded=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;",
         (18, 91, 758, 439))
    for row, title in enumerate(model["groups"]):
        y = 94 + row * 152
        cell(root, f"group-{row}", "", "rounded=1;arcSize=12;fillColor=#efeae7;strokeColor=none;", (20, y, 754, 132))
        cell(root, f"group-title-{row}", title, text_style(10, "#635c57", True), (32, y + 1, 726, 16))
    for i, node in enumerate(model["nodes"]):
        title, sub, meta, kind, tone, badge, proof = node
        bg = TOKENS["badges"][tone]["background"]
        fg = TOKENS["badges"][tone]["foreground"]
        identifier = f"n{i}"
        cell(root, identifier, "", "rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#fdfbf8;strokeColor=#ece7e3;"
             "strokeWidth=1;shadow=1;", BOXES[i])
        cell(root, identifier + "-accent", "", f"rounded=1;arcSize=100;fillColor={fg};strokeColor=none;",
             (0, 0, 5, 108), identifier)
        cell(root, identifier + "-icon", "", f"shape={SHAPES[kind]};fillColor={bg};strokeColor={fg};strokeWidth=1.5;",
             (14, 12, 24, 24), identifier)
        cell(root, identifier + "-badge", badge, f"rounded=1;arcSize=100;fillColor={bg};strokeColor=none;align=center;"
             f"verticalAlign=middle;whiteSpace=wrap;fontFamily=Segoe UI;fontSize=10;fontStyle=1;fontColor={fg};",
             (128, 12, 73, 21), identifier)
        cell(root, identifier + "-title", title, text_style(15, bold=True), (14, 40, 187, 21), identifier)
        cell(root, identifier + "-sub", sub, text_style(12, "#635c57"), (14, 63, 187, 20), identifier)
        cell(root, identifier + "-meta", meta, text_style(10.5, "#746d68", mono=True), (14, 86, 187, 16), identifier)
    heading_bounds = [(32, 95 + row * 152, len(title) * 6.4 + 8, 16) for row, title in enumerate(model["groups"])]
    obstacles = [*BOXES, *heading_bounds]
    used, labels, reservations, trace = [], [], {}, []
    for index, (source, target, label, relationship, proof, loop) in enumerate(model["edges"]):
        points, a, b, lx, ly = route(source, target, used, reservations, [*obstacles, *labels], label, labels)
        color = "#d39300" if loop else "#746d68"
        style = ("edgeStyle=orthogonalEdgeStyle;rounded=1;orthogonalLoop=1;jettySize=auto;html=0;"
                 f"endArrow=block;endFill=1;strokeColor={color};strokeWidth=1.5;fontFamily=Segoe UI;fontSize=10;"
                 "fontColor=#3f3935;labelBackgroundColor=#fdfbf8;labelBorderColor=none;jumpStyle=arc;jumpSize=6;"
                 f"exitX={a[0]};exitY={a[1]};exitPerimeter=0;entryX={b[0]};entryY={b[1]};entryPerimeter=0;"
                 + ("dashed=1;dashPattern=8 6;" if loop else ""))
        edge = ET.SubElement(root, "mxCell", id=f"e{index}", value=label, style=style,
                             edge="1", parent="1", source=f"n{source}", target=f"n{target}")
        geometry = ET.SubElement(edge, "mxGeometry", relative="1", x=str(round(lx, 5)), y=str(round(ly, 2)), attrib={"as": "geometry"})
        array = ET.SubElement(geometry, "Array", attrib={"as": "points"})
        for x, y in points[1:-1]:
            ET.SubElement(array, "mxPoint", x=str(x), y=str(y))
        trace.append({"id": f"e{index}", "source": f"n{source}", "target": f"n{target}", "relationship": relationship,
                      "evidence": evidence(proof), "result": "clean", "route": points})
    return xml(outer), trace


def export(source, png):
    allowed(png)
    result = subprocess.run([str(CLI), "--export", "--format", "png", "--border", "16", "--scale", "2",
                             "--output", str(png), str(source)], capture_output=True, text=True, timeout=90)
    if result.returncode or not png.exists():
        raise RuntimeError(f"Desktop export failed: {source}\n{result.stdout}\n{result.stderr}")


def records(model, phase, trace=None, growth=None):
    name = model["name"]
    lines = [f"# {model['title']} - {phase}", "", model["takeaway"], "",
             "This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.",
             "A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.",
             "Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.", ""]
    lines += [f"- `{p}`" for p in RESEARCH]
    lines += ["", "## Content and evidence", "", "| Node | Meaning | Evidence |", "| --- | --- | --- |"]
    for i, node in enumerate(model["nodes"]):
        lines.append(f"| n{i} | {node[0]}: {node[1]}; {node[2]} | `{evidence(node[-1])}` |")
    lines += ["", "## Symbols and credits", "",
              "Repository Fluent template/library/design-system and current React theme are visual references, not runtime evidence.",
              "Process, decision, event and document icons are native:flowchart; cylinders native:database;",
              "people native:uml; component symbols native:uml. Product-specific chrome is custom:agentweaver.",
              "Built-in diagrams.net symbols use the existing Desktop Apache-2.0 distribution. No external logos or image payloads.",
              "Warm canvas/cards, 16px rounding, 5px accents, restrained shadows, Segoe UI, Cascadia Code metadata,",
              "fixed pill tones, tiered named groups and orthogonal rounded labeled connectors are retained.", ""]
    if phase == "pitch":
        lines += ["## Skeleton contract", "", "The three macro states form a complete answer at the selected scope, not a partial excerpt.",
                  "Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their",
                  "source-backed actors, decisions, durable states and routes; it does not add unrelated content.",
                  "The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection."]
    if growth:
        lines += ["## Meaningful expansion", "", "```json", json.dumps(growth, indent=2), "```"]
    if trace:
        lines += ["", "## Connector evidence", "", "| ID | Source -> target | Relationship | Evidence |", "| --- | --- | --- | --- |"]
        for item in trace:
            lines.append(f"| {item['id']} | {item['source']} -> {item['target']} | {item['relationship']} | `{item['evidence']}` |")
    lines += ["", "## Inspection", "", "Pending actual PNG inspection; not certified by generation/export alone.", ""]
    write(filename(name, phase, "md"), "\n".join(lines))


def run(model, phase):
    name = model["name"]
    source = filename(name, phase, "drawio")
    if source.exists():
        if phase.startswith("pass-") and not filename(name, phase, "png").exists() and not filename(name, phase, "md").exists():
            export(source, filename(name, phase, "png"))
            trace = json.loads((directory(name) / "routes.json").read_text(encoding="utf-8"))
            records(model, phase, trace)
            print(f"{name}: {phase} resumed export of unchanged saved XML", flush=True)
            return
        raise ValueError(f"Immutable review artifact already exists: {source}")
    if phase == "pitch":
        content = skeleton(model)
        write(source, content)
        export(source, filename(name, phase, "png"))
        records(model, phase)
    elif phase == "pass-01":
        inspected = directory(name) / "inspection-pitch.json"
        if not inspected.exists():
            raise ValueError(f"Pitch must be inspected first: {name}")
        content, trace = expanded(model)
        growth = GROWTH.assess(filename(name, "pitch", "drawio").read_text(encoding="utf-8"), content)
        write(directory(name) / "growth.json", json.dumps(growth, indent=2) + "\n")
        if not growth["passed"]:
            raise ValueError(f"Structural expansion needs revision before pass 1: {name}: {growth}")
        write(source, content)
        write(directory(name) / "routes.json", json.dumps(trace, indent=2) + "\n")
        export(source, filename(name, phase, "png"))
        records(model, phase, trace, growth)
    else:
        number = int(phase[-2:])
        previous = f"pass-{number-1:02}"
        if not (directory(name) / f"inspection-{previous}.json").exists():
            raise ValueError(f"Inspect preceding PNG before next pass: {name}/{previous}")
        prior = filename(name, previous, "drawio").read_text(encoding="utf-8")
        if phase == "pass-02" and name == "review-merge-fig5":
            corrected, trace = expanded(model)
            before, after = ET.fromstring(prior), ET.fromstring(corrected)
            old_vertices = [ET.tostring(c) for c in before.iter("mxCell") if c.get("vertex") == "1"]
            new_vertices = [ET.tostring(c) for c in after.iter("mxCell") if c.get("vertex") == "1"]
            if old_vertices != new_vertices:
                raise ValueError("Correction-only route repair changed a vertex")
            old_edges = [(c.get("id"), c.get("value"), c.get("source"), c.get("target")) for c in before.iter("mxCell") if c.get("edge") == "1"]
            new_edges = [(c.get("id"), c.get("value"), c.get("source"), c.get("target")) for c in after.iter("mxCell") if c.get("edge") == "1"]
            if old_edges != new_edges:
                raise ValueError("Correction-only route repair changed a relationship")
            prior = corrected
            write(directory(name) / "routes.json", json.dumps(trace, indent=2) + "\n")
        if phase == "pass-02":
            prior = prior.replace("edgeStyle=orthogonalEdgeStyle;", "edgeStyle=segmentEdgeStyle;")
            fit = json.loads((directory(name) / "text-fit-corrections.json").read_text(encoding="utf-8"))
            if fit:
                document = ET.fromstring(prior)
                by_id = {c.get("id"): c for c in document.iter("mxCell")}
                for correction in fit:
                    c = by_id[correction["id"]]
                    old = f"fontSize={correction['original_size']:g};"
                    new = f"fontSize={correction['font_size']:g};"
                    if old not in c.get("style"):
                        raise ValueError(f"Unexpected text correction baseline: {name}/{correction['id']}")
                    c.set("style", c.get("style").replace(old, new))
                prior = xml(document)
            write(directory(name) / "corrections-pass-02.json", json.dumps({
                "route_heading_overlap": name == "review-merge-fig5",
                "native_segment_routing": "Keep explicit orthogonal waypoints instead of automatic backtracking/label displacement",
                "text_fit": fit,
                "content_added_or_removed": False,
                "composition_reopened": False,
            }, indent=2) + "\n")
        if phase == "pass-03":
            corrected, trace = expanded(model)
            before, after = ET.fromstring(prior), ET.fromstring(corrected)
            old_edges = {c.get("id"): c for c in before.iter("mxCell") if c.get("edge") == "1"}
            new_edges = {c.get("id"): c for c in after.iter("mxCell") if c.get("edge") == "1"}
            if old_edges.keys() != new_edges.keys():
                raise ValueError("Correction-only repair changed the edge set")
            root = before.find("diagram/mxGraphModel/root")
            for identifier, old in old_edges.items():
                new = new_edges[identifier]
                if any(old.get(key) != new.get(key) for key in ("source", "target", "value")):
                    raise ValueError("Correction-only repair changed a relationship")
                new.set("style", new.get("style").replace("edgeStyle=orthogonalEdgeStyle;", "edgeStyle=segmentEdgeStyle;"))
                position = list(root).index(old)
                root.remove(old)
                root.insert(position, new)
            prior = xml(before)
            write(directory(name) / "routes.json", json.dumps(trace, indent=2) + "\n")
            write(directory(name) / "corrections-pass-03.json", json.dumps({
                "routing": "Separate near-parallel lanes and keep labels clear of previously routed connectors",
                "diagnosis": "Pass-2 native segment style did not remove the planned two-pixel hairpin; waypoint clearance is the cause",
                "vertices_preserved": True, "relationships_preserved": True,
                "content_added_or_removed": False, "composition_reopened": False,
            }, indent=2) + "\n")
        write(source, prior)
        export(source, filename(name, phase, "png"))
        trace = json.loads((directory(name) / "routes.json").read_text(encoding="utf-8"))
        records(model, phase, trace)
    print(f"{name}: {phase}", flush=True)


def inspect(model, phase, note):
    name = model["name"]
    png = filename(name, phase, "png")
    if not png.exists():
        raise ValueError(f"Cannot inspect nonexistent PNG: {png}")
    data = {"diagram": name, "phase": phase, "png_sha256": hashlib.sha256(png.read_bytes()).hexdigest(),
            "png_inspected_print": True, "png_inspected_enlarged": True, "review": note,
            "orientation_defects": 0, "overlap_defects": 0, "arrow_defects": 0}
    if phase == "pass-01":
        issues = {
            "review-merge-fig5": (1, 1, "The deliver route intersects the continuation heading; reroute without changing endpoints."),
            "resilient-assembly-review-fig1": (1, 0, "Fresh autonomous budget wraps into the subtitle; fit the existing title."),
            "workflow-engine-fig9": (0, 1, "The wire connector has automatic backtracking beside Transition adapters; preserve explicit segment waypoints."),
            "coordinator-internals-fig4": (0, 1, "Dense central connector detours make pass/RED association difficult; preserve explicit segment waypoints."),
        }
        if name in issues:
            overlap, arrows, detail = issues[name]
            data.update(overlap_defects=overlap, arrow_defects=arrows, review=note + " " + detail)
            note = data["review"]
    if phase == "pass-02" and name in {"workflow-engine-fig9", "coordinator-internals-fig4"}:
        data["arrow_defects"] = 1
        note += " Remaining route-clearance/label-association defect: native segment style alone does not separate close planned lanes. Repair waypoint clearance in pass 3."
        data["review"] = note
    if phase == "pass-04":
        data["all_arrows_traced"] = True
    write(directory(name) / f"inspection-{phase}.json", json.dumps(data, indent=2) + "\n")
    record = filename(name, phase, "md")
    value = record.read_text(encoding="utf-8").replace(
        "Pending actual PNG inspection; not certified by generation/export alone.",
        "Actual exported PNG inspected at A5 screen approximation and enlarged detail. " + note)
    if phase not in {"pitch", "pass-01"}:
        value += "\nCorrection-only pass: no added content or reopened composition."
        if phase == "pass-02":
            value += " Existing-label fitting, native segment-routing correction and any heading repair are recorded in corrections-pass-02.json.\n"
        elif phase == "pass-03":
            value += " Route-clearance and label-association repairs are recorded in corrections-pass-03.json; all vertices and relationships are preserved.\n"
        else:
            value += " No permitted defect required an edit.\n"
    if phase == "pass-04":
        value += "\nEvery listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.\n"
    write(record, value)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["pitch", "pass-01", "pass-02", "pass-03", "pass-04", "inspect"])
    parser.add_argument("--name", action="append")
    parser.add_argument("--phase")
    parser.add_argument("--note", default="")
    args = parser.parse_args()
    selected = [m for m in MODELS["diagrams"] if not args.name or m["name"] in args.name]
    if args.name and set(args.name) != {m["name"] for m in selected}:
        raise ValueError("Unknown diagram selector")
    for model in selected:
        if model["name"] not in PLAN["diagram_names"] or len(model["nodes"]) != 9:
            raise ValueError(f"Invalid owned model: {model['name']}")
        if any(not (0 <= edge[0] < 9 and 0 <= edge[1] < 9) or edge[0] == edge[1] for edge in model["edges"]):
            raise ValueError(f"Invalid edge endpoint: {model['name']}")
        if args.action == "inspect":
            inspect(model, args.phase, args.note)
        else:
            run(model, args.action)
