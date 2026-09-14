"""Retire only the two audited, now-reconciled duplicate public families."""
import hashlib
import json
from pathlib import Path
import shutil

here=Path(__file__).resolve().parent
root=here.parents[3]
diagrams=root/"docs/diagrams"
audit=json.loads((root/"docs-diagram-audit.json").read_text(encoding="utf-8-sig"))
targets={"canonical-event-replay-tail":"canonical-durable-event-stream",
         "guide-review-fig1":"canonical-default-workflow"}
pages=[root/"README.md"]+[p for p in (root/"docs").rglob("*.md")
    if not any(s in ("reviews","node_modules",".vitepress") for s in p.relative_to(root/"docs").parts)]
prepared=[]
for name,target in targets.items():
    entry=next(e for e in audit["diagrams"] if e["name"]==name)
    if entry["disposition"]!="merge" or entry["target"]!=target:
        raise ValueError(name+": audited target changed")
    for suffix in (".png",".hash.txt"):
        if not (diagrams/(target+suffix)).exists():raise ValueError(target+": target incomplete")
    if not (diagrams/"src"/(target+".drawio")).exists():raise ValueError(target+": editable target missing")
    for page in pages:
        text=page.read_text(encoding="utf-8")
        if any(name+suffix in text for suffix in (".png",".drawio",".json",".hash.txt")):
            raise ValueError(str(page)+": unresolved retired asset reference")
    for file in [diagrams/"src"/(name+".json"),diagrams/"src"/(name+".drawio"),
                 diagrams/(name+".png"),diagrams/(name+".hash.txt"),diagrams/"drawio/generated"/(name+".drawio")]:
        if file.exists():prepared.append((name,target,file))
records=[]
for name,target,file in prepared:
    backup=here/"originals"/("retired-"+file.relative_to(diagrams).as_posix().replace("/","--"))
    if not backup.exists():shutil.copyfile(file,backup)
    records.append({"name":name,"target":target,"removed":str(file.relative_to(root)),
                    "sha256":hashlib.sha256(file.read_bytes()).hexdigest(),"archive":str(backup.relative_to(root))})
    file.unlink()
(here/"retirements.json").write_text(json.dumps(records,indent=2)+"\n",encoding="utf-8")
plan=json.loads((here/"repair-plan.json").read_text(encoding="utf-8"))
for entry in plan["entries"]:
    if entry["name"] in targets:
        entry.update(status="retired-merged",replacement=targets[entry["name"]],uncertainty=[])
(here/"repair-plan.json").write_text(json.dumps(plan,indent=2)+"\n",encoding="utf-8")
print(f"Retired {len(targets)} audited duplicate families; removed {len(records)} public/generated files; preserved archives.")
