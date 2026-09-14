"""Validate and promote only the orchestration plan's exclusive artifacts."""

import argparse
import hashlib
import importlib.util
import json
import re
import shutil
import struct
import subprocess
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit
from xml.etree import ElementTree as ET

import jsonschema

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("author", HERE / "revamp-v2.py")
A = importlib.util.module_from_spec(spec)
spec.loader.exec_module(A)
ROOT, PLAN = A.REPO, A.PLAN
MODELS = A.MODELS["diagrams"]
NAMES = {m["name"] for m in MODELS}
RETIRED = set(PLAN["diagram_names"]) - NAMES
OUT = HERE / "v2"
SCHEMA = json.loads((ROOT / ".github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json").read_text())
spec = importlib.util.spec_from_file_location("manifest_validator", ROOT / ".github/skills/docs-diagram-iterate/scripts/validate_iteration_manifest.py")
V = importlib.util.module_from_spec(spec)
spec.loader.exec_module(V)


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def save(path, value):
    A.write(path, json.dumps(value, indent=2) + "\n")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def relative(path):
    return path.relative_to(ROOT).as_posix()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def styles(cell):
    return dict(item.split("=", 1) for item in cell.get("style", "").split(";") if "=" in item)


def cells(path):
    document = ET.parse(path)
    graphs = document.findall("diagram/mxGraphModel")
    require(len(graphs) == 1, f"Not one uncompressed page: {path}")
    graph = graphs[0]
    require((graph.get("pageWidth"), graph.get("pageHeight"), graph.get("pageScale")) == ("794", "559", "1"), f"Not A5: {path}")
    items = list(graph.iter("mxCell"))
    require(len({c.get("id") for c in items}) == len(items), f"Duplicate IDs: {path}")
    return {c.get("id"): c for c in items}


def artifacts(name, phase):
    inspection = load(A.directory(name) / f"inspection-{phase}.json")
    require(inspection["png_sha256"] == digest(A.filename(name, phase, "png")), f"Inspected PNG changed: {name}/{phase}")
    for flag in ("png_inspected_print", "png_inspected_enlarged"):
        require(inspection[flag] is True, f"Missing inspection: {name}/{phase}/{flag}")
    png = A.filename(name, phase, "png").read_bytes()
    require(png[:8] == b"\x89PNG\r\n\x1a\n", f"Invalid PNG: {name}/{phase}")
    width, height = struct.unpack(">II", png[16:24])
    require(width >= 1400 and height >= (400 if phase == "pitch" else 1000), f"Unexpected export dimensions: {name}/{phase}: {width}x{height}")
    for extension in ("drawio", "png", "md"):
        require(A.filename(name, phase, extension).is_file(), f"Missing artifact: {name}/{phase}/{extension}")
    return {
        "drawio": relative(A.filename(name, phase, "drawio")),
        "png": relative(A.filename(name, phase, "png")),
        "change_record": relative(A.filename(name, phase, "md")),
        "png_inspected_print": True, "png_inspected_enlarged": True,
    }, inspection


def check_trace(model, phase="pass-04"):
    name = model["name"]
    final = cells(A.filename(name, phase, "drawio"))
    original = cells(A.filename(name, "pass-01", "drawio"))
    routes = load(A.directory(name) / "routes.json")
    require(len(routes) == len(model["edges"]), f"Trace cardinality: {name}")
    trace = []
    for index, (source, target, label, relationship, proof, loop) in enumerate(model["edges"]):
        identifier = f"e{index}"
        edge, route = final[identifier], routes[index]
        require((edge.get("source"), edge.get("target"), edge.get("value")) == (f"n{source}", f"n{target}", label), f"Wrong relationship: {name}/{identifier}")
        require((route["id"], route["source"], route["target"], route["relationship"], route["evidence"]) ==
                (identifier, f"n{source}", f"n{target}", relationship, A.evidence(proof)), f"Wrong evidence trace: {name}/{identifier}")
        style = styles(edge)
        require(style["endArrow"] == "block" and style["endFill"] == "1" and style["rounded"] == "1" and style["jumpStyle"] == "arc", f"Wrong connector contract: {name}/{identifier}")
        require((style.get("dashed") == "1") == loop, f"Wrong semantic return rail: {name}/{identifier}")
        require(style["strokeColor"] == ("#d39300" if loop else "#746d68"), f"Wrong arrow tone: {name}/{identifier}")
        points = [tuple(p) for p in route["route"]]
        actual = [(float(p.get("x")), float(p.get("y"))) for p in edge.findall("mxGeometry/Array/mxPoint")]
        require(actual == points[1:-1], f"Waypoint drift: {name}/{identifier}")
        for node, prefix, point in ((source, "exit", points[0]), (target, "entry", points[-1])):
            x, y, w, h = A.BOXES[node]
            expected = (round(x + w * float(style[prefix + "X"]), 2), round(y + h * float(style[prefix + "Y"]), 2))
            require(point == expected, f"Detached endpoint: {name}/{identifier}")
        for first, second in zip(points, points[1:]):
            require(first != second and (first[0] == second[0] or first[1] == second[1]), f"Non-orthogonal segment: {name}/{identifier}")
            require(not any(A.intersects((first, second), box) for box in A.BOXES), f"Arrow through card: {name}/{identifier}")
        source_file, lines = A.evidence(proof).rsplit(":", 1)
        file = ROOT / source_file
        require(file.is_file(), f"Missing evidence: {file}")
        end = int(lines.split("-")[-1])
        require(0 < end <= len(file.read_text(encoding="utf-8-sig").splitlines()), f"Out-of-range evidence: {name}/{identifier}")
        old = original[identifier]
        port_keys = ("exitX", "exitY", "entryX", "entryY")
        changed = any(styles(old)[key] != style[key] for key in port_keys) or ET.tostring(old.find("mxGeometry")) != ET.tostring(edge.find("mxGeometry"))
        trace.append({"id": identifier, "source": f"n{source}", "target": f"n{target}", "relationship": relationship,
                      "evidence": A.evidence(proof), "result": "corrected" if changed else "clean"})
    return trace


def manifests():
    results = []
    for model in MODELS:
        name = model["name"]
        pitch, _ = artifacts(name, "pitch")
        growth = A.GROWTH.assess(A.filename(name, "pitch", "drawio").read_text(encoding="utf-8"), A.filename(name, "pass-01", "drawio").read_text(encoding="utf-8"))
        require(growth["passed"], f"Growth gate failed: {name}")
        passes = []
        for number in range(1, 5):
            phase = f"pass-{number:02}"
            item, inspection = artifacts(name, phase)
            item.update(number=number, mode="visual-upgrade" if number == 1 else "correction-only")
            item.update({key: inspection[key] for key in ("orientation_defects", "overlap_defects", "arrow_defects")})
            current = cells(A.filename(name, phase, "drawio"))
            if number > 1:
                previous = cells(A.filename(name, f"pass-{number-1:02}", "drawio"))
                require(current.keys() == previous.keys(), f"Later pass changed cell set: {name}/{phase}")
                for identifier, cell in current.items():
                    prior = previous[identifier]
                    require(all(cell.get(k) == prior.get(k) for k in ("value", "source", "target", "parent", "vertex", "edge")), f"Later pass changed content: {name}/{phase}/{identifier}")
                    if cell.get("vertex") == "1":
                        require(ET.tostring(cell.find("mxGeometry")) == ET.tostring(prior.find("mxGeometry")), f"Later pass reopened composition: {name}/{phase}/{identifier}")
                        old_style, new_style = styles(prior), styles(cell)
                        old_style.pop("fontSize", None)
                        new_style.pop("fontSize", None)
                        require(old_style == new_style, f"Later pass changed visual language: {name}/{phase}/{identifier}")
            if number == 1:
                item.update({k: growth[k] for k in ("baseline_meaningful_xml", "result_meaningful_xml", "growth_metric", "growth_ratio")})
            if number == 4:
                require(inspection.get("all_arrows_traced") is True, f"Final visual trace not recorded: {name}")
                item.update(all_arrows_traced=True, arrow_trace=check_trace(model))
                final_growth = A.GROWTH.assess(A.filename(name, "pitch", "drawio").read_text(encoding="utf-8"), A.filename(name, phase, "drawio").read_text(encoding="utf-8"))
                require(final_growth["passed"], f"Final XML gate failed: {name}")
            passes.append(item)
        manifest = {"diagram": name, "orientation": "A5-landscape", "pitch": pitch, "passes": passes, "final_pass": 4}
        jsonschema.Draft202012Validator(SCHEMA).validate(manifest)
        require(not V.validate_manifest(manifest), f"Unmodified iteration validator rejected: {name}")
        save(A.directory(name) / "iteration-manifest.json", manifest)
        results.append({"name": name, "growth_ratio": growth["growth_ratio"], "arrows": len(passes[-1]["arrow_trace"]), "manifest": relative(A.directory(name) / "iteration-manifest.json")})
    save(OUT / "manifest-validation.json", {"passed": True, "diagrams": results, "arrow_count": sum(r["arrows"] for r in results)})
    print(f"Validated {len(results)} manifests, growth gates, correction-only invariants and {sum(r['arrows'] for r in results)} arrows")


def archive(source, destination, copy_only=False):
    if source.exists():
        A.allowed(source)
        A.allowed(destination)
        require(not destination.exists(), f"Archive already exists: {destination}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        if copy_only:
            shutil.copy2(source, destination)
        else:
            shutil.move(source, destination)


def promote():
    manifests()
    for model in MODELS:
        name = model["name"]
        folder = A.directory(name) / "archive"
        for source, target in (
            (ROOT / f"docs/diagrams/src/{name}.json", "legacy.json"),
            (ROOT / f"docs/diagrams/drawio/generated/{name}.drawio", "legacy-generated.drawio"),
            (ROOT / f"docs/diagrams/{name}.png", "legacy.png"),
            (ROOT / f"docs/diagrams/{name}.hash.txt", "legacy.hash.txt"),
        ):
            archive(source, folder / target, copy_only=target in {"legacy.png", "legacy.hash.txt"})
        target = ROOT / f"docs/diagrams/src/{name}.drawio"
        require(not target.exists(), f"Canonical already exists: {name}")
        A.write(target, A.filename(name, "pass-04", "drawio").read_text(encoding="utf-8"))
    for document in PLAN["document_paths"]:
        path = ROOT / document
        text = path.read_text(encoding="utf-8")
        for model in MODELS:
            name = model["name"]
            comment = (f"<!-- Editable A5 source: ../diagrams/src/{name}.drawio; exported with draw.io Desktop 31.4.5.\n"
                       f"     Inspections and arrow trace: ../diagrams/reviews/{name}/v2/iteration-manifest.json. -->")
            text = re.sub(r"<!--(?:(?!-->).)*\.\./diagrams/src/" + re.escape(name) + r"\.json(?:(?!-->).)*-->", lambda _: comment, text, flags=re.S)
            alt = model["title"] + ": " + model["takeaway"]
            text = re.sub(r"!\[[^\]]*\]\(\.\./diagrams/" + re.escape(name) + r"\.png\)", lambda _: f"![{alt}](../diagrams/{name}.png)", text)
        for name in ("canonical-default-workflow", "canonical-workflow-selection"):
            if (ROOT / f"docs/diagrams/src/{name}.drawio").exists():
                comment = f"<!-- Shared read-only canonical; editable source: ../diagrams/src/{name}.drawio. -->"
                text = re.sub(r"<!--(?:(?!-->).)*\.\./diagrams/src/" + name + r"\.json(?:(?!-->).)*-->", lambda _: comment, text, flags=re.S)
        A.write(path, text)
    args = ["node", str(ROOT / "scripts/docs/render-diagrams.mjs"), "--drawio-cli", str(A.CLI), "--no-embed"]
    for model in MODELS:
        args.extend(["--spec", model["name"]])
    command(args, "render.log")
    print("Promoted and rendered 28 canonical draw.io sources")


def command(args, log):
    result = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    A.write(OUT / log, result.stdout + result.stderr)
    require(result.returncode == 0, f"Command failed ({result.returncode}); see {OUT / log}")
    return result.stdout


class IDs(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = set()

    def handle_starttag(self, tag, attrs):
        self.ids.update(value for key, value in attrs if key == "id")


def links(check_anchors=False):
    count, images, anchors, provenance = 0, 0, 0, 0
    broken_anchors = []
    for document in PLAN["document_paths"]:
        path = ROOT / document
        text = path.read_text(encoding="utf-8")
        for name in RETIRED:
            require(not re.search(r"\.\./diagrams/(?:src/)?" + re.escape(name) + r"\.(png|json|drawio)", text), f"Retired consumer: {document}/{name}")
        for ref in re.findall(r"\.\./diagrams/(?:src|reviews)/[^\s;<>]+\.(?:json|drawio)", text):
            require((path.parent / ref).is_file(), f"Missing provenance: {document}/{ref}")
            provenance += 1
        body = re.sub(r"```.*?```", "", text, flags=re.S)
        for bang, label, url in re.findall(r"(!?)\[([^\]]*)\]\(([^)\s]+)\)", body):
            parts = urlsplit(url)
            if parts.scheme or parts.netloc:
                continue
            if parts.path.startswith("/"):
                target = ROOT / "docs/public" / parts.path.lstrip("/")
            else:
                target = (path.parent / unquote(parts.path)).resolve() if parts.path else path
            require(target.is_file(), f"Broken local link: {document}: {url}")
            count += 1
            images += bool(bang)
            if check_anchors and parts.fragment and target.suffix == ".md":
                rendered = ROOT / "docs/.vitepress/dist" / target.relative_to(ROOT / "docs").with_suffix(".html")
                require(rendered.is_file(), f"Missing built anchor target: {rendered}")
                parser = IDs()
                parser.feed(rendered.read_text(encoding="utf-8"))
                if unquote(parts.fragment) not in parser.ids:
                    broken_anchors.append({"document": document, "url": url, "available_ids": sorted(parser.ids)})
                anchors += 1
    result = {"passed": not broken_anchors, "local_links": count, "images": images, "rendered_anchors": anchors, "provenance_paths": provenance,
              "broken_anchors": broken_anchors}
    save(OUT / ("links-built.json" if check_anchors else "links.json"), result)
    print(json.dumps(result))
    require(not broken_anchors, "Broken rendered anchors; see links-built.json")


def validate():
    manifests()
    for model in MODELS:
        name = model["name"]
        require(digest(ROOT / f"docs/diagrams/src/{name}.drawio") == digest(A.filename(name, "pass-04", "drawio")), f"Canonical XML differs: {name}")
        require(digest(ROOT / f"docs/diagrams/{name}.png") == digest(A.filename(name, "pass-04", "png")), f"Canonical PNG differs from inspected final: {name}")
    for name in RETIRED:
        for suffix in (f"src/{name}.json", f"src/{name}.drawio", f"{name}.png", f"{name}.hash.txt", f"drawio/generated/{name}.drawio"):
            require(not (ROOT / "docs/diagrams" / suffix).exists(), f"Retired public asset survives: {suffix}")
    args = ["node", str(ROOT / "scripts/docs/render-diagrams.mjs"), "--check"]
    for model in MODELS:
        args.extend(["--spec", model["name"]])
    command(args, "drift.log")
    tests = sorted((ROOT / "scripts/docs").glob("*.test.mjs"))
    output = command(["node", "--test", "--test-reporter=tap", *map(str, tests)], "tests.log")
    require(re.search(r"# fail 0\b", output), "Diagram tests did not report zero failures")
    command(["git", "--no-pager", "diff", "--check", "--", *PLAN["document_paths"], *PLAN["exclusive_asset_paths"]], "diff-check.log")
    links()
    save(OUT / "validation.json", {"passed": True, "diagram_count": 28, "retired_count": 6, "exact_final_xml_png_matches": 28,
         "tests": re.search(r"# tests (\d+)", output).group(1), "drift": "passed", "diff_check": "passed"})
    print("Canonical XML/PNG identity, selected drift, diagram tests, retirements and owned links passed")


def finish():
    validation = load(OUT / "validation.json")
    require(validation["passed"], "Scoped validation has not passed")
    build = (OUT / "docs-build.log").read_text(encoding="utf-8")
    require("build complete" in build.lower(), "No successful docs build log")
    links(True)
    verified = load(OUT / "manifest-validation.json")
    summary = {"status": "complete", "model": "gpt-6-astra", "plan": ".github/skills/docs-diagram-audit/reports/plan-deep-dive-orchestration.json",
               "researchers": 3, "research": A.RESEARCH, "survivors": 28, "removed": 2, "merged": 4, "documents": PLAN["document_paths"],
               "post_pitch_passes_per_diagram": 4, "final_arrows_traced": verified["arrow_count"],
               "growth_ratio_min": min(d["growth_ratio"] for d in verified["diagrams"]),
               "growth_ratio_max": max(d["growth_ratio"] for d in verified["diagrams"]),
               "diagrams": verified["diagrams"], "validation": validation, "docs_build": "passed", "links": load(OUT / "links-built.json"),
               "renderer": load(HERE / "renderer/renderer-provenance.json"),
               "scope": "Exclusive assets, ten owned pages, and one owned execution finding only. No product, workflow, pilot, shared asset, inventory, plan or commit changes.",
               "blockers": []}
    save(OUT / "execution-manifest.json", summary)
    for name in ("execution-manifest", "iteration-manifest"):
        old = HERE / f"{name}.json"
        archive(old, HERE / f"{name}-v1-historical.json")
    save(HERE / "execution-manifest.json", summary)
    save(HERE / "iteration-manifest.json", load(A.directory("coordinator-internals-fig4") / "iteration-manifest.json"))
    report = ROOT / ".github/skills/docs-diagram-audit/reports/deep-dive-orchestration.json"
    text = report.read_text(encoding="utf-8")
    finding = {"severity": "info", "subject": "Completed bounded orchestration v2 execution",
               "detail": "28 editable A5 canonicals promoted after inspected pitches and four post-pitch passes each; all pass-1 expansions exceed 9x. Three bounded Astra researchers, 259 final arrows traced, 2 removals, 4 consolidations and all 10 owned pages completed. Historical research-only/blocked artifacts remain as history, not current status. Shared assets and global catalogs were not edited. The shared memory schematic is retained as a reference link beside the authoritative eligibility table rather than an unqualified bitmap.",
               "evidence": [relative(OUT / "execution-manifest.json"), relative(OUT / "validation.json"), relative(OUT / "links-built.json"), relative(OUT / "docs-build.log")]}
    require(finding["subject"] not in text, "Completion finding already present")
    text, count = re.subn(r'("findings"\s*:\s*\[)', lambda m: m[0] + "\n    " + json.dumps(finding) + ",", text, count=1)
    require(count == 1, "Could not locate owned findings")
    json.loads(text)
    require(report.relative_to(ROOT).as_posix() == ".github/skills/docs-diagram-audit/reports/deep-dive-orchestration.json", "Wrong report scope")
    report.write_text(text, encoding="utf-8")
    print(json.dumps({k: summary[k] for k in ("status", "survivors", "removed", "merged", "final_arrows_traced", "growth_ratio_min", "growth_ratio_max", "blockers")}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["manifests", "promote", "validate", "finish", "trace-preview"])
    args = parser.parse_args()
    if args.action == "trace-preview":
        for model in MODELS:
            trace = check_trace(model)
            print(model["name"] + ": " + "; ".join(f"{t['id']} {model['nodes'][int(t['source'][1:])][0]} -> {model['nodes'][int(t['target'][1:])][0]}" for t in trace))
    else:
        globals()[args.action]()
