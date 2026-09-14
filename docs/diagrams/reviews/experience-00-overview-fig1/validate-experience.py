"""Scoped publication checks for this experience review, not a global audit."""
import argparse
import hashlib
import json
import re
import runpy
import shutil
import subprocess
from urllib.parse import unquote
from pathlib import Path
from xml.etree import ElementTree as ET

import jsonschema
from PIL import Image, ImageChops

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
AUTHOR = runpy.run_path(str(HERE / "author-experience.py"))
PLAN = AUTHOR["PLAN"]
MODELS = AUTHOR["MODELS"]
REVIEWS = AUTHOR["REVIEWS"]
REMOVED = sorted(set(PLAN["diagram_names"]) - set(MODELS))


def allowed(path):
    relative = path.relative_to(ROOT).as_posix()
    if not any(relative == p or (p.endswith("/") and relative.startswith(p))
               for p in PLAN["exclusive_asset_paths"]):
        raise ValueError(f"Unowned asset: {relative}")
    return path


def save(path, content):
    allowed(path).write_text(json.dumps(content, indent=2) + "\n", encoding="utf-8")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def label(value):
    return " ".join(re.sub(r"<br\s*/?>", " ", value).split())


def semantic_cells(source):
    return {c.get("id"): {k: label(v) if k == "value" else v
                         for k, v in c.attrib.items() if k != "style"}
            for c in ET.parse(source).iter("mxCell")}


def validate():
    result = {}
    for name, model in MODELS.items():
        folder = REVIEWS / name
        manifest_path = folder / "iteration-manifest.json"
        manifest = json.loads(manifest_path.read_text())
        jsonschema.validate(manifest, json.loads(AUTHOR["SCHEMA"].read_text()))
        subprocess.run(["python", str(ROOT / ".github/skills/docs-diagram-iterate/scripts/validate_iteration_manifest.py"),
                        str(manifest_path)], check=True, capture_output=True, text=True)
        measured = subprocess.run(
            ["python", str(ROOT / ".github/skills/docs-diagram-iterate/scripts/check_xml_growth.py"),
             str(folder / f"{name}-pitch.drawio"), str(folder / f"{name}-pass-01.drawio"), "--json"],
            check=True, capture_output=True, text=True)
        growth = json.loads(measured.stdout)
        growth["pitch"] = f"{name}-pitch.drawio"
        growth["pass_one"] = f"{name}-pass-01.drawio"
        save(folder / "meaningful-growth.json", growth)
        baseline = semantic_cells(folder / f"{name}-pass-01.drawio")
        for number in range(2, 5):
            assert semantic_cells(folder / f"{name}-pass-{number:02}.drawio") == baseline, name
        source = folder / f"{name}-pass-04.drawio"
        tree = ET.parse(source)
        assert len(tree.findall("./diagram")) == 1, name
        graph = tree.find("./diagram/mxGraphModel")
        assert graph is not None and graph.get("pageWidth") == "827" and graph.get("pageHeight") == "583", name
        assert graph.get("pageScale") == "1", name
        analysis = AUTHOR["growth"].analyze(source.read_text(encoding="utf-8"))
        for field in ("invisible_cells", "off_page_cells", "duplicate_cells", "metadata_padding"):
            assert not analysis[field], (name, field, analysis[field])
        cells = {c.get("id"): c for c in graph.iter("mxCell")}
        edges = {c.get("id"): c for c in cells.values() if c.get("edge") == "1"}
        trace = manifest["passes"][-1]["arrow_trace"]
        assert set(edges) == {e["id"] for e in trace}, name
        for e in trace:
            actual = edges[e["id"]]
            assert actual.get("source") == e["source"] and actual.get("target") == e["target"], (name, e)
            assert label(actual.get("value")) == e["relationship"], (name, e)
            assert "endArrow=block" in actual.get("style"), (name, e)
            assert e["evidence"] and e["result"] == "clean", (name, e)
        png = folder / f"{name}-pass-04.png"
        image = Image.open(png).convert("RGB")
        assert image.size == (1704, 1216), (name, image.size)
        assert Image.open(folder / f"{name}-pass-04-print.png").width == 827, name
        colors = {rgb for count, rgb in image.getcolors(image.width * image.height)}
        assert {(239, 234, 231), (253, 251, 248), (248, 244, 241)} <= colors, name
        final = ROOT / "docs/diagrams/src" / f"{name}.drawio"
        published = ROOT / "docs/diagrams" / f"{name}.png"
        publication = "not-promoted"
        if final.exists() and final.read_bytes() == source.read_bytes():
            canonical_image = Image.open(published).convert("RGB")
            assert canonical_image.size == image.size, name
            assert ImageChops.difference(image, canonical_image).getbbox() is None, name
            publication = "canonical-source-identical-and-render-pixel-identical"
        result[name] = {
            "growth_ratio": growth["growth_ratio"], "arrow_count": len(trace),
            "passes": 4, "a5": True, "uncompressed": True, "correction_only_semantics_preserved": True,
            "png_size": list(image.size), "source_sha256": digest(source), "png_sha256": digest(png),
            "publication": publication,
        }
    save(HERE / "artifact-validation.json", result)
    print(f"Validated {len(result)} manifests, growth gates, A5 XML, semantic preservation and complete arrow sets.")


def promote():
    validate()
    for name in REMOVED:
        for doc in (ROOT / "docs").rglob("*.md"):
            if "reviews" in doc.parts or ".vitepress" in doc.parts:
                continue
            if re.search(re.escape(name) + r"\.(png|drawio|json)", doc.read_text(encoding="utf-8")):
                raise ValueError(f"Remaining consumer: {doc}: {name}")
    for name in MODELS:
        folder = REVIEWS / name
        target = allowed(ROOT / "docs/diagrams/src" / f"{name}.drawio")
        shutil.copyfile(folder / f"{name}-pass-04.drawio", target)
        shutil.copyfile(folder / f"{name}-pass-04.png", allowed(ROOT / "docs/diagrams" / f"{name}.png"))
    removed_paths = []
    for name in PLAN["diagram_names"]:
        paths = [ROOT / "docs/diagrams/src" / f"{name}.json",
                 ROOT / "docs/diagrams/drawio/generated" / f"{name}.drawio"]
        if name in REMOVED:
            paths += [ROOT / "docs/diagrams/src" / f"{name}.drawio",
                      ROOT / "docs/diagrams" / f"{name}.png",
                      ROOT / "docs/diagrams" / f"{name}.hash.txt"]
        for path in paths:
            allowed(path)
            if path.exists():
                path.unlink()
                removed_paths.append(path.relative_to(ROOT).as_posix())
    save(HERE / "publication-ledger.json", {
        "published": list(MODELS), "removed_diagram_identities": REMOVED, "removed_asset_paths": removed_paths,
        "screenshots": "No screenshots generated or changed. Misleading embeds replaced by grounded prose; two qualified real examples retained.",
        "documents": PLAN["document_paths"],
    })
    print(f"Promoted {len(MODELS)} diagrams; removed {len(REMOVED)} identities and {len(removed_paths)} obsolete asset files.")


def check_documents():
    audit = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/experience.json").read_text())
    declared = {c["canonical_diagram"] for d in audit["documents"] for c in d["concepts"]
                if c.get("canonical_diagram")}
    issues, checked, screenshots, shared = [], [], set(), set()
    documents = [ROOT / p for p in PLAN["document_paths"]]
    documents += [p for name in MODELS for p in (REVIEWS / name).glob("*.md")]
    for doc in documents:
        text = doc.read_text(encoding="utf-8")
        text = re.sub(r"```.*?```", "", text, flags=re.S)
        targets = re.findall(r"!?\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)", text)
        targets += [p.rstrip(".") for p in re.findall(r"(?:\.\./)+diagrams/src/[\w.-]+", text)]
        for target in targets:
            if re.match(r"(?:https?:|mailto:|data:)", target):
                continue
            original = target
            target, _, fragment = unquote(target).partition("#")
            if target.startswith("/screenshots/"):
                resolved = ROOT / "docs/public" / target.lstrip("/")
                screenshots.add(target)
            elif target.startswith("/"):
                resolved = ROOT / "docs" / target.lstrip("/")
            else:
                resolved = doc.parent / target if target else doc
            if not resolved.suffix:
                candidates = [resolved.with_suffix(".md"), resolved / "index.md", resolved / "README.md"]
                resolved = next((p for p in candidates if p.is_file()), resolved)
            if not resolved.is_file():
                issues.append(f"{doc.relative_to(ROOT)}: missing {original}")
                continue
            if fragment and resolved.suffix == ".md":
                content = resolved.read_text(encoding="utf-8")
                headings = re.findall(r"^#{1,6}\s+(.+?)\s*#*\s*$", content, re.M)
                anchors = set(re.findall(r'(?:id|name)=["\']([^"\']+)', content))
                for heading in headings:
                    custom = re.search(r"\{#([^}]+)\}", heading)
                    if custom:
                        anchors.add(custom.group(1))
                    heading = re.sub(r"<[^>]+>", "", heading)
                    heading = re.sub(r"[^\w\s-]", "", heading.lower()).strip()
                    anchors.add(re.sub(r"\s", "-", heading))
                compiled = ROOT / "docs/.vitepress/dist" / resolved.resolve().relative_to(ROOT / "docs").with_suffix(".html")
                if compiled.is_file():
                    anchors = set(re.findall(r'id="([^"]+)"', compiled.read_text(encoding="utf-8")))
                if fragment not in anchors:
                    issues.append(f"{doc.relative_to(ROOT)}: missing anchor {original}")
            if resolved.suffix == ".png" and resolved.parent.name == "diagrams":
                assert resolved.stem not in REMOVED, original
                if resolved.stem not in MODELS:
                    assert resolved.stem in declared, f"Undeclared shared reference: {original}"
                    shared.add(resolved.stem)
            checked.append({"document": doc.relative_to(ROOT).as_posix(), "target": original})
    expected = {"/screenshots/casting-wizard-review.png", "/screenshots/project-board.png"}
    assert screenshots == expected, screenshots
    report = {"checked_count": len(checked), "issues": issues, "links": checked,
              "qualified_real_screenshot_references": sorted(screenshots),
              "declared_shared_diagrams_referenced_without_asset_writes": sorted(shared)}
    save(HERE / "consumer-link-validation.json", report)
    if issues:
        raise ValueError("\n".join(issues))
    print(f"Validated {len(checked)} local links/provenance references, two real capture examples and {len(shared)} declared shared assets.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["validate", "promote", "documents"])
    args = parser.parse_args()
    {"promote": promote, "validate": validate, "documents": check_documents}[args.action]()
