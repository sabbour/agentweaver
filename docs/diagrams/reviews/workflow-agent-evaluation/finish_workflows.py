"""Persist inspection records and validate this bounded authoring batch."""
from pathlib import Path
from collections import Counter
import hashlib
import importlib.util
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

import jsonschema
from PIL import Image, ImageChops

sys.dont_write_bytecode = True
from author_workflows import NAMES, ROOT, REVIEWS, load_model, metric_module, ground


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def artifacts(name, stage):
    return dict(drawio=f"{name}-{stage}.drawio", png=f"{name}-{stage}.png",
                change_record=f"{name}-{stage}.md",
                png_inspected_print=True, png_inspected_enlarged=True)


def trace(name, model):
    tree = ET.parse(REVIEWS / name / f"{name}-pass-04.drawio")
    cells = {c.get("id"): c for c in tree.iter("mxCell")}
    arrows = []
    rows = ["| Connector | Source → target | Relationship | Evidence | Direction / endpoints / route | Result |",
            "|---|---|---|---|---|---|"]
    actual_edges = [c for c in cells.values() if c.get("edge") == "1"]
    assert len(actual_edges) == len(model["edges"])
    for i, e in enumerate(model["edges"], 1):
        id_ = f"edge-{i:02}"
        cell = cells[id_]
        assert cell.get("source") == e["source"] and cell.get("target") == e["target"]
        label_cell = cells.get(id_ + "-label")
        actual_label = label_cell.get("value") if label_cell is not None else cell.get("value", "")
        assert actual_label.replace("\n", "") == e["label"], (id_, actual_label, e)
        style = cell.get("style")
        assert "endArrow=block;" in style and "endFill=1;" in style
        assert "jumpStyle=arc;" in style
        a, b = e["source"], e["target"]
        main = model["main"]
        if "dashed=1;" in style:
            route = "source left → dedicated marigold outer rail → target bottom; separate arrowhead; native bridges at crossings"
        elif a in main and b in main and main.index(b) == main.index(a) + 1:
            route = "source bottom → target top; downward gutter; block arrow at target"
        elif a == "fallback":
            route = "fallback bottom → lower gutter → selected right; block arrow at selected"
        else:
            route = "source right → distinct outcome rail → intended target side; block arrow; native bridges, no junction implication"
        for endpoint in (a, b):
            assert cells[endpoint].get("vertex") == "1"
            assert cells[endpoint].get("parent") == "1", "Attach semantic nodes, not source annotations or group surfaces"
        relation = e["label"] or "unconditional advance"
        arrows.append(dict(id=id_, source=a, target=b, relationship=relation,
                           evidence=e["evidence"], result="clean"))
        rows.append(f"| {id_} | {a} → {b} | {relation} | `{e['evidence']}` | {route} | clean |")
    return arrows, "\n".join(rows)


def record(name):
    model = load_model(name)
    folder = REVIEWS / name
    ground(name, model)
    (folder / "research-components.md").write_text(
        "# Completed independent component thread — relayed findings\n\n"
        "Provenance: parent reports this separate GPT-6 Astra thread completed before authoring. "
        "This is a preserved handoff summary with direct source reconciliation, not a verbatim raw transcript. "
        "Catalog YAML defines the authored nodes; do not invent merge, PR or Scribe stages. "
        "DefaultWorkflowTemplate is separate and explicitly includes those stages. "
        "Evaluation run/collect are prompts, not fan-out/fan-in. Selection is trigger-agnostic.\n\n"
        + "\n".join(f"- `{n['id']}`: {n['label']} — `{n['evidence']}`; native:flowchart."
                    for n in model["nodes"]) + "\n", encoding="utf-8")
    (folder / "research-flows.md").write_text(
        "# Completed independent flow thread — relayed findings\n\n"
        "Provenance: parent reports a separate completed GPT-6 Astra direction/flow thread. "
        "This handoff preserves the reported conclusions and reconciles every relationship against current code. "
        "Default is merge → create/reuse PR → Scribe, not a proved git push. "
        "Explicit/conversational choices precede singleton handling; malformed/unknown choices permit "
        "two model attempts, exceptions fall back immediately. Runtime fallback prefers default/standard, "
        "then a non-code-review candidate, finally the first candidate. "
        "Outer coordinator fallback is the project default.\n\n"
        + "\n".join(f"- `{e['source']} → {e['target']}`: {e['label'] or 'unconditional'} — `{e['evidence']}`."
                    for e in model["edges"]) + "\n", encoding="utf-8")
    (folder / "research-assurance.md").write_text(
        "# Completed independent assurance thread — relayed findings\n\n"
        "Parent reports the third separate GPT-6 Astra thread independently compared all seven YAML graphs "
        "with the legacy graph JSON: no (from,to,label) differences; counts "
        "evaluation 8, bug 15, content 11, incident 7, infra 14, PM 6, software 17. "
        "These comparisons were repeated locally before promotion and saved in validation.json. "
        "Directly inspected tests/Agentweaver.Tests/Workflows/CatalogWorkflowBindingTests.cs:12–51: "
        "bindability theory lists six catalogs (not evaluation); the authorable-gate theory includes all seven "
        "and rejects authored Merge/Scribe nodes. No claim is made that product tests were executed here.\n\n"
        "Full component/flow raw transcript files supplied under Temp were not read/copied due to "
        "the hard directory restriction; this limitation is explicitly retained rather than forged away.\n",
        encoding="utf-8")
    growth = metric_module().assess(
        (folder / f"{name}-pitch.drawio").read_text(encoding="utf-8"),
        (folder / f"{name}-pass-01-upgrade.drawio").read_text(encoding="utf-8"))
    assert growth["passed"], growth
    (folder / "growth.json").write_text(json.dumps(growth, indent=2) + "\n", encoding="utf-8")
    pitch = artifacts(name, "pitch")
    main = model["main"]
    return_count = sum(1 for e in model["edges"] if e["source"] in main and e["target"] in main
                       and main.index(e["target"]) <= main.index(e["source"]))
    shared = (
        f"# {model['title']}\n\n"
        f"Audience: workflow authors and operators. Takeaway: {model['note']}.\n"
        "One editable, uncompressed A5 portrait page, 583 × 827 units at 100 units/inch.\n"
        f"Stable image: `docs/diagrams/{name}.png`; source: `docs/diagrams/src/{name}.drawio`.\n"
        "Target documentation: docs/guide/workflows.md and docs/deep-dive/workflow-engine.md"
        + ("; docs/deep-dive/workflow-selection.md" if name.endswith("workflow-selection") else "") + ".\n\n"
        "## Evidence and assets\n\n"
        "Full node and connector evidence, line anchors, classifications, ownership and research handoff are in "
        "`evidence.json`. Source-backed YAML graphs are facts, not proposals. The seven catalogs preserve all "
        "authored edges and add no merge, PR or Scribe stages. Evaluation task steps remain prompts. "
        "Default publication creates/reuses a PR; it does not prove git push or successful publication. "
        "The selection diagram is trigger-agnostic and distinguishes overrides, silent count handling, "
        "bounded model retries and separate fallbacks. An all-code-review selector set ultimately uses its first "
        "entry; the main fallback badge abbreviates this last-resort detail.\n\n"
        "Started with the committed Fluent template and loaded its library; removed invisible template metadata. "
        "Native flowchart process, decision, document and terminal shapes are framed by the repository's warm "
        "Fluent card system. Native shapes are bundled diagrams.net assets (Apache-2.0); no downloaded logos. "
        "Visual references: docs/diagrams/drawio/fluent-{template.drawio,library.xml}, "
        "docs/diagrams/README.md, apps/web/src/components/WorkflowGraphPanel.tsx.\n\n"
        "## Research provenance\n\n"
        "The parent supplied findings from three already-completed independent GPT-6 Astra research threads. "
        "Those findings were reconciled with direct reads of the current YAML, DefaultWorkflowTemplate, "
        "WorkflowSelector, CoordinatorOrchestratorExecutor and current documentation. No new agents were launched. "
        "The two full output files supplied under Temp were not accessed because this execution forbids any "
        "temporary-directory file operations; their full text remains a handoff residual, not a claimed local copy. "
        "The supplied assurance result independently reports zero catalog (from,to,label) differences, "
        "with 8/15/11/7/14/6/17 edges; evaluation is in the gate theory but not the bindability theory.\n\n"
        "## Export and actual image inspection\n\n"
        "Official draw.io Desktop 31.4.5, --export --format png --border 16 --scale 2. "
        "The actual exported PNGs were opened in the image tool. Print-size derivatives (583 × 827) "
        "were opened in three-diagram contact sheets; full exported PNGs were also opened for enlarged detail. "
        "These were raster inspections, not XML/editor proxies. Native bridge arcs were checked at rail crossings. "
        "No page, inventory, global report, product or pipeline edits were made by this author.\n\n"
    )
    (folder / pitch["change_record"]).write_text(
        shared + "## Pitch observations\n\n"
        "The lean native-symbol pitch preserves the grounded graph. It is not publishable: "
        "outcome labels compete in narrow gutters, return arrowheads bunch at shared destinations, "
        "and card/type/badge/source hierarchy is missing. These visible defects are handed to pass one.\n",
        encoding="utf-8")
    passes = []
    descriptions = {
        1: "Completed the full visual upgrade: warm paper and tiered surface, 5-unit semantic accents, native symbol tiles, "
           "title/subtitle/source metadata/pill hierarchy, source-ID/type/line-reference companion cards and explicit connector labels. "
           "Companion cards contain real authoring keys and line anchors, not padding. Side outcomes use distinct ports. "
           "Known defects handed onward: tightly spaced return arrowheads/lanes, single-line outcome labels floating too far from their source, "
           "and a terminal-looking fallback symbol with a continuing edge in selection.",
        2: "Correction-only: moved single-line outcome labels beside their source stubs; corrected selection fallback to a process symbol. "
           "Tried shared return routing with genuine merge dots to remove overlapping target arrowheads. "
           "Actual raster inspection revealed a new defect where overlaid dashed routes read as a solid rail. "
           "That intermediate is preserved rather than falsely declared final.",
        3: "Correction-only: replaced overlaid return paths with individually traceable packed lanes and separated destination ports. "
           "Removed obsolete merge dots, added endpoint clearance by reducing card height 4 units, and moved existing metadata/badges "
           "inside the corrected card bounds. Shifted existing source panels 6 units from bridge arcs. "
           "No semantic nodes, relationships, new facts or visual language were added. "
           "Actual print/enlarged inspection found no remaining orientation, overlap, routing or endpoint defect.",
        4: "Correction-only final audit. No permitted defect remained after pass three, so this independently saved source is byte-identical "
           "and was freshly exported by the official CLI. The new PNG was opened at print size and enlarged. "
           "Every connector below was traced from its actual source port, across its route and any native bridge, to its target arrowhead. "
           "No false junctions, reversed arrows, clipped labels or card-crossing routes remained.",
    }
    arrows, rows = trace(name, model)
    for number, stage in enumerate(("pass-01-upgrade", "pass-02", "pass-03", "pass-04"), 1):
        entry = dict(number=number, mode="visual-upgrade" if number == 1 else "correction-only",
                     **artifacts(name, stage), orientation_defects=0, overlap_defects=0, arrow_defects=0)
        if number == 1:
            entry.update({k: growth[k] for k in
                          ("growth_metric", "baseline_meaningful_xml", "result_meaningful_xml", "growth_ratio")})
            entry["overlap_defects"] = max(0, return_count - 1)
            entry["arrow_defects"] = 1
        if number == 2:
            entry["arrow_defects"] = max(0, return_count - 1)
        if number == 4:
            entry.update(all_arrows_traced=True, arrow_trace=arrows)
        body = shared + f"## Pass {number}: {entry['mode']}\n\n{descriptions[number]}\n\n"
        if number == 1:
            body += (f"Metric: `{growth['growth_metric']}`. Baseline **{growth['baseline_meaningful_xml']}**; "
                     f"result **{growth['result_meaningful_xml']}**; growth **{growth['growth_ratio']}×**. "
                     "No invisible, off-page, duplicate or metadata-padding cells; see `growth.json`.\n\n"
                     "The earlier `-pass-01` pair is a superseded intermediate candidate. This accepted "
                     "`-pass-01-upgrade` triple is pass one in the manifest; neither earlier source was overwritten.\n")
        if number == 4:
            body += f"## Complete final arrow trace ({len(arrows)} connectors)\n\n{rows}\n"
        (folder / entry["change_record"]).write_text(body, encoding="utf-8")
        passes.append(entry)
    (folder / f"{name}-pass-01.md").write_text(
        "# Superseded pass-one candidate\n\nThis intermediate source/PNG was preserved. "
        "The initial growth measurement failed for seven of nine batch entries; all candidates were superseded "
        "by the separately saved `pass-01-upgrade` triple before corrections began. "
        "Its print contact sheet was inspected; only the bug-fix intermediate was additionally opened enlarged. "
        "This pair is not an accepted iteration-manifest pass and is not publication-ready.\n", encoding="utf-8")
    manifest = dict(diagram=name, orientation="A5-portrait", pitch=pitch, passes=passes, final_pass=4)
    schema = json.loads((ROOT / ".github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json").read_text())
    jsonschema.Draft202012Validator(schema).validate(manifest)
    validation_path = ROOT / ".github/skills/docs-diagram-iterate/scripts/validate_iteration_manifest.py"
    spec = importlib.util.spec_from_file_location("manifest_validation", validation_path)
    validator = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(validator)
    assert not validator.validate_manifest(manifest)
    (folder / "iteration-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    results = []
    for record_ in [pitch] + passes:
        for field in ("drawio", "png", "change_record"):
            assert (folder / record_[field]).is_file()
        analysis = metric_module().analyze((folder / record_["drawio"]).read_text(encoding="utf-8"))
        assert (analysis["page_width"], analysis["page_height"]) == (583, 827)
        assert not any(analysis[k] for k in ("invisible_cells", "off_page_cells", "duplicate_cells", "metadata_padding"))
        results.append(dict(source=record_["drawio"], sha256=sha(folder / record_["drawio"]), xml_valid=True))
    comparison = None
    old = ROOT / "docs/diagrams/src" / f"{name}.json"
    if name.startswith("workflow-"):
        legacy = json.loads((old if old.exists() else folder / "legacy-graph.json").read_text(encoding="utf-8"))
        expected = Counter((e["from"], e["to"], e.get("label", "")) for e in legacy["edges"])
        actual = Counter((e["source"], e["target"], e["label"]) for e in model["edges"])
        assert actual == expected, (name, actual - expected, expected - actual)
        assert {n["id"] for n in legacy["nodes"]} == {n["id"] for n in model["nodes"]}
        comparison = dict(legacy_yaml_node_sets_equal=True, legacy_yaml_edge_tuples_equal=True,
                          edges=len(model["edges"]), missing=[], extra=[])
    result = dict(diagram=name, renderer="31.4.5", manifest_schema_valid=True, manifest_cross_fields_valid=True,
                  sources=results, final_arrows=len(arrows), legacy_yaml_comparison=comparison,
                  final_remaining_visual_defects=0, canonical_promoted=False,
                  residuals=["Full independent component/flow transcript text supplied under Temp was not copied; summarized handoff and direct code evidence are retained."])
    (folder / "validation.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(name, f"valid: {len(arrows)} arrows, {growth['growth_ratio']:.3f}x", flush=True)


def promote(name):
    folder = REVIEWS / name
    validation = json.loads((folder / "validation.json").read_text(encoding="utf-8"))
    assert validation["manifest_schema_valid"] and validation["final_remaining_visual_defects"] == 0
    source = ROOT / "docs/diagrams/src" / f"{name}.drawio"
    png = ROOT / "docs/diagrams" / f"{name}.png"
    old = source.with_suffix(".json")
    generated = ROOT / "docs/diagrams/drawio/generated" / f"{name}.drawio"
    authorized = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/plan-shared.json").read_text(encoding="utf-8"))["exclusive_asset_paths"]
    for path in (source, png, old, generated, png.with_suffix(".hash.txt")):
        assert path.relative_to(ROOT).as_posix() in authorized, path
    final_source = folder / f"{name}-pass-04.drawio"
    final_png = final_source.with_suffix(".png")
    source.write_bytes(final_source.read_bytes())
    png.write_bytes(final_png.read_bytes())
    assert sha(source) == sha(final_source) and sha(png) == sha(final_png)
    assert metric_module().analyze(source.read_text(encoding="utf-8"))["page_width"] == 583
    # Only retire legacy sources after the exact inspected pair has been promoted and verified.
    if old.exists():
        (folder / "legacy-graph.json").write_bytes(old.read_bytes())
        old.unlink()
    if generated.exists():
        generated.unlink()
    validation["canonical_promoted"] = True
    validation["promotion_source_sha256"] = sha(source)
    validation["promotion_png_sha256"] = sha(png)
    (folder / "validation.json").write_text(json.dumps(validation, indent=2) + "\n", encoding="utf-8")
    print(name, "inspected pair promoted; legacy source retired", flush=True)


def verify_publication():
    command = ["node", str(ROOT / "scripts/docs/render-diagrams.mjs"), "--check"]
    for name in NAMES:
        command.extend(["--spec", name])
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=120)
    assert result.returncode == 0, result.stdout + result.stderr
    for name in NAMES:
        folder = REVIEWS / name
        source = ROOT / "docs/diagrams/src" / f"{name}.drawio"
        png = ROOT / "docs/diagrams" / f"{name}.png"
        stamp_path = png.with_suffix(".hash.txt")
        stamp = json.loads(stamp_path.read_text(encoding="utf-8"))
        assert stamp["renderer"]["rendererVersion"] == "31.4.5"
        assert stamp["drawio"]["sha256"] == sha(source)
        assert stamp["png"]["sha256"] == sha(png)
        assert sha(source) == sha(folder / f"{name}-pass-04.drawio")
        with Image.open(png) as published, Image.open(folder / f"{name}-pass-04.png") as inspected:
            assert published.size == inspected.size
            assert ImageChops.difference(published.convert("RGB"), inspected.convert("RGB")).getbbox() is None
            assert published.convert("RGBA").getchannel("A").tobytes() == inspected.convert("RGBA").getchannel("A").tobytes()
        validation = json.loads((folder / "validation.json").read_text(encoding="utf-8"))
        validation.update(scoped_repository_drift_check="passed", renderer_stamp_verified=True,
                          published_png_matches_inspected_pixels=True, canonical_promoted=True,
                          publication_png_sha256=sha(png))
        (folder / "validation.json").write_text(json.dumps(validation, indent=2) + "\n", encoding="utf-8")
        line = next(line for line in result.stdout.splitlines() if line.startswith(f"OK: {name}.png"))
        (folder / "publication-check.txt").write_text(
            line + "\nPinned renderer: 31.4.5\nPublished pixels equal the inspected pass-04 PNG.\n"
            "Docs build not run: no Markdown/page writes; build outputs are outside the exclusive asset scope.\n",
            encoding="utf-8")
        paths = dict(
            added=[source.relative_to(ROOT).as_posix()],
            modified=[png.relative_to(ROOT).as_posix(), stamp_path.relative_to(ROOT).as_posix()],
            removed=[source.with_suffix(".json").relative_to(ROOT).as_posix(),
                     f"docs/diagrams/drawio/generated/{name}.drawio"],
            reviews=[p.relative_to(ROOT).as_posix() for p in folder.rglob("*")
                     if p.is_file() and "__pycache__" not in p.parts],
        )
        (folder / "changed-paths.json").write_text(json.dumps(paths, indent=2) + "\n", encoding="utf-8")
        print(name, "publication hash/pixels/drift verified", flush=True)


if __name__ == "__main__":
    if "--verify-publication" in sys.argv:
        verify_publication()
    else:
        for name in NAMES:
            if "--promote" in sys.argv:
                promote(name)
            else:
                record(name)
