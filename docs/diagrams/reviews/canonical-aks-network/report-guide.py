"""Record every owned plan disposition without mutating global audit/inventory."""
import hashlib
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
PLAN_PATH = ROOT / ".github/skills/docs-diagram-audit/reports/plan-guide.json"
PLAN = json.loads(PLAN_PATH.read_text())
AUDIT = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/guide.json").read_text())
NOTES = {
    "architecture-aks.md": "Three exclusive diagrams promoted. Corrected binding/readiness/retention, separate identity and broker authority, routes/additive policies, EF/shared/local storage, Worker floor/HPA and provider hierarchy. Shared component image withheld with truthful prose.",
    "assistant.md": "Five active sessions and resumable Idle; personal model/provider hierarchy, not project Copilot prerequisite.",
    "authentication.md": "Canonical provider decision table, fail-closed project binding, three Copilot callback completion scopes and separate MCP handoff.",
    "blueprints.md": "Retained blueprint reference; only default review_policy accepted.",
    "board.md": "Removed placeholder. Retained canonical board reference; four main/two attention buckets, explicit Problems recovery, safe-tool approval qualification, pickup/direct alternatives and decomposition preview/confirm.",
    "configuration.md": "Corrected callback table and three completion scopes, purpose-bound durable credential authority, personal/project distinctions; retained operational reference.",
    "deployment-aks.md": "Command-choice table, correct azure:provision-infra, dirty rejection and explicit --allow-dirty; retained origin/secret setup and canonical configuration links.",
    "diagram-authoring.md": "Distinguished transitional JSON/generated XML from promoted canonical editable draw.io; selective commands, ambiguity checks, pinned exports and evidence.",
    "example-scenarios.md": "Exclusive MCP lifecycle promoted. Replaced stale default-workflow image with current collective prose; corrected manual/direct/pickup, team confirmation, decomposition, review and Scribe semantics; retained shared board reuse.",
    "fleet-cutover-validation.md": "Historical checklist explicitly qualified by current credential/identity contract and coverage; no deployment execution.",
    "getting-started.md": "Retained local setup and dev/release/main separation; removed fixed four-CI-check claim in favor of canonical policy links.",
    "index.md": "Removed placeholder; clarified personal sessions, launch modes, collective gates and authoritative trust records while retaining coordinator reuse/domain prose.",
    "mcp-cli.md": "Retained client onboarding and retry guidance; exact confirmed manual/direct/pickup lifecycle reuse, conditional project binding and boolean review parity.",
    "operations.md": "Retained diagnostic/runbook prose; compatibility-gated rollback and Worker two-replica floor/HPA 2-3, CPU70/memory80.",
    "projects.md": "Removed three placeholders, retained clear gallery/creation instructions; new-or-empty directory requirement and provider/readiness activation prose.",
    "review.md": "Blocked conditional shared merge; obsolete embed withheld. Corrected collective gates/steering and retained approval projection, file review and merge semantics; removed diff placeholder.",
    "runs.md": "Removed three placeholders. Child Agent->Assemble-ready, collective gates, deterministic PreviewStep, launch confirmation qualification; retained diagnostics and shared worktree/coordinator reuse.",
    "teams.md": "Removed placeholder; software-delivery/bug-fix scope, explicit casting/launch confirmation, coordinator steering and memory governance retained.",
    "validation.md": "Retained unchanged: existing validation profile/cache reference remains the appropriate prose representation.",
    "workflows.md": "Removed placeholder; catalog/local/blueprint selection distinctions and workflow runtime binding; retained canonical authoring/invocation reuse and trigger reference.",
}
BLOCKED = {
    "guide-aks-components": "Shared canonical-aks-components requires its owner to remove obsolete privileged AgentHost/MCP CSI claims and correct Worker/storage/preview. Owned prose is corrected; obsolete embed withheld.",
    "guide-collective-review": "Conditional guide-review-fig1 -> canonical-default-workflow merge cannot complete before shared target promotion. Historical local assets retained exactly as required.",
    "guide-scenario-default-pipeline": "Shared canonical-default-workflow reuse deferred; truthful collective prose and review-guide link replace its obsolete embed pending owner promotion.",
}
SCREENSHOTS = {
    "guide-board-screenshot", "guide-overview-screenshot", "guide-project-gallery-and-creation",
    "guide-review-diff-screenshot", "guide-run-ui-captures", "guide-team-roster-screenshot",
    "guide-workflows-screenshot",
}

entries = []
documents = []
for document in AUDIT["documents"]:
    relative = document["path"]
    assert relative in PLAN["document_paths"]
    note = NOTES[Path(relative).name]
    documents.append({"path": relative,
                      "action": "retained" if relative.endswith("/validation.md") else "updated",
                      "result": note,
                      "sha256": hashlib.sha256((ROOT / relative).read_bytes()).hexdigest()})
    for concept in document["concepts"]:
        cid = concept["id"]
        if cid in BLOCKED:
            status, result = "blocked-shared-owner", BLOCKED[cid]
        elif cid in SCREENSHOTS:
            status = "applied-user-authorized-alternative"
            result = "Removed nonexistent visual coverage (1x1 embeds/assets); retained clearer truthful prose. No UI capture or fabricated replacement."
        elif concept["disposition"] == "redesign":
            status, result = "promoted", "Exclusive canonical editable A5 source and PNG; four inspected passes, >=9x pass-1 growth, full arrow trace and manifest."
        elif str(concept.get("target") or "").startswith("canonical-"):
            status, result = "owned-consumer-applied", "Shared asset retained/reused without modification; no four-pass or current-correctness attestation for foreign-owned source. " + note
        else:
            status, result = "applied", note
        entries.append({"concept_id": cid, "document": relative,
                        "planned_disposition": concept["disposition"],
                        "target": concept.get("target"), "canonical_diagram": concept.get("canonical_diagram"),
                        "status": status, "result": result})
assert {e["concept_id"] for e in entries} == set(PLAN["concept_ids"])
assert {d["path"] for d in documents} == set(PLAN["document_paths"])

files = []
for relative in [*PLAN["document_paths"], *PLAN["exclusive_asset_paths"]]:
    target = ROOT / relative
    if relative.endswith("/"):
        if target.exists():
            files.extend(str(p.relative_to(ROOT)).replace("\\", "/")
                         for p in target.rglob("*")
                         if p.is_file() and "renderer-temp" not in p.parts)
    else:
        files.append(relative)
report = {
    "area": "guide", "report_entry_owner": "guide", "model": "gpt-6-astra",
    "status": "blocked", "plan": str(PLAN_PATH.relative_to(ROOT)).replace("\\", "/"),
    "plan_sha256": hashlib.sha256(PLAN_PATH.read_bytes()).hexdigest(),
    "scope": "Only guide exclusive assets/pages/review evidence. No commits, product or deployment implementation changes, pilot/shared asset edits, or global inventory/audit/reconciliation edits.",
    "research_threads": ["guide-runtime-evidence", "guide-network-evidence", "guide-workflow-evidence"],
    "promoted": ["canonical-aks-network", "guide-architecture-aks-fig1",
                 "guide-architecture-aks-fig5", "guide-example-scenarios-fig3"],
    "blocked": BLOCKED,
    "historical_pending_merge": "guide-review-fig1",
    "global_handoff": "Shared owners must promote dependencies and reconcile protected inventory source paths. This execution deliberately does not edit those records.",
    "documents": documents, "dispositions": entries, "owned_paths": sorted(set(files)),
    "evidence": {
        "artifact_checks": "artifact-validation.json",
        "placeholder_retirement": "placeholder-retirement.json",
        "diagram_tests": "diagram-tests.log",
        "iteration_tests": "iteration-tests.log",
        "drift": "drift-validation.log",
        "built_links": "link-validation.json",
        "docs_build": "docs-build.log",
        "research": ["research-runtime.md", "research-network.md", "research-workflow.md"],
        "per_diagram": "../<diagram>/iteration-manifest.json and pitch/pass-01..04 sources, PNGs, print views and Markdown reviews",
    },
}
(HERE / "guide-execution.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(f"Recorded {len(entries)} dispositions and {len(documents)} owned pages; overall status blocked on shared ownership.")
