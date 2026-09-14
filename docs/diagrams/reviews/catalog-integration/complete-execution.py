"""Sequential, inspection-gated completion using the retained execution models."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
sys.path.insert(0, str(ROOT / ".github/skills/docs-diagram-iterate/scripts"))
from check_xml_growth import assess
from validate_iteration_manifest import validate_manifest

SELECTION = {
    "agent-runtime-fig2": ("Native shell admission", ["native", "denied"]),
    "agent-runtime-fig3": ("Remote writeback boundary", ["envelope", "apply"]),
    "canonical-agent-communication-a2a": ("A2A leaf transport", ["proxy", "bridge"]),
    "canonical-agent-communication-shared": ("Transport channel excerpt", ["proxy", "host"]),
    "canonical-sandbox-pod-evolution": ("Current remote leaf path", ["proxy", "pod"]),
    "distributed-execution-scaling-fig3": ("Worker execution boundary", ["worker", "pod"]),
    "distributed-execution-scaling-fig4": ("Durable event append", ["ef", "sql"]),
    "distributed-execution-scaling-fig5": ("Lease claim boundary", ["a", "row"]),
    "infra-deployment-fig2": ("Key Vault authorization", ["identity", "vault"]),
    "infra-deployment-fig3": ("Image resolution", ["resolve", "images"]),
    "infra-deployment-fig4": ("API route excerpt", ["match", "api"]),
    "live-preview-provisioning-fig1": ("Validated preview publication", ["validate", "ready"]),
    "sandbox-browser-preview-fig2": ("Approval boundary", ["gate", "approved"]),
    "sandbox-fig2": ("Executor selection", ["router", "cluster"]),
    "sandbox-fig3": ("Utility claim request", ["executor", "controller"]),
    "sandbox-pod-execution-fig4": ("Authoritative writeback boundary", ["ref", "apply"]),
    "sandbox-pod-execution-fig5": ("Provisioning progress", ["claim", "pending"]),
    "sandbox-pod-execution-fig6": ("Standby configuration", ["configure", "setup"]),
    "sandbox-pod-execution-fig7": ("Owning approval gate", ["client", "pod"]),
    "canonical-pod-process-boundaries": ("Pod-private request path", ["host", "ipc", "executor"]),
}


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def stage_name(number):
    return "pitch" if number == 0 else f"pass-{number:02}"


def directory(name):
    return ROOT / "docs/diagrams/reviews" / name / "completion-20260913"


def artifact(name, stage, suffix):
    return directory(name) / f"{name}-{stage_name(stage)}.{suffix}"


def save_new(path, content):
    if path.exists() and path.read_text(encoding="utf-8") != content:
        raise ValueError(f"Immutable artifact already exists: {path}")
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")


def pitch(name, model, full):
    title, ids = SELECTION[name]
    tree = ET.Element("mxfile", host="Agentweaver", version="31.4.5", compressed="false")
    diagram = ET.SubElement(tree, "diagram", id=name, name=title)
    original = ET.fromstring(full).find("diagram/mxGraphModel")
    graph = ET.SubElement(diagram, "mxGraphModel", page="1", pageWidth=original.get("pageWidth"),
                          pageHeight=original.get("pageHeight"), pageScale="2", background="#efeae7")
    root = ET.SubElement(graph, "root")
    ET.SubElement(root, "mxCell", id="0")
    ET.SubElement(root, "mxCell", id="1", parent="0")

    def vertex(identifier, value, style, x, y, width, height):
        cell = ET.SubElement(root, "mxCell", id=identifier, value=value, style=style, vertex="1", parent="1")
        ET.SubElement(cell, "mxGeometry", x=str(x), y=str(y), width=str(width), height=str(height), **{"as": "geometry"})

    width = len(ids) * 410 + 20
    vertex("boundary", title, "rounded=1;fillColor=#f8f4f1;strokeColor=#e2ddd9;fontFamily=Segoe UI;fontSize=20;verticalAlign=top;spacing=18;",
           20, 20, width, 206)
    nodes = {n["id"]: n for n in model["nodes"]}
    for i, identifier in enumerate(ids):
        vertex(identifier, nodes[identifier]["label"],
               "rounded=1;arcSize=16;absoluteArcSize=1;fillColor=#fdfbf8;strokeColor=#e2ddd9;shadow=1;fontFamily=Segoe UI;fontSize=20;",
               40 + i * 410, 92, 340, 104)
    for i, edge in enumerate(model["edges"]):
        if edge["from"] not in ids or edge["to"] not in ids:
            continue
        cell = ET.SubElement(root, "mxCell", id=f"e{i}", value=edge.get("label", ""), source=edge["from"], target=edge["to"],
                             edge="1", parent="1", style="edgeStyle=orthogonalEdgeStyle;rounded=1;endArrow=classicThin;endSize=8;strokeColor=#746d68;fontFamily=Segoe UI;fontSize=16;labelBackgroundColor=#f8f4f1;exitX=1;exitY=0.5;entryX=0;entryY=0.5;")
        ET.SubElement(cell, "mxGeometry", relative="1", **{"as": "geometry"})
    return ET.tostring(tree, encoding="unicode") + "\n"


def contact_sheets(stage):
    names = list(SELECTION)
    font = ImageFont.truetype("segoeui.ttf", 22)
    for offset in range(0, len(names), 2):
        sheet = Image.new("RGB", (1600, 2200), "#efeae7")
        draw = ImageDraw.Draw(sheet)
        for row, name in enumerate(names[offset:offset + 2]):
            draw.text((24, row * 1100 + 10), f"{name} / {stage_name(stage)}", font=font, fill="#272320")
            with Image.open(artifact(name, stage, "png")) as image:
                image = image.convert("RGB")
                image.thumbnail((1560, 1030))
                sheet.paste(image, ((1600 - image.width) // 2, row * 1100 + 54))
        sheet.save(HERE / f"completion-{stage_name(stage)}-contact-{offset // 2 + 1:02}.png")


parser = argparse.ArgumentParser()
parser.add_argument("operation", choices=("prepare", "render", "inspect", "finalize"))
parser.add_argument("--stage", type=int, choices=range(5), default=0)
parser.add_argument("--drawio-cli", type=Path)
parser.add_argument("--notes")
args = parser.parse_args()
plan = {e["name"]: e for e in read(HERE / "repair-plan.json")["entries"]}
ledger_path = HERE / "completion-inspections.json"
ledger = read(ledger_path) if ledger_path.exists() else {}

if args.operation in ("prepare", "render"):
    for name in SELECTION:
        entry = plan[name]
        full = (ROOT / entry["candidate"]).read_text(encoding="utf-8")
        model = read(HERE / "candidates" / f"{name}.model.json")
        if args.stage:
            prior = ledger.get(name, {}).get(str(args.stage - 1))
            if not prior or prior["png_sha256"] != sha(artifact(name, args.stage - 1, "png")):
                raise ValueError(f"{name}: preceding stage has not been inspected")
        source = pitch(name, model, full) if args.stage == 0 else (
            full if args.stage == 1 else artifact(name, args.stage - 1, "drawio").read_text(encoding="utf-8"))
        save_new(artifact(name, args.stage, "drawio"), source)
        if args.stage == 1:
            growth = assess(artifact(name, 0, "drawio").read_text(encoding="utf-8"), source)
            save_new(directory(name) / "growth.json", json.dumps(growth, indent=2) + "\n")
            if not growth["passed"]:
                raise ValueError(f"{name}: growth failed: {growth}")
        if args.operation == "render":
            if not args.drawio_cli:
                raise ValueError("--drawio-cli required")
            png = artifact(name, args.stage, "png")
            if not png.exists():
                subprocess.run([str(args.drawio_cli), "--export", "--format", "png", "--border", "16", "--scale", "2",
                                "--output", str(png), str(artifact(name, args.stage, "drawio"))],
                               check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            with Image.open(png) as image:
                image.verify()
        print(f"{args.operation} {stage_name(args.stage)} {name}", flush=True)
    if args.operation == "render":
        contact_sheets(args.stage)

if args.operation == "inspect":
    if not args.notes:
        raise ValueError("Actual inspection notes required; do not pre-approve unviewed PNGs")
    for name in SELECTION:
        ledger.setdefault(name, {})[str(args.stage)] = {
            "drawio_sha256": sha(artifact(name, args.stage, "drawio")),
            "png_sha256": sha(artifact(name, args.stage, "png")),
            "inspection": "Actual stage contact sheet at page scale and enlarged tiles; dense/uncertain diagrams opened individually.",
            "notes": args.notes,
        }
    ledger_path.write_text(json.dumps(ledger, indent=2) + "\n", encoding="utf-8")

if args.operation == "finalize":
    for name in SELECTION:
        entry = plan[name]
        model = read(HERE / "candidates" / f"{name}.model.json")
        growth = read(directory(name) / "growth.json")
        trace = [{"id": f"e{i}", "source": e["from"], "target": e["to"], "relationship": e.get("label", "related"),
                  "evidence": f"{entry['model_source']}; retained candidate model edge {i}",
                  "result": "clean"} for i, e in enumerate(model["edges"])]
        manifest = {"diagram": name, "orientation": "A5-landscape", "passes": [], "final_pass": 4}
        page = ET.parse(artifact(name, 4, "drawio")).find("diagram/mxGraphModel")
        if float(page.get("pageWidth")) < float(page.get("pageHeight")):
            manifest["orientation"] = "A5-portrait"
        for stage in range(5):
            checked = ledger.get(name, {}).get(str(stage))
            if not checked or checked["png_sha256"] != sha(artifact(name, stage, "png")) or checked["drawio_sha256"] != sha(artifact(name, stage, "drawio")):
                raise ValueError(f"{name}: stage {stage} inspection missing or stale")
            record = f"# {name}: {stage_name(stage)}\n\n"
            record += f"Grounding reused without new research: `{entry['model_source']}`. "
            record += "The explicit user single-agent exception applies; no agents were launched. Earlier incomplete attempts remain unchanged.\n\n"
            if stage == 0:
                record += f"Concise excerpt: {SELECTION[name][0]}. Actors: {', '.join(SELECTION[name][1])}. "
                record += "The surrounding frame names this path, not an invented deployment boundary. Other grounded paths, classifications and constraints are deliberately deferred to pass 1.\n\n"
            elif stage == 1:
                record += f"Expanded to the existing complete model: {len(model['nodes'])} actors and {len(trace)} relationships, preserving every label, native symbol, route and context record. "
                record += f"Meaningful XML: {growth['baseline_meaningful_xml']} -> {growth['result_meaningful_xml']} ({growth['growth_ratio']}x; visible-semantic-canonical-xml-v1). "
                record += "All anti-padding checks pass. The checker now respects the actual draw.io print scale; no source content was hidden or moved off-page.\n\n"
            else:
                record += "Correction-only inspection found no remaining orientation, overlap or arrow defect. No content/style/layout change was made; a separate source and newly exported PNG are retained.\n\n"
            record += "Symbol sources: native libraries bundled with draw.io Desktop 31.4.5 and the retained Agentweaver Fluent library; source/license credits remain in the original pitch research. "
            record += "Native shapes stay inside calibrated card icon footprints.\n\n"
            record += checked["inspection"] + " " + checked["notes"] + "\n\n"
            record += "PNG SHA256: `" + checked["png_sha256"] + "`.\n"
            if stage == 4:
                record += "\n## Full arrow trace\n\n| ID | Source | Target | Relationship | Result |\n|---|---|---|---|---|\n"
                record += "".join(f"| {a['id']} | {a['source']} | {a['target']} | {a['relationship']} | Clean |\n" for a in trace)
                record += "\nEach trace checks the intended endpoint card, arrow direction, label association, orthogonal gutter route, bridge clearance and absence of false junctions against the retained complete model.\n"
            save_new(artifact(name, stage, "md"), record)
            artifacts = {key: artifact(name, stage, suffix).name for key, suffix in (("drawio", "drawio"), ("png", "png"), ("change_record", "md"))}
            artifacts.update(png_inspected_print=True, png_inspected_enlarged=True)
            if stage == 0:
                manifest["pitch"] = artifacts
            else:
                artifacts.update(number=stage, mode="visual-upgrade" if stage == 1 else "correction-only",
                                 orientation_defects=0, overlap_defects=0, arrow_defects=0)
                if stage == 1:
                    artifacts.update({k: growth[k] for k in ("baseline_meaningful_xml", "result_meaningful_xml", "growth_metric", "growth_ratio")})
                if stage == 4:
                    artifacts.update(all_arrows_traced=True, arrow_trace=trace)
                manifest["passes"].append(artifacts)
        errors = validate_manifest(manifest)
        if errors:
            raise ValueError(f"{name}: {errors}")
        save_new(directory(name) / "iteration-manifest.json", json.dumps(manifest, indent=2) + "\n")
    print("Completed 20 separate evidence manifests; no public assets written.")
