"""Sequential pinned export and family contact sheets; never publishes."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from PIL import Image, ImageDraw, ImageFont

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[3]
parser=argparse.ArgumentParser()
parser.add_argument("--drawio-cli",type=Path,required=True)
parser.add_argument("--name",action="append")
args=parser.parse_args()
plan=json.loads((HERE/"repair-plan.json").read_text(encoding="utf8"))
rendered=HERE/"rendered";rendered.mkdir(exist_ok=True)
owners={}
for path in (ROOT/".github/skills/docs-diagram-audit/reports").glob("plan-*.json"):
    item=json.loads(path.read_text(encoding="utf8"))
    for name in item["diagram_names"]+item.get("proposed_diagram_names",[]):owners[name]=item["area"]
digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
records=[]
for item in plan["entries"]:
    if args.name and item["name"] not in args.name:continue
    if not item.get("candidate") or item.get("style_errors"):continue
    source=ROOT/item["candidate"];target=rendered/(item["name"]+".png")
    stamp=rendered/(item["name"]+".json")
    previous=json.loads(stamp.read_text()) if stamp.exists() else {}
    if not target.exists() or previous.get("source_sha256")!=digest(source):
        subprocess.run([str(args.drawio_cli),"--export","--format","png","--border","16","--scale","2",
                        "--output",str(target),str(source)],check=True,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    with Image.open(target) as image:
        size=list(image.size);image.verify()
    record={"name":item["name"],"family":owners.get(item["name"],"other"),"source":str(source),"png":str(target),
            "source_sha256":digest(source),"png_sha256":digest(target),"dimensions":size,"renderer":"31.4.5",
            "inspection":"pending"}
    stamp.write_text(json.dumps(record,indent=2)+"\n")
    records.append(record)
    print("Exported "+item["name"],flush=True)
font=ImageFont.truetype("segoeui.ttf",18)
for family in sorted({r["family"] for r in records}):
    images=[r for r in records if r["family"]==family]
    for offset in range(0,len(images),9):
        batch=images[offset:offset+9]
        sheet=Image.new("RGB",(1440,650*((len(batch)+2)//3)),"#efeae7");draw=ImageDraw.Draw(sheet)
        for i,record in enumerate(batch):
            x=(i%3)*480;y=(i//3)*650
            draw.text((x+12,y+12),record["name"],font=font,fill="#272320")
            with Image.open(record["png"]) as image:
                image=image.convert("RGB");image.thumbnail((460,600))
                sheet.paste(image,(x+(480-image.width)//2,y+44))
        sheet.save(HERE/(f"contact-{family}-{offset//9+1}.png"))
(HERE/"candidate-exports.json").write_text(json.dumps(records,indent=2)+"\n")
print(f"{len(records)} staged exports; no public writes.")
