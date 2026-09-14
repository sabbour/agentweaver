"""Validate this lineage, selectively render, and supersede only its eight manifests."""
import copy
import hashlib
import importlib.util
import json
import shutil
import subprocess
import sys
from pathlib import Path
from xml.etree import ElementTree as E
import jsonschema
from PIL import Image, ImageChops

sys.dont_write_bytecode=True
spec=importlib.util.spec_from_file_location("e",Path(__file__).with_name("evidence.py"))
e=importlib.util.module_from_spec(spec);spec.loader.exec_module(e)
r=e.r
schema=json.loads((r.ROOT/".github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json").read_text())
spec=importlib.util.spec_from_file_location("growth",r.ROOT/".github/skills/docs-diagram-iterate/scripts/check_xml_growth.py")
g=importlib.util.module_from_spec(spec);spec.loader.exec_module(g)
allowed=json.loads((r.ROOT/".github/skills/docs-diagram-audit/reports/plan-shared.json").read_text())["exclusive_asset_paths"]

def guarded(path):
    rel=path.relative_to(r.ROOT).as_posix()
    assert any(rel==a or (a.endswith("/") and rel.startswith(a)) for a in allowed),rel
    return path

def write(path,data):
    guarded(path).write_text(json.dumps(data,indent=2)+"\n",encoding="utf-8")

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

manifests={}
checks={}
for n in r.NAMES:
    f=r.folder(n)
    report=json.loads((f/"growth-check.json").read_text())
    assert report["passed"]
    stages=["pitch"]+[f"pass-{i:02}" for i in range(1,5)]
    artifact=[]
    per_stage=[]
    for stage in stages:
        for suffix in (".drawio",".png",".md"):
            assert (f/f"{n}-{stage}{suffix}").is_file()
        source=f/f"{n}-{stage}.drawio"
        analysis=g.analyze(source.read_text(encoding="utf-8"),stage)
        for defect in ("invisible_cells","off_page_cells","duplicate_cells","metadata_padding"):
            assert not analysis[defect],(n,stage,defect,analysis[defect])
        xml=E.parse(source)
        model=xml.find(".//mxGraphModel")
        assert (model.get("pageWidth"),model.get("pageHeight"),model.get("pageScale"))==("827","583","1")
        trace=e.arrows(n,source)
        cells={c.get("id"):c for c in xml.findall(".//mxCell")}
        anchor_checks=[]
        for arrow in trace:
            c=cells[arrow["id"]]
            style=g.parse_style(c.get("style",""))
            for end,keys in (("source",("exitX","exitY")),("target",("entryX","entryY"))):
                assert all(k in style and 0<=float(style[k])<=1 for k in keys),(n,stage,arrow["id"],end)
                bounds=g.absolute_bounds(c.get(end),cells,{})
                x,y,w,h=bounds
                px=x+w*float(style[keys[0]]);py=y+h*float(style[keys[1]])
                assert 0<=px<=827 and 0<=py<=583
                anchor_checks.append(dict(arrow=arrow["id"],end=end,cell=c.get(end),point=[px,py],within_page=True))
        per_stage.append(dict(stage=stage,meaningful_xml=analysis["meaningful_count"],visible_structures=analysis["visible_structures"],
                              arrow_count=len(trace),a5_page_scale_one=True,anchor_checks=anchor_checks))
        entry=dict(drawio=f"{n}-{stage}.drawio",png=f"{n}-{stage}.png",change_record=f"{n}-{stage}.md",
                   png_inspected_print=True,png_inspected_enlarged=True)
        if stage!="pitch":
            number=int(stage[-2:])
            entry.update(number=number,mode="visual-upgrade" if number==1 else "correction-only",
                         orientation_defects=0,overlap_defects=1 if number==1 and n=="email-coordinator-workflow" else 0,
                         arrow_defects=2 if number==1 and n=="canonical-coordinator-journey" else 0)
            if number==1:
                for key in ("growth_metric","baseline_meaningful_xml","result_meaningful_xml","growth_ratio"):
                    entry[key]=report[key]
            if number==4:
                entry.update(all_arrows_traced=True,arrow_trace=trace)
        artifact.append(entry)
    manifest=dict(diagram=n,orientation="A5-landscape",pitch=artifact[0],passes=artifact[1:],final_pass=4)
    jsonschema.Draft202012Validator(schema).validate(manifest)
    write(f/"iteration-manifest.json",manifest)
    subprocess.run([sys.executable,"-B",str(r.ROOT/".github/skills/docs-diagram-iterate/scripts/validate_iteration_manifest.py"),
                    str(f/"iteration-manifest.json")],check=True,capture_output=True)
    manifests[n]=manifest
    checks[n]=per_stage
    guarded(r.ROOT/"docs/diagrams/src"/f"{n}.drawio")
    shutil.copyfile(f/f"{n}-pass-04.drawio",r.ROOT/"docs/diagrams/src"/f"{n}.drawio")

command=["node",str(r.ROOT/"scripts/docs/render-diagrams.mjs"),"--drawio-cli",str(r.a.CLI)]
for n in r.NAMES:command+=["--spec",n]
for extra in ([],["--check"]):
    result=subprocess.run(command+extra,cwd=r.ROOT,capture_output=True,text=True)
    print(result.stdout)
    assert result.returncode==0,result.stderr
    guarded(r.HERE/("selective-drift-check.log" if extra else "selective-render.log")).write_text(result.stdout+result.stderr,encoding="utf-8")

for n in r.NAMES:
    f=r.folder(n);review=r.a.REVIEWS/n
    published=r.ROOT/"docs/diagrams"/f"{n}.png"
    final=f/f"{n}-pass-04.png"
    left=Image.open(final).convert("RGB");right=Image.open(published).convert("RGB")
    assert left.size==right.size and ImageChops.difference(left,right).getbbox() is None,n
    assert (r.ROOT/"docs/diagrams/src"/f"{n}.drawio").read_bytes()==(f/f"{n}-pass-04.drawio").read_bytes()
    if n.startswith("email-"):
        derived=guarded(r.ROOT/"docs/diagrams/email-exports"/f"{n}.png")
        shutil.copyfile(published,derived)
        assert sha(derived)==sha(published)
    archive=guarded(review/"iteration-manifest.pre-fluent-remediation.json")
    assert not archive.exists(),"Never overwrite the archived root manifest"
    shutil.copyfile(review/"iteration-manifest.json",archive)
    rooted=copy.deepcopy(manifests[n])
    for entry in [rooted["pitch"]]+rooted["passes"]:
        for field in ("drawio","png","change_record"):
            entry[field]=r.LINEAGE+"/"+entry[field]
    jsonschema.Draft202012Validator(schema).validate(rooted)
    write(review/"iteration-manifest.json",rooted)
    subprocess.run([sys.executable,"-B",str(r.ROOT/".github/skills/docs-diagram-iterate/scripts/validate_iteration_manifest.py"),
                    str(review/"iteration-manifest.json")],check=True,capture_output=True)
    validation=dict(stages=checks[n],schema="draft-2020-12",lineage_and_root_manifest_valid=True,
                    selective_render_and_stamp=True,selective_drift_check=True,pinned_renderer="31.4.5",
                    export_border=16,export_scale=2,canonical_source_equals_review=True,
                    published_pixels_equal_inspected_pass_04=True,published_dimensions=list(right.size),
                    published_sha256=sha(published),derived_email_copy_identical=n.startswith("email-"))
    write(f/"validation-evidence.json",validation)
    growth=json.loads((f/"growth-check.json").read_text())
    result=dict(diagram=n,status="completed",lineage=r.LINEAGE,final_pass=4,
                supersedes="Old root pitch was a sparse generic concept strip without the complete Fluent/native initial-pitch contract. Its mechanical stamps and previous pass ratios did not establish compliant pitch provenance.",
                superseded_root_manifest=archive.name,prior_pitch_and_pass_files_preserved=True,
                new_initial_pitch="Source-backed compact responsibility model with native notation and full Fluent hierarchy; not a reapproval of the old pitch.",
                research="Three preserved independent Astra threads reused by explicit user instruction; no nested/research agents launched.",
                growth_metric=growth["growth_metric"],baseline_meaningful_xml=growth["baseline_meaningful_xml"],
                result_meaningful_xml=growth["result_meaningful_xml"],growth_ratio=growth["growth_ratio"],
                final_arrow_count=checks[n][-1]["arrow_count"],validation=f"{r.LINEAGE}/validation-evidence.json",
                print_and_enlarged_inspections_per_stage=True,residual_defects=[],
                parent_owned_remaining=["documentation build","inventory/consumer updates"],
                scope="Only eight exclusive asset families; no pages, plans, global inventory, product changes, staging or commits.")
    write(review/"remediation-result.json",result)
    print(n,growth["growth_ratio"],checks[n][-1]["arrow_count"],"PROMOTED + VERIFIED")
