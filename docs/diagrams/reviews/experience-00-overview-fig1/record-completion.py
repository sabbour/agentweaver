"""Persist execution evidence for plan-experience only."""
import json
import re
import runpy
import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
V = runpy.run_path(str(HERE / "validate-experience.py"))
ROOT, PLAN, MODELS = V["ROOT"], V["PLAN"], V["MODELS"]
REPORT_PATH = ".github/skills/docs-diagram-audit/reports/experience.json"
PLAN_PATH = ".github/skills/docs-diagram-audit/reports/plan-experience.json"

FINDING_RESOLUTIONS = [
    "Published broker-only OAuth visual and corrected browser/assistant/MCP credentials; no raw Entra/GitHub/API-key authentication fallback. No runtime changes; shared authentication assets remain their owners' responsibility.",
    "Removed misleading placeholder and incoherent/outdated capture references in owned pages. Replaced them with source-grounded prose, not fabricated captures. Retained only qualified real casting-proposal and cropped-board examples. Legacy screenshot binaries remain untouched.",
    "Corrected four-lane plus attention layout and differentiated Define Outcome, Direct and unattended pickup. References use declared shared stable assets without editing them.",
    "Published visible incomplete/transport failure and policy-directed recovery, with checkpoint waits separate; removed transparent/exactly-once replay claims.",
    "Replaced obsolete loopback preview figure with declared Gateway canonical; corrected 1440-minute lifetime/hard cap, keepalive, preview verdicts and conditional resource retention.",
    "Corrected same-run versus fresh-run retry, human-review escalation, collective revision caps and renewed human-request counters.",
    "Published current worker HPA 2-3, CPU 70%/memory 80%, web baseline 2; identified KEDA/web HPA as commented proposals, not deployed evidence.",
    "Published database-authoritative context with approved/high-importance learning-or-pattern eligibility; exports are mirrors and prompt content remains untrusted.",
    "Updated current Account/Platform/Project settings, navigation, selected-task sessions, Cluster historical capacity states and Team Memory surfaces.",
    "Corrected renewable broker credentials, Repo App-authorized repository selection, best-effort Driver materialization and generated-catalog links instead of stale hard-coded counts.",
    "Documented optional recorded sessionId, span hierarchy and truthful missing-data/query-error states without invented screenshots.",
    "Applied three merges and two shared reuses, removed five obsolete local identities, retained the small decision-governance Mermaid and removed duplicate run-state Mermaid.",
    "Published 14 editable uncompressed A5 sources with Desktop 31.4.5, three bounded Astra researchers, inspected pitches and four passes, measured >=9x growth, complete arrow traces and schema-valid manifests.",
]


def main():
    artifacts = json.loads((HERE / "artifact-validation.json").read_text())
    links = json.loads((HERE / "consumer-link-validation.json").read_text())
    assert len(artifacts) == 14 and not links["issues"]
    assert all(v["publication"] == "canonical-source-identical-and-render-pixel-identical" for v in artifacts.values())
    assert "build complete" in (HERE / "docs-build.log").read_text(encoding="utf-8")
    assert "pass 19" in (HERE / "focused-tests.log").read_text(encoding="utf-8")
    assert (HERE / "render-drift.log").read_text(encoding="utf-8").count("OK: ") == 14
    assert (HERE / "exporter-provenance.json").is_file()
    assert not (HERE / "export-tool").exists()
    audit = json.loads((ROOT / REPORT_PATH).read_text())
    assert len(FINDING_RESOLUTIONS) == len(audit["findings"])
    concept_results = []
    for document in audit["documents"]:
        assert document["path"] in PLAN["document_paths"]
        for concept in document["concepts"]:
            name = concept.get("canonical_diagram")
            if concept["representation"] == "screenshot":
                outcome = ("retained-qualified-real-example" if concept["id"] in
                           {"experience-board-capture", "experience-casting-proposal-capture"}
                           else "misleading-capture-reference-removed-grounded-prose-retained")
            elif concept["disposition"] == "merge":
                outcome = "consolidated-consumer"
            elif name in MODELS:
                outcome = "reviewed-diagram-published"
            elif name:
                outcome = "declared-shared-canonical-reused-without-asset-writes"
            else:
                outcome = "retained-and-corrected-from-current-evidence"
            concept_results.append({"id": concept["id"], "document": document["path"],
                                    "planned_disposition": concept["disposition"], "outcome": outcome,
                                    "canonical_diagram": name})
    assert set(PLAN["concept_ids"]) <= {c["id"] for c in concept_results}
    old_screenshots, current_screenshots = set(), set()
    for path in PLAN["document_paths"]:
        original = subprocess.run(["git", "show", f"HEAD:{path}"], cwd=ROOT, check=True,
                                  capture_output=True, text=True, encoding="utf-8").stdout
        old_screenshots.update(re.findall(r"/screenshots/[\w.-]+\.png", original))
        current_screenshots.update(re.findall(r"/screenshots/[\w.-]+\.png", (ROOT / path).read_text(encoding="utf-8")))
    paths = subprocess.run(["git", "diff", "--name-only", "--", *PLAN["document_paths"],
                            *[p for p in PLAN["exclusive_asset_paths"] if not p.endswith("/")]],
                           cwd=ROOT, check=True, capture_output=True, text=True).stdout.splitlines()
    for name in MODELS:
        paths += [p.relative_to(ROOT).as_posix() for p in (V["REVIEWS"] / name).rglob("*")
                  if p.is_file() and "export-tool" not in p.parts and "__pycache__" not in p.parts]
        paths.append(f"docs/diagrams/src/{name}.drawio")
    paths += [REPORT_PATH, PLAN_PATH]
    output = {
        "status": "completed-owned-scope", "model": "gpt-6-astra", "plan": PLAN_PATH,
        "researcher_count": 3,
        "research": [
            "docs/diagrams/reviews/experience-00-overview-fig1/research-identity.md",
            "docs/diagrams/reviews/canonical-a2a-execution/research-execution.md",
            "docs/diagrams/reviews/experience-review-workspace-merge-fig1/research-orchestration.md",
        ],
        "counts": {
            "planned_concepts": len(PLAN["concept_ids"]), "owned_report_concepts": len(concept_results),
            "consumer_pages": len(PLAN["document_paths"]), "original_diagram_identities": 19,
            "published_diagrams": 14, "merged_identities": 3, "replaced_by_shared_identities": 2,
            "removed_local_identities": 5, "obsolete_asset_files_removed": 48,
            "pitches": 14, "post_pitch_passes": 56, "schema_valid_manifests": 14,
            "inspected_exported_pngs": 70, "inspected_print_derivatives": 70,
            "final_arrows_traced": sum(v["arrow_count"] for v in artifacts.values()),
            "minimum_pass_one_growth": min(v["growth_ratio"] for v in artifacts.values()),
            "maximum_pass_one_growth": max(v["growth_ratio"] for v in artifacts.values()),
            "removed_unique_screenshot_references": len(old_screenshots - current_screenshots),
            "retained_qualified_screenshot_references": len(current_screenshots),
            "new_captures": 0, "focused_tests_passed": 19, "local_links_checked": links["checked_count"],
        },
        "concept_outcomes": concept_results,
        "finding_resolutions": [{"subject": f["subject"], "status": "resolved-owned-scope", "resolution": resolution}
                                for f, resolution in zip(audit["findings"], FINDING_RESOLUTIONS)],
        "diagram_outcomes": [
            {"name": d["name"], "planned_disposition": d["disposition"], "target": d.get("target"),
             "outcome": "published" if d["name"] in MODELS else "consolidated-and-local-assets-removed",
             "manifest": f"docs/diagrams/reviews/{d['name']}/iteration-manifest.json" if d["name"] in MODELS else None}
            for d in audit["diagrams"]
        ],
        "validation": {
            "artifact_checks": "artifact-validation.json", "links": "consumer-link-validation.json",
            "render_and_drift": "render-drift.log", "focused_tests": "focused-tests.log",
            "docs_build": "docs-build.log", "exporter": "exporter-provenance.json",
            "renderer": "official draw.io Desktop 31.4.5, PNG scale 2, border 16",
            "publication_identity": "All canonical XML bytes and re-rendered PNG pixels match the inspected fourth pass.",
            "temporary_exporter_removed": True,
            "build_warnings": "Existing third-party use-client directives, unloaded PromQL highlighter and chunk-size warnings; build exits zero.",
        },
        "deviations_and_boundaries": [
            "Requested skills were invoked but unregistered; their on-disk SKILL.md, checklists, schema and validators were read and followed.",
            "Audit screenshot recapture suggestions were resolved using the user's permitted truthful-prose alternative. No captures fabricated; legacy screenshot files are not changed.",
            "Historical audit rationale and line references remain as the audit snapshot; this execution ledger records current dispositions and corrections, including nonterminal Assistant Idle.",
            "Shared/pilot assets, global inventory/audit/reconciliation, other plans, skills/pipeline, runtime/auth/product code and git history were not modified by this execution.",
            "No approval of shared owners' publication workflows is claimed. Only declared stable shared paths are referenced.",
        ],
        "blockers": [],
        "changed_paths": sorted(set(paths + [(HERE / "execution-results.json").relative_to(ROOT).as_posix()])),
    }
    for path in output["changed_paths"]:
        assert path in PLAN["document_paths"] or path in {REPORT_PATH, PLAN_PATH} or any(
            path == owned or (owned.endswith("/") and path.startswith(owned))
            for owned in PLAN["exclusive_asset_paths"]), f"Unowned result path: {path}"
    V["save"](HERE / "execution-results.json", output)
    print(json.dumps(output["counts"], indent=2))
    print(f"Saved {len(output['changed_paths'])} changed-path entries and {len(concept_results)} concept outcomes.")


if __name__ == "__main__":
    main()
