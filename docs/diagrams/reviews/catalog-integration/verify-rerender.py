"""Verify canonical re-export keeps the exact inspected pixels and source."""
import hashlib
import json
from pathlib import Path
import sys
from PIL import Image

here=Path(__file__).resolve().parent
root=here.parents[3]
ledger=json.loads((here/"promotions.json").read_text(encoding="utf-8"))
records={}
for item in ledger:
    name=item["name"]
    source=root/"docs/diagrams/src"/(name+".drawio")
    png=root/"docs/diagrams"/(name+".png")
    with Image.open(png) as image:
        pixels=hashlib.sha256(image.convert("RGBA").tobytes()).hexdigest()
        dimensions=list(image.size)
    records[name]={"source_sha256":hashlib.sha256(source.read_bytes()).hexdigest(),
        "png_sha256":hashlib.sha256(png.read_bytes()).hexdigest(),"pixels_sha256":pixels,"dimensions":dimensions}
if sys.argv[1]=="before":
    (here/"render-before.json").write_text(json.dumps(records,indent=2)+"\n",encoding="utf-8")
else:
    before=json.loads((here/"render-before.json").read_text(encoding="utf-8"))
    failures=[name for name,item in records.items() if any(item[key]!=before[name][key]
              for key in ("source_sha256","pixels_sha256","dimensions"))]
    result={"checked":len(records),"failures":failures,"artifacts":records,
            "metadata_only_png_changes":sum(item["png_sha256"]!=before[name]["png_sha256"] for name,item in records.items())}
    (here/"render-verification.json").write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    if failures:raise ValueError("Re-export altered inspected source or pixels: "+", ".join(failures))
    for item in ledger:
        item.setdefault("inspected_export_sha256",item["png_sha256"])
        item.update(png_sha256=records[item["name"]]["png_sha256"],pixels_sha256=records[item["name"]]["pixels_sha256"])
    (here/"promotions.json").write_text(json.dumps(ledger,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({k:v for k,v in result.items() if k!="artifacts"}))
