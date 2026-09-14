"""Summarize observed results without advancing incomplete publication gates."""
import hashlib
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

from jsonschema import Draft202012Validator

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
sys.path.insert(0, str(ROOT / ".github/skills/docs-diagram-iterate/scripts"))
from check_xml_growth import assess


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


validation = read(HERE / "catalog-validation.json")
readiness = read(HERE / "publication-readiness.json")
promotions = read(HERE / "promotions.json")
retirements = read(HERE / "retirements.json")
checks = read(HERE / "completion-checks.json")
assert len(checks) == 12 and all(c["exit_code"] == 0 for c in checks), checks
assert validation["status"] == "passed"
assert not readiness["blocked"]
reconciliation = read(HERE / "completion-reconciliation.json")
audit = read(ROOT / "docs-diagram-audit.json")
assert digest(ROOT / "docs-diagram-audit.json") == validation["audit_sha256"]
assert all(not p["missing_proposed"] and not p["candidate_statuses"].get("blocked-review") for p in validation["plans"])
assert len(re.findall(r"^OK:", (HERE / "final-full-drift.log").read_text(encoding="utf-8-sig"), re.M)) == validation["public_source_count"]
assert len(re.findall(r"^Rendered ", (HERE / "final-full-render.log").read_text(encoding="utf-8-sig"), re.M)) == validation["public_source_count"]
original_audit = read(HERE / "originals/audit-before-completion.json")
current_by_name = {d["name"]: d for d in audit["diagrams"]}
for original in original_audit["diagrams"]:
    assert all(original[key] == current_by_name[original["name"]][key]
               for key in ("disposition", "target", "concept_id")), original["name"]

node_log = (HERE / "final-node-tests.log").read_text(encoding="utf-8-sig")
node_count = int(re.search(r"(?:# |\u2139 )tests (\d+)", node_log).group(1))
skill_counts = {
    skill: int(re.search(r"Ran (\d+) tests", (HERE / f"final-{skill}-tests.log").read_text(encoding="utf-8-sig")).group(1))
    for skill in ("pitch", "iterate", "audit")
}
execution_evidence = []
for manifest_path in sorted((ROOT / "docs/diagrams/reviews").glob("*/completion-20260913/iteration-manifest.json")):
    manifest = read(manifest_path)
    pitch = (manifest_path.parent / manifest["pitch"]["drawio"]).read_text(encoding="utf-8")
    first = (manifest_path.parent / manifest["passes"][0]["drawio"]).read_text(encoding="utf-8")
    growth = assess(pitch, first)
    assert growth["passed"], (manifest["diagram"], growth)
    final = manifest["passes"][-1]
    source = ROOT / "docs/diagrams/src" / (manifest["diagram"] + ".drawio")
    png = ROOT / "docs/diagrams" / (manifest["diagram"] + ".png")
    final_source = manifest_path.parent / final["drawio"]
    assert source.read_bytes().replace(b"\r\n", b"\n") == final_source.read_bytes().replace(b"\r\n", b"\n"), manifest["diagram"]
    assert digest(png) == digest(manifest_path.parent / final["png"])
    cells = ET.parse(source).findall('.//mxCell[@edge="1"]')
    leaders = [c for c in cells if c.get("fluentRole") == "label-leader"]
    assert all("endArrow=none;" in c.get("style", "") and "startArrow=none;" in c.get("style", "") for c in leaders)
    edges = {c.get("id"): (c.get("source"), c.get("target")) for c in cells if c not in leaders}
    traces = {t["id"]: ("node-" + t["source"], "node-" + t["target"]) for t in final["arrow_trace"]}
    assert edges == traces, manifest["diagram"]
    execution_evidence.append({"name": manifest["diagram"], "growth_ratio": growth["growth_ratio"],
                               "anti_padding_passed": True, "arrows_traced": len(traces),
                               "non_arrow_label_leaders": len(leaders),
                               "source_byte_identical": digest(source) == digest(final_source),
                               "source_identical_after_crlf_normalization": True,
                               "public_source_sha256": digest(source), "final_pass_source_sha256": digest(final_source),
                               "manifest": manifest_path.relative_to(ROOT).as_posix()})
assert len(execution_evidence) == 20
for promotion in promotions:
    assert digest(ROOT / f"docs/diagrams/src/{promotion['name']}.drawio") == promotion["source_sha256"]
    assert digest(ROOT / f"docs/diagrams/{promotion['name']}.png") == promotion["png_sha256"]
assert digest(ROOT / "docs/diagrams/canonical-coordinator-architecture.png") == digest(HERE / "pilot-compatibility/corrected.png")
reports = ROOT / ".github/skills/docs-diagram-audit/reports"
schema_results = []
for schema_name in ("area-research", "coverage", "reconciliation"):
    schema = read(reports / f"{schema_name}.schema.json")
    names = ("shared", "deep-dive-core", "deep-dive-execution",
             "deep-dive-orchestration", "experience", "guide", "reference"
             ) if schema_name == "area-research" else (schema_name,)
    for name in names:
        errors = [e.message for e in Draft202012Validator(schema).iter_errors(
            read(reports / f"{name}.json"))]
        schema_results.append({"report": name, "errors": errors})
for filename, schema_path in (
    ("docs-diagram-audit.json", ".github/skills/docs-diagram-audit/references/audit-report.schema.json"),
    ("docs-diagram-inventory.json", ".github/skills/docs-diagram-pitch/references/diagram-inventory.schema.json"),
):
    errors = [e.message for e in Draft202012Validator(read(ROOT / schema_path)).iter_errors(read(ROOT / filename))]
    schema_results.append({"report": filename, "errors": errors})

derived = []
for name in ("email-architecture", "email-components", "email-coordinator-workflow"):
    canonical = ROOT / f"docs/diagrams/{name}.png"
    copy = ROOT / f"docs/diagrams/email-exports/{name}.png"
    derived.append({"path": str(copy), "canonical": str(canonical),
                    "sha256": digest(copy), "matches_canonical": digest(copy) == digest(canonical)})

assert not any(item["errors"] for item in schema_results)
assert all(item["matches_canonical"] for item in derived)
assert not read(HERE / "render-verification.json")["failures"]
assert validation["summary"]["new_broken_local_links_vs_head"] == 0

result = {
    "status": "done",
    "model": "gpt-6-astra",
    "sole_agent": True,
    "subagents": 0,
    "commits": 0,
    "runtime_changes": 0,
    "calibration": {
        "status": "user-approved",
        "comparison": str(ROOT / "docs/diagrams/reviews/canonical-default-workflow/style-calibration/comparison-final.png"),
        "marker": "classicThin",
        "marker_size": 8,
    },
    "integration_counts": {
        "published_canonical_triples": len({p["name"] for p in promotions}),
        "merged_families": len({p["name"] for p in retirements}),
        "retired_public_or_generated_files": len(retirements),
        "restored_public_asset_files": 0,
        "restored_consumer_embeds": 1,
        "synchronized_derived_png_copies": len(derived),
        "evidence_blocked_diagrams": len(readiness["blocked"]),
        "protected_pilot": 1,
        "total_public_canonicals": validation["public_source_count"],
        "execution_diagrams_completed_in_final_resume": len(execution_evidence),
    },
    "public_asset_delta_vs_head_including_stopped_shards":
        validation["public_asset_counts_vs_head_including_stopped_shards"],
    "all_diagram_doc_tooling_path_counts_including_stopped_shards":
        validation["worktree_counts_vs_head_including_stopped_shards"],
    "exact_changed_paths": "catalog-validation.json#worktree_changes_vs_head_including_stopped_shards",
    "planned_dispositions": reconciliation["dispositions"],
    "original_audited_dispositions": reconciliation["original_audited_dispositions"],
    "original_dispositions_and_targets_preserved": True,
    "execution_completion_evidence": execution_evidence,
    "execution_growth_range": [min(e["growth_ratio"] for e in execution_evidence), max(e["growth_ratio"] for e in execution_evidence)],
    "execution_arrows_traced": sum(e["arrows_traced"] for e in execution_evidence),
    "derived_exports": derived,
    "restored_consumer": "docs/deep-dive/assistant-runtime.md",
    "validation": {
        "node_tests": {"passed": node_count, "log": "final-node-tests.log"},
        "skill_tests": skill_counts,
        "total_tests": node_count + sum(skill_counts.values()),
        "commands": checks,
        "catalog": validation["summary"],
        "research_coverage_reconciliation_schemas": schema_results,
        "audit_schema": {"status": "passed", "diagrams": len(audit["diagrams"])},
        "current_inventory_schema": {"status": "passed", "diagrams": validation["public_source_count"], "path": "docs-diagram-inventory.json"},
        "audit_final_gate": {"status": "passed", "log": "final-audit-final.log"},
        "published_rerender_pixels": read(HERE / "render-verification.json"),
        "full_render": {"status": "passed", "count": validation["public_source_count"], "renderer": "31.4.5", "log": "final-full-render.log"},
        "full_drift": {"status": "passed", "current": validation["public_source_count"], "stale": 0, "log": "final-full-drift.log"},
        "docs_build": {"status": "passed", "log": "final-docs-build.log"},
        "diff_check": {"status": "passed", "log": "final-diff-check.log"},
        "unreferenced_public_diagrams": validation["unreferenced_public_diagrams"],
        "redundant_generated_drawio": validation["redundant_generated_drawio"],
        "readme_visuals": validation["readme_visuals"],
    },
    "shards": [{
        "area": p["area"],
        "status": "blocked" if p["candidate_statuses"].get("blocked-review") or p["missing_proposed"] else "done",
        "candidate_statuses": p["candidate_statuses"],
        "missing_proposed": p["missing_proposed"],
    } for p in validation["plans"]],
    "residuals": readiness["blocked"],
    "global_audit_reports_regenerated": True,
    "global_audit_residual": None,
    "protected_pilot": read(HERE / "pilot-compatibility/verification.json"),
    "scope_notes": [
        "The 20 incomplete historical sequences are preserved, not relabeled; new honest pitches and separately inspected four-pass sequences now qualify.",
        "Pass-1 growth was remeasured with invisible/off-page/duplicate/padding detection; every final execution XML arrow matches the saved trace endpoints.",
        "Execution pass files use Windows CRLF while the published staged sources use LF. Equality is checked after only CRLF normalization; both raw digests are recorded.",
        "Nineteen legacy replacements and canonical-pod-process-boundaries are published at their stable canonical paths.",
        "The historical orchestration report received citation line ranges only; its research and findings were not regenerated.",
        "The pilot's copy, geometry, endpoints and routes remain exact; marker-driven re-rasterization is transparently measured, not described as pixel-identical.",
        "All seven plan shards are reconciled. Existing unrelated link failures are retained with HEAD-baseline evidence; no new broken links.",
    ],
}
(HERE / "integration-result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps(result["integration_counts"], indent=2))
