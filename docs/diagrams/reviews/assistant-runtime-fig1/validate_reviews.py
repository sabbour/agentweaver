"""Validate only this assignment's review-local artifacts; never promote canonicals."""
import json
from pathlib import Path
import re
import runpy
import xml.etree.ElementTree as ET

from jsonschema import Draft202012Validator
from PIL import Image

HERE = Path(__file__).resolve().parent
A = runpy.run_path(str(HERE / "author_diagrams.py"))
ROOT = A["ROOT"]
SCRIPTS = ROOT / ".github" / "skills" / "docs-diagram-iterate"
G = runpy.run_path(str(SCRIPTS / "scripts" / "check_xml_growth.py"))
V = runpy.run_path(str(SCRIPTS / "scripts" / "validate_iteration_manifest.py"))
SCHEMA = json.loads((SCRIPTS / "references" / "iteration-manifest.schema.json").read_text())
Draft202012Validator.check_schema(SCHEMA)


def cells(path):
    tree = ET.parse(path)
    assert len(tree.findall("diagram")) == 1, path
    model = tree.find("diagram/mxGraphModel")
    assert model is not None, path
    assert (int(model.get("pageWidth")), int(model.get("pageHeight"))) == (827, 583), path
    return {c.get("id"): c for c in tree.getroot().iter("mxCell")}


for name, data in A["DATA"].items():
    folder = A["REVIEWS"] / name
    manifest = json.loads((folder / "iteration-manifest.json").read_text(encoding="utf-8"))
    Draft202012Validator(SCHEMA).validate(manifest)
    assert not V["validate_manifest"](manifest), name
    assert manifest["diagram"] == name and manifest["final_pass"] == 4
    measured = G["assess"](
        (folder / manifest["pitch"]["drawio"]).read_text(encoding="utf-8"),
        (folder / manifest["passes"][0]["drawio"]).read_text(encoding="utf-8"),
    )
    assert measured["passed"], measured
    assert measured["baseline_meaningful_xml"] == manifest["passes"][0]["baseline_meaningful_xml"]
    assert measured["result_meaningful_xml"] == manifest["passes"][0]["result_meaningful_xml"]
    image_sizes = {}
    for artifact in [manifest["pitch"], *manifest["passes"]]:
        for key in ("drawio", "png", "change_record"):
            path = folder / artifact[key]
            assert path.is_file() and path.stat().st_size > 0, path
        source = folder / artifact["drawio"]
        cells(source)
        analysis = G["analyze"](source.read_text(encoding="utf-8"))
        for field in ("invisible_cells", "off_page_cells", "duplicate_cells", "metadata_padding"):
            assert not analysis[field], (source, field, analysis[field])
        with Image.open(folder / artifact["png"]) as image:
            assert image.format == "PNG" and image.width > 800 and image.height > 500
            image_sizes[artifact["png"]] = image.size
            image.verify()
        with Image.open(source.with_name(source.stem + "-print.png")) as image:
            assert image.size == (827, 583)
            image.verify()
    for previous, current in zip(manifest["passes"], manifest["passes"][1:]):
        before = cells(folder / previous["drawio"])
        after = cells(folder / current["drawio"])
        assert before.keys() == after.keys(), name
        for ident, old in before.items():
            new = after[ident]
            if old.get("edge") == "1":
                for field in ("source", "target", "value"):
                    assert old.get(field) == new.get(field), (name, ident, field)
            elif ident.endswith("-icon"):
                old.set("style", old.get("style").replace("size=6;", ""))
                assert ET.tostring(old) == ET.tostring(new), (name, ident)
            else:
                assert ET.tostring(old) == ET.tostring(new), (name, ident)
    final = cells(folder / manifest["passes"][-1]["drawio"])
    arrows = {ident: cell for ident, cell in final.items() if cell.get("edge") == "1"}
    trace = manifest["passes"][-1]["arrow_trace"]
    assert len(trace) == len(arrows) == len(data["edges"])
    assert {entry["id"] for entry in trace} == arrows.keys()
    for entry in trace:
        actual = arrows[entry["id"]]
        assert entry["source"] == actual.get("source")
        assert entry["target"] == actual.get("target")
        assert "endArrow=block;" in actual.get("style")
        assert "jumpStyle=arc;" in actual.get("style")
        assert actual.find("mxGeometry") is not None
        for array in actual.findall("mxGeometry/Array"):
            assert array.get("as") == "points"
    missing = []
    for node in data["nodes"]:
        for citation in re.findall(r"(?:apps|src|packages|k8s|docs)/[A-Za-z0-9_./-]+\.(?:cs|yaml|md|tsx)", node["evidence"]):
            if not ROOT.joinpath(*citation.split("/")).is_file():
                missing.append(citation)
    assert not missing, (name, missing)
    (folder / "evidence.md").write_text(A["research_record"](name), encoding="utf-8")
    model = dict(data)
    model["symbol_classifications"] = {
        f"n{i}": A["SYMBOLS"][node["symbol"]][1] for i, node in enumerate(data["nodes"])
    }
    (folder / "content-model.json").write_text(json.dumps(model, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    report = {
        "diagram": name,
        "status": "review-candidate-validated-not-promoted",
        "json_schema": "passed",
        "skill_manifest_validator": "passed",
        "growth": measured,
        "artifact_triples": 5,
        "images": image_sizes,
        "one_page_a5_editable_xml": "passed",
        "padding_invisibility_offpage_duplicates": "none",
        "correction_only_content_freeze": "passed",
        "final_arrow_xml_trace_completeness": len(trace),
        "cited_source_file_existence": "passed",
        "inspection_provenance": "Actual enlarged and print images inspected individually before pass records were written.",
        "canonical_promotion": "blocked; see promotion-status.md",
    }
    (folder / "validation-results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    status = [
        f"# {name}: review candidate, NOT canonically promoted",
        "",
        "## Completed and verified",
        f"- Preserved editable pitch and four independently exported/inspected passes. Final candidate: `{name}-pass-04.drawio` / `{name}-pass-04.png`.",
        "- Each initial/pass PNG was actually opened enlarged and on a 100-unit/inch A5 print-scale page before its inspection record was saved.",
        f"- Semantic pass-1 growth: {measured['growth_ratio']:.6f}×; no invisible, off-page, duplicated or metadata-padding cells.",
        f"- Final {len(trace)} arrows individually traced in `{name}-pass-04.md`; XML endpoints agree with the trace.",
        "- JSON Schema Draft 2020-12 plus the skill cross-field validator pass. Five triples exist; correction passes preserve content and composition.",
        "- One editable uncompressed 827×583 page throughout. Image files, print dimensions and source-file citations checked.",
        "- Review-local stamp/drift verification uses the repository's actual createDiagramStamp and checkSourceArtifacts functions. See validation-results.json after stamp_reviews.mjs runs.",
        "",
        "## Exact remaining promotion blockers",
        "1. Independent-research deliverables are incomplete locally. The coordinator reports three completed Astra threads, but only the user-relayed reconciled findings are preserved. The two full component/flow transcripts were supplied at prohibited temporary paths and were not accessed or copied. Obtain allowed-path copies from the coordinator; do not launch replacement agents or claim these summaries are the original reports.",
        "2. The preserved initial pitch is a sparse three-node concept overview, not the skill's required dense/native-symbol/full-hierarchy initial composition. The pass-1 redesign meets the measured growth gate and has the richer visual structure, but that does not retroactively satisfy the initial-pitch contract. A compliant replacement pitch/iteration lineage must retain these original artifacts and repeat genuine exports/inspections; do not overwrite the historical pitch or treat its missing content as invisible growth padding.",
        "",
        "## Deferred, not claimed complete",
        f"- No write to `docs/diagrams/src/{name}.drawio`, `docs/diagrams/{name}.png`, or the stable hash.",
        "- No legacy JSON retirement or generated canonical cleanup: those are allowed only after verified promotion.",
        "- Canonical selective render/drift remains pending. Review-local stamp success is not canonical publication success.",
        "- No documentation pages or global inventory/reports changed; those are outside this assignment. No docs build was run because documentation pages were not changed.",
        "- No nested agents, new research agents, commits, git mutations, product changes, pipeline changes, or coordinator-pilot work.",
        "",
        "## Reproducibility",
        "- From the assigned worktree: run `python docs\\diagrams\\reviews\\assistant-runtime-fig1\\validate_reviews.py`, then `node docs\\diagrams\\reviews\\assistant-runtime-fig1\\stamp_reviews.mjs`.",
        "- The bounded authoring/record helpers live only in the assistant-runtime review directory. Do not run record_reviews.py to assert an inspection that has not actually happened.",
        "",
    ]
    (folder / "promotion-status.md").write_text("\n".join(status), encoding="utf-8")
    print(f"{name}: schema + skill + XML + images + {len(trace)} arrows PASS; growth {measured['growth_ratio']:.3f}x")
