"""Bind an actual image inspection to immutable source and raster digests."""
import argparse
import hashlib
import json
from pathlib import Path

here=Path(__file__).resolve().parent
parser=argparse.ArgumentParser()
parser.add_argument("names",nargs="+")
parser.add_argument("--method",choices=["individual","contact-sheet"],required=True)
parser.add_argument("--notes",required=True)
args=parser.parse_args()
destination=here/"inspection-approvals.json"
approvals=json.loads(destination.read_text(encoding="utf-8")) if destination.exists() else {}
digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
prepared={}
for name in args.names:
    record=json.loads((here/"rendered"/(name+".json")).read_text(encoding="utf-8"))
    if digest(Path(record["source"]))!=record["source_sha256"] or digest(Path(record["png"]))!=record["png_sha256"]:
        raise ValueError(name+": inspected artifacts changed")
    prepared[name]={**record,"inspection":args.method,"notes":args.notes,
                    "reviewer":"GPT-6 Astra, sole authorized agent"}
approvals.update(prepared)
destination.write_text(json.dumps(approvals,indent=2)+"\n",encoding="utf-8")
print(f"Recorded {len(prepared)} exact-image inspections")
