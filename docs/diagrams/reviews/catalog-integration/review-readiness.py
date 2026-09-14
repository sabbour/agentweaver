"""Report real retained evidence and unmet gates; never synthesize completed passes."""
import json
from pathlib import Path
import sys

here=Path(__file__).resolve().parent
root=here.parents[3]
sys.path.insert(0,str(root/".github/skills/docs-diagram-iterate/scripts"))
from validate_iteration_manifest import validate_manifest
from check_xml_growth import assess

plan=json.loads((here/"repair-plan.json").read_text(encoding="utf-8"))
valid={}
for file in (root/"docs/diagrams/reviews").rglob("iteration-manifest.json"):
    manifest=json.loads(file.read_text(encoding="utf-8-sig"))
    if not validate_manifest(manifest):
        valid.setdefault(manifest["diagram"],[]).append(str(file.relative_to(root)))
retired={"canonical-event-replay-tail","guide-review-fig1"}
report={"eligible":[],"blocked":[],"protected":["canonical-coordinator-architecture"],"retire":sorted(retired)}
for entry in plan["entries"]:
    name=entry["name"]
    if name in retired or name in report["protected"]:
        continue
    if name in valid and not entry.get("style_errors"):
        report["eligible"].append({"name":name,"prior_evidence":valid[name],"already_published":entry["status"]=="published-validated"})
        continue
    directory=root/"docs/diagrams/reviews"/name
    pitch=directory/(name+"-pitch.drawio")
    pass1=directory/(name+"-pass-01.drawio")
    growth=assess(pitch.read_text(encoding="utf-8"),pass1.read_text(encoding="utf-8")) if pitch.exists() and pass1.exists() else None
    report["blocked"].append({"name":name,"growth":growth,
        "residual":"Complete a genuine >=9x visible-semantic pass 1 and three correction-only inspected passes without padding or violating approved compact Fluent styling.",
        "preserved_research":"Existing research, content model, pitch and initial pass remain intact.",
        "single_agent_exception":"User prohibits subagents; no research agents were launched and no prior evidence was relabeled as completed."})
(here/"publication-readiness.json").write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
print(f"{len(report['eligible'])} eligible; {len(report['blocked'])} blocked by actual unmet evidence gates; {len(retired)} audited retirements.")
