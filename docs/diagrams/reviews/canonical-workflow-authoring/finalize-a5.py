"""Record the coordinator's completed PNG inspections and promote the corrected lineage."""
import importlib.util
import json
from pathlib import Path
import shutil

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
OUT = HERE / "a5-final"
old_manifest = HERE / "iteration-manifest.json"
archive = HERE / "oversized-iteration-manifest.json"
assert not archive.exists(), "Do not overwrite the historical manifest"
shutil.copyfile(old_manifest, archive)
manifest = json.loads(archive.read_text())
spec = importlib.util.spec_from_file_location(
    "growth", ROOT / ".github/skills/docs-diagram-iterate/scripts/check_xml_growth.py")
growth = importlib.util.module_from_spec(spec)
spec.loader.exec_module(growth)
notes = [
    "Initial pitch: two native-symbol, full-hierarchy Fluent responsibility cards. "
    "Opened actual exported PNG and 583x827 print view; labels and explicit Save arrow are legible. "
    "This replaces the invalid 1123x1587 page lineage without overwriting it.",
    "Visual upgrade: expand the two responsibility units into source-backed generation, validation, "
    "repair, review, save, write and reload details. Actual PNG and print view inspected. "
    "Found component/title overlaps, error text/badge collisions, group-title routing and repair-lane conflicts.",
    "Corrections only: separate error metadata, move the lower group title off the Save arrow, "
    "and route repair below the prompt-input lane. Actual output inspected at both sizes. "
    "Two component/title overlaps and the invalid-again endpoint still need correction.",
    "Corrections only: fix the invalid-again endpoint and error-card collision. Actual output "
    "inspected at both sizes. Two UML component symbols still extend into their title space.",
    "Corrections only: move Generate candidate and Sync titles 12 logical units right to clear "
    "the native UML glyph overhang. Actual output inspected at both sizes. Full 13-arrow trace "
    "completed; no remaining orientation, overlap, endpoint, label or routing defect.",
]
stages = [manifest["pitch"], *manifest["passes"]]
for index, stage in enumerate(stages):
    for key in ("drawio", "png", "change_record"):
        assert not stage[key].startswith("a5-")
        stage[key] = "a5-final/" + stage[key]
    record = HERE / stage["change_record"]
    text = (
        f"# Workflow authoring: {'pitch' if index == 0 else f'pass {index:02}'} on true A5\n\n"
        + notes[index]
        + "\n\nSource is uncompressed editable XML on one 583x827 A5 portrait page. "
        "Export: draw.io Desktop 31.4.5, border 16, scale 2. Export DPI is not page size. "
        "Rounded warm cards, semantic accents, native flowchart/UML symbols, badges, Segoe UI "
        "and monospaced metadata are retained. No new content after pass 1.\n\n"
        "The original larger-page triples remain at the review root. The first rescaling "
        "probe in a5-corrected omitted HTML font scaling and visibly failed; it is not a pass "
        "in this accepted lineage. All five a5-final images were subsequently exported and "
        "opened individually at print and enlarged size before this record was generated.\n\n"
        "Grounding: original research is preserved in ../canonical-provider-admission/"
        "research-thread-01.md, research-thread-02.md and research-thread-03.md. "
        "The root pitch and pass-04 records retain the detailed node evidence and native-symbol credits.\n"
    )
    if index:
        stage["orientation_defects"] = 0
        stage["overlap_defects"] = {1: 5, 2: 2, 3: 2, 4: 0}[index]
        stage["arrow_defects"] = {1: 1, 2: 1, 3: 0, 4: 0}[index]
    if index == 1:
        baseline = growth.analyze((HERE / stages[0]["drawio"]).read_text())["meaningful_count"]
        result = growth.analyze((HERE / stage["drawio"]).read_text())["meaningful_count"]
        assert result / baseline >= 9
        stage.update(baseline_meaningful_xml=baseline, result_meaningful_xml=result,
                     growth_ratio=result / baseline)
        text += f"\nMeaningful XML: {baseline} -> {result}, {result/baseline:.6f}x, visible-semantic-canonical-xml-v1.\n"
    if index == 4:
        text += "\n## Final every-arrow trace\n\n"
        text += "| ID | Source -> target | Relationship | Evidence | Result |\n| --- | --- | --- | --- | --- |\n"
        for arrow in stage["arrow_trace"]:
            text += f"| {arrow['id']} | {arrow['source']} -> {arrow['target']} | {arrow['relationship']} | {arrow['evidence']} | clean |\n"
        text += (
            "\nTraced e01 down request-to-generator, e02 left/down/left input-to-generator, "
            "e03 down candidate-to-validation, e04 down valid-to-review, e05 right first-invalid-to-repair, "
            "e06 outer right/up/left one-repair return, e07 right/down/right second-invalid-to-error, "
            "e08 down explicit Save across the responsibility boundary, e09 right save rejection, "
            "e10 down successful validation-to-write, e11 right write failure, e12 down write-to-registry, "
            "and e13 right reload failure. Heads and endpoints attach to the correct cards, not "
            "icons/groups; labels stay in gutters. No crossings require a bridge; no fake junctions. "
            "Only the semantic repair return is dashed marigold.\n"
        )
    record.write_text(text, encoding="utf-8")
old_manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
shutil.copyfile(HERE / manifest["passes"][-1]["drawio"], ROOT / "docs/diagrams/src/canonical-workflow-authoring.drawio")
print("Corrected lineage recorded; canonical XML promoted. Re-export canonical PNG/stamp before validating.")
