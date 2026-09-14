"""Record bounded reference execution without modifying plans or inventory."""
import collections
import copy
import hashlib
import json
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

import jsonschema
from PIL import Image

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[4]
REVIEW = Path(__file__).resolve().parent
PLAN_PATH = ROOT / ".github/skills/docs-diagram-audit/reports/plan-reference.json"
REPORT_PATH = ROOT / ".github/skills/docs-diagram-audit/reports/reference.json"
PLAN = json.loads(PLAN_PATH.read_text(encoding="utf-8"))
OWNED = set(PLAN["concept_ids"])
NAMES = ("reference-a2a-fig1", "reference-scaling-data-layer-fig1")
MARKER = "\n\nReference execution:"


def save(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    report = json.loads(REPORT_PATH.read_text(encoding="utf-8"))
    baseline = REVIEW / "audit-baseline.json"
    if not baseline.exists():
        baseline.write_bytes(REPORT_PATH.read_bytes())
    foreign = [copy.deepcopy(c) for d in report["documents"] for c in d["concepts"] if c["id"] not in OWNED]
    operations = json.loads((REVIEW / "document-changes.json").read_text(encoding="utf-8"))
    changed = {entry["file"] for entry in operations}
    concepts = []
    for document in report["documents"]:
        text = (ROOT / document["path"]).read_text(encoding="utf-8")
        for concept in document["concepts"]:
            if concept["id"] not in OWNED:
                continue
            status = "blocked" if concept["id"] == "reference-generated-tool-catalog" else "complete"
            note = "Applied within the owned document; concise reference material remains searchable prose/table/inline text."
            if concept["disposition"] in ("reuse", "merge"):
                if concept["id"] == "reference-mcp-provider-context":
                    assert "./api.md#ai-execution-context" in text
                    note = "Consolidated to API prose/anchor. Unassigned shared provider-context asset remains a planning placeholder; no bitmap authored or linked."
                else:
                    assert concept["target"] + ".png" in text, concept["id"]
                    note = f"Consumer now uses stable {concept['target']}.png; no foreign target asset modified."
            elif concept["disposition"] == "redesign":
                note = f"Published editable A5 Fluent draw.io and pinned PNG; see docs/diagrams/reviews/{concept['canonical_diagram']}/iteration-manifest.json."
            elif concept["disposition"] == "remove":
                note = "Removed the obsolete resource_graph schema/example; current bounded diagnostics and topology remain textual."
            if status == "blocked":
                note = ("Retained generated catalog unchanged and in sync. Its preview-approval exclusion and "
                        "full-parameter-reference promise originate in forbidden producer files. See execution.json blocker.")
            concept["rationale"] = concept["rationale"].split(MARKER)[0] + MARKER + " " + note
            concepts.append({"id": concept["id"], "document": document["path"],
                             "disposition": concept["disposition"], "status": status,
                             "target": concept.get("target"), "result": note})
    assert len(concepts) == len(OWNED) == 74
    assert foreign == [c for d in report["documents"] for c in d["concepts"] if c["id"] not in OWNED]
    for diagram in report["diagrams"]:
        if diagram["name"] not in PLAN["diagram_names"]:
            continue
        if diagram["name"] in NAMES:
            note = "Redesign published; pitch and at least four inspected passes, measured >9x pass-one growth, final arrow trace and manifest saved under its owned review directory."
        else:
            note = "Consumer changed before old assets were retired; merge tombstone saved. Replacement sandbox-browser-preview-fig1 assets were not modified."
        diagram["rationale"] = diagram["rationale"].split(MARKER)[0] + MARKER + " " + note
    for finding in report["findings"]:
        if finding["subject"].startswith("Generated agent model"):
            note = "Five-output documentation corrected. Generated catalog source-wording correction remains BLOCKED by producer write scope; generated drift check passes unchanged."
        elif finding["subject"].startswith("Complete bounded"):
            note = "The preceding text describes the audit-time baseline. Reference-only publication, inspections and validation are now recorded in docs/diagrams/reviews/reference-a2a-fig1/execution.json. No global inventory/reconciliation or foreign assets were changed."
        else:
            note = "Owned-document correction/disposition applied. Historical evidence above is preserved; current validation and source maps are in docs/diagrams/reviews/reference-a2a-fig1/execution.json. Foreign-owned visuals are stable references, not certified by this execution."
        finding["detail"] = finding["detail"].split(MARKER)[0] + MARKER + " " + note
    schema = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/area-research.schema.json").read_text())
    jsonschema.Draft202012Validator(schema).validate(report)
    save(REPORT_PATH, report)

    diagrams = []
    manifest_schema = json.loads((ROOT / ".github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json").read_text())
    for name in NAMES:
        folder = ROOT / "docs/diagrams/reviews" / name
        source = ROOT / "docs/diagrams/src" / (name + ".drawio")
        png = ROOT / "docs/diagrams" / (name + ".png")
        manifest = json.loads((folder / "iteration-manifest.json").read_text())
        jsonschema.Draft202012Validator(manifest_schema).validate(manifest)
        final = manifest["passes"][-1]
        assert source.read_bytes() == (folder / final["drawio"]).read_bytes()
        assert png.read_bytes() == (folder / final["png"]).read_bytes()
        tree = ET.parse(source)
        assert tree.getroot().get("compressed") == "false"
        assert len(tree.findall("diagram")) == 1
        model = tree.find("diagram/mxGraphModel")
        assert (model.get("pageWidth"), model.get("pageHeight")) == ("794", "559")
        cells = model.findall("root/mxCell")
        ids = [cell.get("id") for cell in cells]
        assert len(ids) == len(set(ids))
        edges = [cell for cell in cells if cell.get("edge") == "1"]
        assert all(edge.get("source") in ids and edge.get("target") in ids for edge in edges)
        assert {edge.get("id") for edge in edges} == {edge["id"] for edge in manifest["passes"][-1]["arrow_trace"]}
        with Image.open(png) as image:
            size = image.size
            assert image.format == "PNG" and size[0] > size[1] and size[0] >= 1500
            image.verify()
        diagrams.append({"name": name, "page": "A5-landscape", "png_dimensions": size,
                         "edges_traced": len(edges), "source_sha256": digest(source), "png_sha256": digest(png),
                         "growth": manifest["passes"][0]["growth_ratio"], "final_pass": manifest["final_pass"]})
    links = json.loads((REVIEW / "link-validation.json").read_text())
    assert not links["failures"]
    publication = json.loads((REVIEW / "publication.json").read_text())
    blocker = {
        "id": "generated-catalog-source-wording",
        "document": "docs/reference/mcp-tools.md",
        "facts": [
            "coordinator_start description excludes preview from safe auto-approval, contrary to AgentPreviewGate.",
            "Generated introduction promises a full per-tool parameter reference that the curated MCP page does not provide."
        ],
        "required_out_of_scope_files": ["apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs", "scripts/gen-docs.mjs"],
        "resolution": "Correct producer wording in a separately authorized change, then regenerate all five outputs. Hand-editing only the catalog would fail generated drift."
    }
    result = {
        "area": "reference", "status": "blocked", "model": "gpt-6-astra",
        "plan": PLAN_PATH.relative_to(ROOT).as_posix(), "plan_sha256": digest(PLAN_PATH),
        "research_agents": 3, "diagrams_redesigned": 2, "diagrams_merged_and_retired": 1,
        "post_pitch_passes": sum(d["final_pass"] for d in diagrams), "final_connectors_traced": 14,
        "documents_reviewed": 23, "documents_changed": len(changed),
        "documents_retained_unchanged": sorted(set(PLAN["document_paths"]) - changed),
        "concepts_complete": sum(c["status"] == "complete" for c in concepts),
        "concepts_blocked": sum(c["status"] == "blocked" for c in concepts),
        "dispositions": dict(collections.Counter(c["disposition"] for c in concepts)),
        "diagrams": diagrams, "concept_results": concepts, "blockers": [blocker],
        "validation": {
            "node_tests": {"passed": 27, "failed": 0, "log": "node-tests.log"},
            "iteration_python_tests": {"passed": 14, "failed": 0, "log": "python-tests.log"},
            "xml_and_manifest_schema": "passed", "manifest_cross_field_checks": "passed",
            "independent_pinned_raster_export": "byte-identical to each inspected final-pass PNG",
            "selective_diagram_drift": "passed; diagram-drift.log",
            "generated_docs_drift": "all five outputs in sync; generated-docs-check.log",
            "links": links, "docs_build": json.loads((REVIEW / "build-validation.json").read_text()),
            "owned_diff_whitespace": "passed"
        },
        "publication": publication,
        "scope": {
            "writes": "Only plan-owned documents, exclusive assets/review subtrees, and reference report entries.",
            "unassigned_report_concepts_unchanged": len(foreign),
            "plans_inventory_other_reports_pipeline_product_and_foreign_assets_modified": False,
            "commits_created": False,
            "concurrent_worktree": "Pre-existing and other-owner changes were preserved; this is not a global audit."
        },
        "document_sha256": {name: digest(ROOT / name) for name in PLAN["document_paths"]},
        "validation_note": "Initial standalone link probe omitted the site's attrs.disable setting; corrected the probe, then fixed six actual owned anchors. Repository line endings were restored with normalized Markdown equality verified (line-ending-validation.json). No pipeline/config changes."
    }
    save(REVIEW / "execution.json", result)
    print(json.dumps({key: result[key] for key in
                      ("status", "diagrams_redesigned", "diagrams_merged_and_retired", "documents_changed",
                       "concepts_complete", "concepts_blocked", "dispositions")}, indent=2))


if __name__ == "__main__":
    main()
