"""Reconcile completion state only after canonical assets and consumers agree."""
from collections import Counter
import json
from pathlib import Path
import shutil
import sys

from jsonschema import Draft202012Validator

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
sys.path.insert(0, str(ROOT / ".github/skills/docs-diagram-audit/scripts"))
from diagram_audit import discover, validate, summary
sys.path.insert(0, str(ROOT / ".github/skills/docs-diagram-iterate/scripts"))
from validate_iteration_manifest import validate_manifest


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write(path, data):
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


original = ROOT / "docs-diagram-audit.json"
archive = HERE / "originals/audit-before-completion.json"
if not archive.exists():
    shutil.copyfile(original, archive)
report = discover(ROOT, original)
assert not any(report["integrity"].values()), report["integrity"]
readiness = read(HERE / "publication-readiness.json")
assert not readiness["blocked"]
assert len(read(HERE / "promotions.json")) == 120
assert read(HERE / "pilot-compatibility/verification.json")["status"] == "passed"
manifests = {}
manifest_schema = read(ROOT / ".github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json")
for path in (ROOT / "docs/diagrams/reviews").rglob("iteration-manifest.json"):
    manifest = read(path)
    if not validate_manifest(manifest) and not list(Draft202012Validator(manifest_schema).iter_errors(manifest)):
        manifests.setdefault(manifest["diagram"], []).append(path.relative_to(ROOT).as_posix())

new = {
    "canonical-pod-process-boundaries": {
        "owner": "deep-dive-execution",
        "title": "Kata VM, AgentHost, executor and child mount boundaries",
        "rationale": "Publish the process-boundary diagram explicitly proposed by the execution audit, using the retained grounded complete model.",
        "evidence": [
            "docs/deep-dive/sandbox-pod-execution.md:145",
            "k8s/base/sandbox-template-agenthost.yaml:60",
            "apps/Agentweaver.AgentHost/Program.cs:92",
            "packages/Agentweaver.SandboxExec/KataBwrapExecutor.cs:236",
            "tests/Agentweaver.Tests/KataBwrapExecutorTests.cs:29",
        ],
    },
    "canonical-provider-admission": {
        "owner": "shared",
        "title": "Provider admission and execution snapshot",
        "rationale": "Publish the provider-admission canonical explicitly proposed by the shared plan. Signed admission freezes execution choice; live capability checks stay separate.",
        "evidence": [
            "apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:205-358",
            "apps/Agentweaver.Api/Auth/RunModelProviderSnapshotStore.cs:45-155",
            "apps/Agentweaver.Api/Auth/RunModelInvocationGuard.cs:11-48",
            "docs/diagrams/reviews/canonical-provider-admission/evidence.md:1",
        ],
    },
}
for diagram in report["diagrams"]:
    name = diagram["name"]
    if name in new:
        diagram.update(disposition="redesign", owner_area=new[name]["owner"],
                       legacy_assessment=new[name]["rationale"])
    if diagram["tombstone"]:
        diagram.update(source_paths=[], image_path=None, hash_path=None, references=[])
    else:
        assert name in manifests, f"{name}: no completed iteration evidence"
        assert len(diagram["source_paths"]) == 1 and diagram["source_paths"][0].endswith(".drawio"), name
    diagram["status"] = "done"

for concept in report["concepts"]:
    name = concept["id"]
    if name in new:
        item = new[name]
        concept.update(disposition="redesign", owner_area=item["owner"], title=item["title"],
                       rationale=item["rationale"], evidence=item["evidence"], canonical_diagram=name)
    if concept["disposition"] in ("retain", "redesign"):
        assert concept["canonical_diagram"] in manifests, name
        concept.update(pitch_skill_completed=True, iterate_skill_completed=True)
    concept["status"] = "done"

schema = read(ROOT / ".github/skills/docs-diagram-audit/references/audit-report.schema.json")
errors = validate(report, final=True) + [e.message for e in Draft202012Validator(schema).iter_errors(report)]
assert not errors, errors
write(original, report)
(ROOT / "docs-diagram-audit.md").write_text(summary(report), encoding="utf-8")

inventory = {"version": 1, "diagrams": []}
for diagram in report["diagrams"]:
    if diagram["tombstone"]:
        continue
    inventory["diagrams"].append({
        "name": diagram["name"], "source_path": diagram["source_paths"][0], "source_kind": "drawio",
        "output_path": diagram["image_path"],
        "references": sorted({r["path"] for r in diagram["references"]}),
        "referenced_areas": sorted({r["path"].split("/")[1] if r["path"].startswith("docs/") else "shared" for r in diagram["references"]}),
        "owner_area": diagram["owner_area"], "disposition": diagram["disposition"],
        "target": diagram["target"], "status": "done", "rationale": diagram["legacy_assessment"],
    })
Draft202012Validator(read(ROOT / ".github/skills/docs-diagram-pitch/references/diagram-inventory.schema.json")).validate(inventory)
write(ROOT / "docs-diagram-inventory.json", inventory)
inventory_snapshot = HERE / "current-inventory.json"
old_inventory = HERE / "originals/current-inventory-before-completion.json"
if inventory_snapshot.exists() and not old_inventory.exists():
    shutil.copyfile(inventory_snapshot, old_inventory)
write(inventory_snapshot, inventory)
for plan_path in (ROOT / ".github/skills/docs-diagram-audit/reports").glob("plan-*.json"):
    plan = read(plan_path)
    plan["status"] = "done"
    plan["completion_evidence"] = "docs/diagrams/reviews/catalog-integration/integration-result.json"
    write(plan_path, plan)
write(HERE / "completion-reconciliation.json", {
    "status": "done", "diagram_records": len(report["diagrams"]), "public_canonicals": len(inventory["diagrams"]),
    "dispositions": dict(Counter(d["disposition"] for d in report["diagrams"])),
    "original_audited_dispositions": dict(Counter(d["disposition"] for d in read(archive)["diagrams"])),
    "added_proposals": sorted(new), "integrity": report["integrity"],
    "iteration_evidence_by_canonical": {d["name"]: manifests[d["name"]] for d in report["diagrams"] if not d["tombstone"]},
})
print(f"Reconciled {len(report['diagrams'])} audit records and {len(inventory['diagrams'])} public canonicals.")
