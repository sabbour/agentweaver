"""Preserve a per-concept residual list without changing global audit evidence."""
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
REPORTS = ROOT / ".github/skills/docs-diagram-audit/reports"
plan = json.loads((REPORTS / "plan-shared.json").read_text())
reconciliation = json.loads((REPORTS / "reconciliation.json").read_text())
concepts = {}
for area in ("shared", "deep-dive-core", "deep-dive-orchestration", "experience", "reference"):
    report = json.loads((REPORTS / (area + ".json")).read_text())
    for document in report["documents"]:
        for concept in document["concepts"]:
            if concept["id"] in plan["concept_ids"]:
                concepts[concept["id"]] = {
                    "id": concept["id"],
                    "document": document["path"],
                    "disposition": concept["disposition"],
                    "representation": concept["representation"],
                    "target": concept.get("target"),
                    "canonical_diagram": concept.get("canonical_diagram"),
                    "status": "retained-text-pending-independent-completion",
                }
                override = reconciliation["concept_overrides"].get(concept["id"], {})
                for key in ("disposition", "target", "canonical_diagram"):
                    if key in override:
                        concepts[concept["id"]][key] = override[key]
assert set(concepts) == set(plan["concept_ids"]), set(plan["concept_ids"]) - set(concepts)
corrected = {
    "shared-harness-verdict-contract", "shared-harness-feedback-loop",
    "shared-contributor-worktree-isolation", "shared-catalog-workflow-mapping",
    "shared-promotion-partition", "shared-promotion-parent-outcome",
    "shared-auth-historical-middleware", "shared-auth-rollout-and-mcp-status",
    "shared-two-app-sandbox-contract-conflict", "shared-two-app-repository-selection",
    "shared-mcp-endpoint-classification", "shared-event-cursor-contract",
    "shared-workflow-binder-catalog-boundary", "shared-library-index",
    "shared-library-code-review-history", "shared-workflow-selection-events",
    "shared-diagram-pipeline",
}
diagram_pending = {
    "shared-auth-current-selector", "shared-durable-event-architecture",
    "shared-durable-event-sequence", "shared-email-coordinator",
    "shared-workflow-binder-default", "shared-workflow-selection-process",
    "shared-library-bugfix", "shared-library-content", "shared-library-discovery",
    "shared-library-evaluation", "shared-library-incident", "shared-library-infra",
    "shared-library-software",
}
for identifier in corrected:
    concepts[identifier]["status"] = "written-contract-corrected"
for identifier in diagram_pending:
    concepts[identifier]["status"] = "written-contract-updated-diagram-publication-gated"
output = {
    "owner": "shared",
    "status": "blocked",
    "note": "Statuses describe this execution, not a rewrite of completed audit conclusions. No extra diagrams were invented.",
    "concepts": [concepts[identifier] for identifier in plan["concept_ids"]],
}
(HERE / "shared-concept-status.json").write_text(json.dumps(output, indent=2) + "\n")
print(f"Recorded {len(concepts)} plan-owned concepts; {len(corrected)} written contracts corrected; "
      f"{len(diagram_pending)} written integrations remain diagram-gated.")
