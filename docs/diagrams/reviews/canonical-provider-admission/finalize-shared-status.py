"""Reconcile only this shard's owned execution/status records from verified artifacts."""
from collections import Counter
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
PLAN = ROOT / ".github/skills/docs-diagram-audit/reports/plan-shared.json"
plan = json.loads(PLAN.read_text())
if plan["execution"].get("visual_pause"):
    raise SystemExit("User visual pause is active. Do not finalize or promote before explicit calibration approval.")
validation = json.loads((HERE / "shared-validation.json").read_text())
assert validation["selective_drift"]["exit_code"] == 0
assert not validation["page_contract_errors"]
assert not validation["local_image_link_errors"]
assert validation["summary"]["canonical_files_migrated"] == 27
concept_path = HERE / "shared-concept-status.json"
concepts = json.loads(concept_path.read_text())
for concept in concepts["concepts"]:
    if concept["status"] == "written-contract-updated-diagram-publication-gated":
        target = concept["canonical_diagram"] or concept["target"]
        assert target in (ROOT / concept["document"]).read_text(encoding="utf-8"), concept["id"]
        assert (ROOT / f"docs/diagrams/src/{target}.drawio").exists(), target
        concept["completion_evidence"] = "docs/diagrams/reviews/canonical-provider-admission/shared-validation.json"
    if concept["id"] in ("shared-readme-system", "shared-root-excalidraw-retirement"):
        concept["status"] = "completed"
        concept["residual"] = None
        concept["completion_evidence"] = "docs/diagrams/reviews/canonical-provider-admission/shared-validation.json"
    elif concept["status"] in (
        "written-contract-corrected",
        "written-contract-updated-diagram-publication-gated",
        "retained-text-independently-completed",
    ):
        concept["status"] = "completed"
    elif "blocked" in concept["status"]:
        concept["status"] = "blocked-out-of-scope-handoff"
    if concept["status"] == "completed":
        concept["residual"] = None
concepts["status"] = "blocked-out-of-scope-handoffs"
concepts["summary"] = dict(Counter(c["status"] for c in concepts["concepts"]))
concepts["note"] = (
    "All 96 concepts independently addressed. Completed canonical lineages and owned-page integrations "
    "resolve former pitch/consumer gates. Remaining handoffs name only foreign consumer or generator-source paths."
)
concept_path.write_text(json.dumps(concepts, indent=2) + "\n")
execution = plan["execution"]
execution["canonical_files_migrated"] = [d["name"] for d in validation["diagrams"] if d["canonical_present"]]
execution["review_candidates_not_promoted"] = []
execution["completed_remediations"] = {
    "canonical_count": 27,
    "active_manifest_artifact_triples": validation["summary"]["artifact_triples"],
    "final_arrows": validation["summary"]["arrows_in_final_manifests"],
    "initial_pitch_and_iteration": "complete; root manifests select the inspected compliant lineages",
    "representative_sample": "docs/diagrams/canonical-workflow-authoring.png",
}
execution["concept_summary"] = concepts["summary"]
execution["readme_visual_coverage"] = validation["readme_visual_coverage"]
execution["completed_merges"] = validation["merged_families"]
execution["pending_merges"] = validation["consumer_dependencies"]
execution["retired_non_catalog_assets"] = validation["retired_non_catalog_assets"]
known = {t["name"] for t in execution["tombstones"]}
for item in validation["merged_families"]:
    if item["name"] not in known:
        execution["tombstones"].append({
            "name": item["name"], "disposition": "merge",
            "status": "removed-after-target-validation", "replacement": item["target"],
        })
execution["residual_work"] = [
    "canonical-event-replay-tail still has two consumers outside document_paths: docs/deep-dive/frontend.md and docs/deep-dive/orchestration.md. Their owners must switch to canonical-durable-event-stream and correct replay/polling prose; then this shard may retire the remaining family.",
    "Five foreign concept integrations remain: shared memory-schema ownership in docs/deep-dive/data-persistence.md; focused ER reuse in docs/deep-dive/memory-decisions.md; inbox state aliases/reuse in docs/experience/agent-communication.md; provider acceptance/snapshot/fence and canonical-provider-admission embeds in docs/deep-dive/api-core.md and docs/reference/api.md.",
    "Two corrected downloadable-agent concepts need out-of-scope source/mirror synchronization: .github/agents/agentweaver.agent.md, apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md and apps/Agentweaver.Web/wwwroot/agents/agentweaver.agent.md. Do not run scripts/gen-docs.mjs over those paths within this shard.",
]
execution["coordinator_inventory_handoff"] = "Global inventory/reconciliation was intentionally not written. Consume this plan's tombstones and completed canonical records."
execution["publication_ready"] = False
execution["owned_canonical_artifacts_complete"] = True
execution["status"] = "blocked-out-of-scope-handoffs"
plan["status"] = "blocked"
PLAN.write_text(json.dumps(plan, indent=2) + "\n")
print(json.dumps(concepts["summary"], indent=2))
print(f"Canonical artifacts: 27; completed merges: {len(validation['merged_families'])}; "
      f"pending merges: {len(validation['consumer_dependencies'])}")
