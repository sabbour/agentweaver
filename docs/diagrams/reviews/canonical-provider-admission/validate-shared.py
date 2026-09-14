"""Read-only shared artifact checks; write only this plan's review report."""
from collections import Counter
import hashlib
from html.parser import HTMLParser
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

import jsonschema
from PIL import Image

ROOT = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
PLAN = ROOT / ".github/skills/docs-diagram-audit/reports/plan-shared.json"
ITERATE = ROOT / ".github/skills/docs-diagram-iterate"
plan = json.loads(PLAN.read_text(encoding="utf-8"))
catalog = json.loads((ROOT / "docs-diagram-audit.json").read_text(encoding="utf-8"))
schema = json.loads((ITERATE / "references/iteration-manifest.schema.json").read_text())
spec = importlib.util.spec_from_file_location("growth", ITERATE / "scripts/check_xml_growth.py")
growth = importlib.util.module_from_spec(spec)
spec.loader.exec_module(growth)
selected = [d for d in catalog["diagrams"] if d["name"] in plan["diagram_names"]]
report = {
    "owner": "shared",
    "status": "blocked",
    "warning": "Mechanical validation complements the per-name inspected remediation lineages; cross-scope consumer handoffs remain separate.",
    "dispositions": dict(Counter(d["disposition"] for d in selected)),
    "diagrams": [],
    "removed_families": [],
    "merged_families": [],
    "consumer_dependencies": [],
    "page_contract_errors": [],
    "local_image_link_errors": [],
    "local_document_link_errors": [],
}
promoted = []


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


for name in plan["diagram_names"] + plan["proposed_diagram_names"]:
    directory = ROOT / "docs/diagrams/reviews" / name
    manifest_path = directory / "iteration-manifest.json"
    if not manifest_path.exists():
        continue
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator(schema).validate(manifest)
    result = subprocess.run(
        [sys.executable, "-B", str(ITERATE / "scripts/validate_iteration_manifest.py"),
         str(manifest_path)], capture_output=True, text=True, check=True)
    entry = {"name": name, "schema": "passed", "manifest_cross_fields": "passed", "artifacts": []}
    for stage in [manifest["pitch"], *manifest["passes"]]:
        source = directory / stage["drawio"]
        image = directory / stage["png"]
        change = directory / stage["change_record"]
        assert source.is_file() and image.is_file() and change.is_file(), name
        analysis = growth.analyze(source.read_text(encoding="utf-8"), str(source))
        if (analysis["page_width"], analysis["page_height"]) not in [(827, 583), (583, 827)]:
            report["page_contract_errors"].append({
                "diagram": name, "source": source.name,
                "width": analysis["page_width"], "height": analysis["page_height"],
                "reason": "Not A5 at draw.io's 100 logical units per inch; export DPI does not change page format.",
            })
        for field in ("invisible_cells", "off_page_cells", "duplicate_cells", "metadata_padding"):
            assert not analysis[field], (name, source.name, field, analysis[field])
        with Image.open(image) as raster:
            dimensions = list(raster.size)
            raster.verify()
        entry["artifacts"].append({
            "drawio": source.name, "xml_sha256": sha(source),
            "png": image.name, "png_sha256": sha(image), "dimensions": dimensions,
            "visible_semantic_count": analysis["meaningful_count"],
        })
    ratio = entry["artifacts"][1]["visible_semantic_count"] / entry["artifacts"][0]["visible_semantic_count"]
    assert ratio >= 9, (name, ratio)
    entry["growth_ratio"] = ratio
    final = manifest["passes"][-1]
    xml = ET.parse(directory / final["drawio"])
    edges = {
        c.get("id"): c for c in xml.iter("mxCell")
        if c.get("edge") == "1"
        and any(growth.parse_style(c.get("style", "")).get(end, "none") != "none"
                for end in ("startArrow", "endArrow"))
    }
    traces = {t["id"]: t for t in final["arrow_trace"]}
    assert set(edges) == set(traces), (name, set(edges) ^ set(traces))
    cells = {c.get("id"): c for c in xml.iter("mxCell")}
    for edge_id, cell in edges.items():
        for endpoint in ("source", "target"):
            actual = cell.get(endpoint)
            intended = traces[edge_id][endpoint]
            if actual != intended:
                assert re.fullmatch(r"activation\d+-" + re.escape(intended), actual or ""), (name, edge_id, endpoint, actual, intended)
                activation_bounds = growth.absolute_bounds(actual, cells, {})
                participant_bounds = growth.absolute_bounds(intended, cells, {})
                assert activation_bounds[0] + activation_bounds[2] / 2 == participant_bounds[0] + participant_bounds[2] / 2
    entry["final_arrows"] = len(edges)
    canonical = ROOT / f"docs/diagrams/src/{name}.drawio"
    entry["canonical_present"] = canonical.exists()
    if canonical.exists():
        assert sha(canonical) == sha(directory / final["drawio"]), name
        published = ROOT / f"docs/diagrams/{name}.png"
        with Image.open(published) as actual, Image.open(directory / final["png"]) as expected:
            assert actual.size == expected.size and actual.convert("RGBA").tobytes() == expected.convert("RGBA").tobytes(), name
        stamp = json.loads((ROOT / f"docs/diagrams/{name}.hash.txt").read_text())
        assert stamp["renderer"]["rendererVersion"] == "31.4.5"
        assert stamp["png"]["sha256"] == sha(published)
        assert not (ROOT / f"docs/diagrams/src/{name}.json").exists()
        assert not (ROOT / f"docs/diagrams/drawio/generated/{name}.drawio").exists()
        if name.startswith("email-"):
            assert sha(ROOT / f"docs/diagrams/email-exports/{name}.png") == sha(published)
        promoted.append(name)
    report["diagrams"].append(entry)

for diagram in selected:
    if diagram["disposition"] == "remove":
        paths = [p for p in plan["exclusive_asset_paths"]
                 if diagram["name"] in p and not p.endswith("/")]
        remaining = [p for p in paths if (ROOT / p).exists()]
        assert not remaining, remaining
        report["removed_families"].append({"name": diagram["name"], "absent_paths": paths})
    if diagram["disposition"] == "merge":
        paths = [p for p in plan["exclusive_asset_paths"]
                 if diagram["name"] in p and not p.endswith("/")]
        remaining = [p for p in paths if (ROOT / p).exists()]
        if remaining:
            report["consumer_dependencies"].append({
                "name": diagram["name"], "target": diagram["target"],
                "status": "deferred-out-of-scope-consumers", "retained_paths": remaining,
                "foreign_consumers": [r for r in diagram["references"]
                                      if r["path"] not in plan["document_paths"]],
            })
        else:
            assert (ROOT / f"docs/diagrams/src/{diagram['target']}.drawio").exists()
            report["merged_families"].append({
                "name": diagram["name"], "target": diagram["target"],
                "status": "merged-and-retired", "absent_paths": paths,
            })
report["retired_non_catalog_assets"] = []
for obsolete, target in (
    ("docs/aks-architecture.excalidraw", "canonical-aks-components"),
    ("docs/aks-architecture-block.excalidraw", "canonical-aks-components"),
    ("docs/public/pitch-architecture.png", "email-architecture"),
):
    assert not (ROOT / obsolete).exists(), obsolete
    report["retired_non_catalog_assets"].append({"path": obsolete, "target": target})

class ReadmeImages(HTMLParser):
    def __init__(self):
        super().__init__()
        self.images = []
        self.unreviewed = []

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        if tag == "img":
            self.images.append({
                "syntax": "html", "path": attributes.get("src"),
                "alt": attributes.get("alt"), "line": self.getpos()[0],
            })
            if attributes.get("srcset"):
                self.unreviewed.append("img srcset")
        elif tag in {"svg", "canvas", "video", "object", "embed", "iframe", "source"}:
            self.unreviewed.append(tag)


readme = (ROOT / "README.md").read_text(encoding="utf-8")
assert not re.search(r"```(?:mermaid|plantuml|dot)\b|url\s*\(", readme, re.I), "Unreviewed README visual"
readme_without_code = re.sub(r"```.*?```", "", readme, flags=re.S)
html_images = ReadmeImages()
html_images.feed(readme_without_code)
assert not html_images.unreviewed, html_images.unreviewed
markdown_images = [
    {"syntax": "markdown", "path": m[2], "alt": m[1],
     "line": readme_without_code[:m.start()].count("\n") + 1}
    for m in re.finditer(r"!\[([^\]]*)\]\(([^)\s]+)\)", readme_without_code)
]
assert len(markdown_images) == len(re.findall(r"!\[", readme_without_code)), "Unreviewed reference-style README image"
readme_images = html_images.images + markdown_images
expected_readme_images = {
    "docs/public/agentweaver.png": "retain",
    "docs/diagrams/email-architecture.png": "reuse",
}
assert len(readme_images) == 2 and {i["path"] for i in readme_images} == set(expected_readme_images), readme_images
for image in readme_images:
    assert image["alt"] and image["alt"].strip(), image
    asset = ROOT / image["path"]
    assert asset.is_file(), image
    with Image.open(asset) as raster:
        image["dimensions"] = list(raster.size)
        raster.verify()
    image["sha256"] = sha(asset)
    image["disposition"] = expected_readme_images[image["path"]]
report["readme_visual_coverage"] = {
    "status": "passed", "embedded_visuals": readme_images,
    "retired_duplicate": "docs/public/pitch-architecture.png",
    "evidence": "docs/diagrams/reviews/canonical-provider-admission/readme-visual-coverage.md",
    "scope": "Entire README: HTML and Markdown images; no additional embedded visual constructs.",
}

for page in plan["document_paths"]:
    path = ROOT / page
    text = path.read_text(encoding="utf-8")
    text = re.sub(r"```.*?```", "", text, flags=re.S)
    for match in re.finditer(r"(!?)\[[^\]]*\]\(([^)\s]+)(?:\s+[^)]*)?\)", text):
        image, target = match.groups()
        target = target.split("#", 1)[0].split("?", 1)[0]
        if not target or re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target):
            continue
        resolved = (ROOT / "docs" / target.lstrip("/")) if target.startswith("/") else path.parent / target
        options = [resolved]
        if not resolved.suffix:
            options += [resolved.with_suffix(".md"), resolved / "index.md"]
        if target.startswith("/"):
            options += [ROOT / "docs/public" / target.lstrip("/")]
        if not any(p.exists() for p in options):
            key = "local_image_link_errors" if image else "local_document_link_errors"
            report[key].append({"page": page, "target": target})

args = ["node", str(ROOT / "scripts/docs/render-diagrams.mjs"), "--check"]
for name in promoted:
    args += ["--spec", name]
drift = subprocess.run(args, cwd=ROOT, capture_output=True, text=True)
report["selective_drift"] = {"exit_code": drift.returncode, "output": drift.stdout + drift.stderr}
report["summary"] = {
    "review_manifests": len(report["diagrams"]),
    "canonical_files_migrated": len(promoted),
    "review_only": len(report["diagrams"]) - len(promoted),
    "artifact_triples": sum(len(d["artifacts"]) for d in report["diagrams"]),
    "arrows_in_final_manifests": sum(d["final_arrows"] for d in report["diagrams"]),
    "removed_families": len(report["removed_families"]),
    "merged_families": len(report["merged_families"]),
    "merge_dispositions_deferred": len(report["consumer_dependencies"]),
}
report["research"] = [
    {"file": f"research-thread-{i:02}.md", "sha256": sha(HERE / f"research-thread-{i:02}.md")}
    for i in (1, 2, 3)
]
(HERE / "shared-validation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(json.dumps(report["summary"], indent=2))
print("Image links:", report["local_image_link_errors"])
print("Document links:", report["local_document_link_errors"])
print("Drift exit:", drift.returncode)
print("A5 page errors:", report["page_contract_errors"])
print("README visual coverage:", len(readme_images), "passed (HTML logo retained; Markdown canonical reused)")
if drift.returncode or report["local_image_link_errors"] or report["page_contract_errors"]:
    sys.exit(1)
