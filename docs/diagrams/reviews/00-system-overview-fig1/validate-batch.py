import hashlib
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import jsonschema
from PIL import Image

here = Path(__file__).resolve().parent
repo = here.parents[3]
schema = json.loads((repo / ".github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json").read_text())
names = [
    "00-system-overview-fig1", "00-system-overview-fig2", "00-system-overview-fig5",
    "agent-definition-fig1", "agent-framework-fig1", "agent-framework-fig2", "agent-framework-fig3",
    "api-core-fig4", "api-core-fig6", "data-persistence-fig1", "canonical-testing-boundary",
    "testing-strategy-fig1", "testing-strategy-fig4",
]
reports = []
for name in names:
    directory = here.parent / name
    manifest_file = directory / "iteration-manifest.json"
    manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator(schema).validate(manifest)
    subprocess.run([sys.executable, "-B", str(repo / ".github/skills/docs-diagram-iterate/scripts/validate_iteration_manifest.py"), str(manifest_file)], check=True, capture_output=True)
    artifacts = [manifest["pitch"], *manifest["passes"]]
    for artifact in artifacts:
        for field in ("drawio", "png", "change_record"):
            assert (directory / artifact[field]).is_file(), (name, artifact[field])
        source = directory / artifact["drawio"]
        root = ET.parse(source).getroot()
        diagrams = root.findall("diagram")
        assert len(diagrams) == 1 and diagrams[0].find("mxGraphModel") is not None
        model = diagrams[0].find("mxGraphModel")
        assert (model.get("pageWidth"), model.get("pageHeight"), model.get("pageScale")) == ("827", "583", "1")
        cells = model.findall(".//mxCell")
        assert len({c.get("id") for c in cells}) == len(cells)
        assert not any("image=" in c.get("style", "") for c in cells)
        vertices = [c for c in cells if c.get("vertex") == "1"]
        assert len([c for c in vertices if "-icon" in c.get("id", "")]) >= (2 if artifact is manifest["pitch"] else 8)
        if artifact is not manifest["pitch"]:
            for suffix in ("accent", "icon", "title", "subtitle", "detail", "metadata", "badge"):
                assert len([c for c in vertices if c.get("id", "").endswith("-" + suffix)]) >= 8
            accents = [c for c in vertices if c.get("id", "").endswith("-accent")]
            assert all(c.find("mxGeometry").get("width") == "5" for c in accents)
            assert len([c for c in vertices if re.fullmatch(r"group-\d", c.get("id", ""))]) == 2
        edges = {c.get("id"): c for c in cells if c.get("edge") == "1"}
        for edge in edges.values():
            assert "edgeStyle=orthogonalEdgeStyle" in edge.get("style")
            assert "endArrow=block" in edge.get("style")
            assert edge.get("source") in {c.get("id") for c in vertices}
            assert edge.get("target") in {c.get("id") for c in vertices}
        if artifact.get("all_arrows_traced"):
            trace = artifact["arrow_trace"]
            assert len(trace) == len(edges) == len({a["id"] for a in trace}), name
            for arrow in trace:
                edge = edges[arrow["id"]]
                assert (arrow["source"], arrow["target"]) == (edge.get("source"), edge.get("target")), (name, arrow["id"])
                assert f'`{arrow["id"]}`' in (directory / artifact["change_record"]).read_text(encoding="utf-8")
                for evidence in arrow["evidence"].split("; "):
                    filename = evidence.split(":")[0]
                    assert (repo / filename).is_file(), (name, arrow["id"], filename)
        with Image.open(directory / artifact["png"]) as image:
            assert image.format == "PNG" and image.width > image.height
            assert image.width >= 1500
            if artifact is not manifest["pitch"]:
                assert image.height >= 1000
            image.verify()
        with Image.open(source.with_name(source.stem + "-print.png")) as image:
            assert image.size == (794, 559)
    growth = subprocess.run(
        [sys.executable, "-B", str(repo / ".github/skills/docs-diagram-iterate/scripts/check_xml_growth.py"),
         str(directory / manifest["pitch"]["drawio"]), str(directory / manifest["passes"][0]["drawio"]), "--json"],
        check=True, capture_output=True, text=True)
    measured = json.loads(growth.stdout)
    for key in ("baseline_meaningful_xml", "result_meaningful_xml", "growth_ratio", "growth_metric"):
        assert measured[key] == manifest["passes"][0][key]
    assert measured["passed"]
    for key in ("invisible_cells", "off_page_cells", "duplicate_cells", "metadata_padding"):
        assert not measured[key]
    final = manifest["passes"][-1]
    final_growth = subprocess.run(
        [sys.executable, "-B", str(repo / ".github/skills/docs-diagram-iterate/scripts/check_xml_growth.py"),
         str(directory / manifest["pitch"]["drawio"]), str(directory / final["drawio"]), "--json"],
        check=True, capture_output=True, text=True)
    assert json.loads(final_growth.stdout)["passed"]
    if "--promoted" in sys.argv:
        src = repo / "docs/diagrams/src" / (name + ".drawio")
        assert not src.with_suffix(".json").exists()
        assert src.read_bytes() == (directory / final["drawio"]).read_bytes()
        assert src.read_bytes() == (repo / "docs/diagrams/drawio/generated" / (name + ".drawio")).read_bytes()
        assert (directory / final["png"]).read_bytes() == (repo / "docs/diagrams" / (name + ".png")).read_bytes()
    report = {
        "diagram": name, "schema": "valid", "repository_validator": "valid",
        "final_pass": manifest["final_pass"], "artifact_sets_verified": len(artifacts),
        "exact_final_arrows": len(final["arrow_trace"]), "a5_uncompressed_native": True,
        "growth_ratio": measured["growth_ratio"], "anti_padding": "passed",
        "inspected_png_sha256": hashlib.sha256((directory / final["png"]).read_bytes()).hexdigest(),
        "promoted_bytes_match": "--promoted" in sys.argv,
    }
    (directory / "validation-results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    reports.append(report)
print(json.dumps({"validated": len(reports), "arrows": sum(r["exact_final_arrows"] for r in reports), "reports": reports}, indent=2))
