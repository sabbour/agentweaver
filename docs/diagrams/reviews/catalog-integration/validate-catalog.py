"""Read-only catalog snapshot. Writes only this integration report directory."""
from collections import Counter
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET

from jsonschema import Draft202012Validator
from PIL import Image

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[3]
DIAGRAMS=ROOT/"docs/diagrams"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def relative(path):
    return path.relative_to(ROOT).as_posix()


def load_module(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    module=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


report={"status":"blocked","public_promotions_this_integration":0,
        "public_removals_this_integration":0,"public_restorations_this_integration":0}
if (HERE/"promotions.json").exists():
    report["public_promotions_this_integration"]=len({e["name"] for e in json.loads((HERE/"promotions.json").read_text(encoding="utf-8"))})
if (HERE/"retirements.json").exists():
    report["public_removals_this_integration"]=len(json.loads((HERE/"retirements.json").read_text(encoding="utf-8")))
sources={p.stem:p for p in (DIAGRAMS/"src").iterdir()
         if p.suffix in (".drawio",".json") and not p.name.endswith(".schema.json")}
pngs={p.stem:p for p in DIAGRAMS.glob("*.png")}
report["public_source_count"]=len(sources)
report["public_png_count"]=len(pngs)
report["missing_public_png"]=sorted(sources.keys()-pngs.keys())
report["png_without_source"]=sorted(pngs.keys()-sources.keys())
stamps={p.name.removesuffix(".hash.txt") for p in DIAGRAMS.glob("*.hash.txt")}
report["hash_without_source"]=sorted(stamps-sources.keys())
report["generated_without_source"]=sorted(p.stem for p in (DIAGRAMS/"drawio/generated").glob("*.drawio") if p.stem not in sources)
report["redundant_generated_drawio"]=sorted(p.stem for p in (DIAGRAMS/"drawio/generated").glob("*.drawio")
    if p.stem in sources and sources[p.stem].suffix==".drawio")
report["xml"]={"checked":0,"errors":[]}
report["public_a5"]={"checked":0,"landscape":0,"portrait":0,"errors":[]}
for path in sorted(DIAGRAMS.rglob("*.drawio")):
    report["xml"]["checked"]+=1
    try:
        tree=ET.parse(path)
        if not tree.findall("diagram/mxGraphModel/root"):
            raise ValueError("Not editable uncompressed XML")
    except (ET.ParseError,ValueError) as error:
        report["xml"]["errors"].append({"path":relative(path),"error":str(error)})
for name,path in sorted(sources.items()):
    if path.suffix != ".drawio":
        report["public_a5"]["errors"].append({"name":name,"error":"Source is not editable draw.io"})
        continue
    report["public_a5"]["checked"]+=1
    tree=ET.parse(path)
    graphs=tree.findall("diagram/mxGraphModel")
    if len(graphs)!=1 or len(tree.findall("diagram"))!=1:
        report["public_a5"]["errors"].append({"name":name,"error":"Expected one uncompressed page"})
        continue
    dimensions=(graphs[0].get("pageWidth"),graphs[0].get("pageHeight"))
    if dimensions==("794","559"):
        report["public_a5"]["landscape"]+=1
    elif dimensions==("559","794"):
        report["public_a5"]["portrait"]+=1
    else:
        report["public_a5"]["errors"].append({"name":name,"error":f"Non-A5 page: {dimensions}"})
report["images"]=[]
report["stamp_errors"]=[]
for name,path in sorted(pngs.items()):
    with Image.open(path) as image:
        size=list(image.size)
        image.verify()
    report["images"].append({"name":name,"dimensions":size,"sha256":digest(path)})
    stamp_path=DIAGRAMS/f"{name}.hash.txt"
    if not stamp_path.exists():
        report["stamp_errors"].append({"name":name,"error":"Missing stamp"})
        continue
    try:
        stamp=json.loads(stamp_path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as error:
        report["stamp_errors"].append({"name":name,"error":str(error)})
        continue
    source=sources.get(name)
    xml=source if source and source.suffix==".drawio" else DIAGRAMS/"drawio/generated"/f"{name}.drawio"
    if stamp.get("png",{}).get("sha256")!=digest(path):
        report["stamp_errors"].append({"name":name,"error":"PNG digest mismatch"})
    if not xml.exists() or stamp.get("drawio",{}).get("sha256")!=digest(xml):
        report["stamp_errors"].append({"name":name,"error":"XML digest mismatch"})
    if stamp.get("renderer",{}).get("rendererVersion")!="31.4.5":
        report["stamp_errors"].append({"name":name,"error":"Renderer is not pinned"})

pages=[ROOT/"README.md",ROOT/"CONTRIBUTING.md",ROOT/"RELEASING.md"]
pages += [p for p in (ROOT/"docs").rglob("*.md")
          if not any(part in ("reviews","node_modules",".vitepress") for part in p.relative_to(ROOT/"docs").parts)]
report["image_references"]=[]
report["broken_images"]=[]
for page in sorted(set(pages)):
    raw=re.sub(r"```[\s\S]*?```","",page.read_text(encoding="utf-8"))
    refs=[(m.group(1),m.group(2)) for m in re.finditer(r"!\[([^\]]*)\]\(\s*<?([^)\s>]+)>?(?:\s+[^)]*)?\)",raw)]
    definitions={m.group(1).strip().lower():m.group(2) for m in re.finditer(r"^\s*\[([^\]]+)\]:\s*<?([^>\s]+)>?",raw,re.M)}
    for alt,key in re.findall(r"!\[([^\]]*)\]\[([^\]]*)\]",raw):
        key=(key or alt).strip().lower()
        if key in definitions:
            refs.append((alt,definitions[key]))
    for tag in re.findall(r"<img\b[^>]*>",raw,re.I):
        src=re.search(r"""\bsrc=(["'])(.*?)\1""",tag,re.S)
        alt=re.search(r"""\balt=(["'])(.*?)\1""",tag,re.S)
        if src:
            target=src.group(2)
            bound=re.fullmatch(r"""withBase\((["'])(.*?)\1\)""",target)
            refs.append((alt.group(2) if alt else "",bound.group(2) if bound else target))
    for alt,target in refs:
        uri=urlsplit(target)
        if uri.scheme or uri.netloc or not uri.path or "<" in uri.path:
            continue
        path=(ROOT/"docs/public"/unquote(uri.path.lstrip("/")) if uri.path.startswith("/")
              else page.parent/unquote(uri.path)).resolve()
        if not path.exists() and uri.path.startswith("/diagrams/"):
            path=(ROOT/"docs"/unquote(uri.path.lstrip("/"))).resolve()
        entry={"page":relative(page),"target":target,"alt":alt,"resolved":str(path),"exists":path.is_file()}
        report["image_references"].append(entry)
        if not entry["exists"]:
            report["broken_images"].append(entry)
report["readme_visuals"]=[r for r in report["image_references"] if r["page"]=="README.md"]
consumed={Path(r["resolved"]).stem for r in report["image_references"] if Path(r["resolved"]).parent==DIAGRAMS}
report["public_diagrams_without_direct_image_embed"]=sorted(pngs.keys()-consumed)


def anchors(raw):
    raw=re.sub(r"```[\s\S]*?```","",raw)
    result=set(re.findall(r"""\b(?:id|name)=["']([^"']+)["']""",raw))
    seen=Counter()
    for heading in re.findall(r"^#{1,6}\s+(.+?)\s*#*\s*$",raw,re.M):
        explicit=re.search(r"\{#([^}]+)\}",heading)
        if explicit:
            result.add(explicit.group(1))
            continue
        heading=re.sub(r"<[^>]*>","",heading)
        heading=re.sub(r"\[([^\]]+)\]\([^)]*\)",r"\1",heading)
        slug=re.sub(r"[^\w\s-]","",heading.lower()).replace(" ","-")
        result.add(slug+(f"-{seen[slug]}" if seen[slug] else ""))
        seen[slug]+=1
    return result


def anchor_structure(raw):
    raw=re.sub(r"```[\s\S]*?```","",raw)
    return (re.findall(r"^#{1,6}\s+.+$",raw,re.M),
            re.findall(r"""\b(?:id|name)=["']([^"']+)["']""",raw))


report["broken_local_links"]=[]
for page in sorted(set(pages)):
    raw=re.sub(r"```[\s\S]*?```","",page.read_text(encoding="utf-8"))
    targets=re.findall(r"(?<!!)\[[^\]]+\]\(\s*<?([^)\s>]+)>?(?:\s+[^)]*)?\)",raw)
    targets += [m[1] for m in re.findall(r"""\bhref=(["'])(.*?)\1""",raw)]
    for target in targets:
        uri=urlsplit(target)
        if uri.scheme or uri.netloc or "<" in target or "{{" in target:
            continue
        destination=(ROOT/"docs"/unquote(uri.path.lstrip("/")) if uri.path.startswith("/")
                     else page.parent/unquote(uri.path)).resolve() if uri.path else page
        if not destination.is_relative_to(ROOT):
            report["broken_local_links"].append({"page":relative(page),"target":target,
                "error":"Target escapes repository; not read","preexisting_at_head":False})
            continue
        if not destination.exists() and not destination.suffix:
            for candidate in (destination.with_suffix(".md"),destination/"index.md"):
                if candidate.is_file():
                    destination=candidate
                    break
        error=None
        if not destination.exists():
            error="Missing local target"
        elif destination.suffix==".md" and uri.fragment and not re.fullmatch(r"L\d+(?:-L\d+)?",uri.fragment):
            built=(ROOT/"docs/.vitepress/dist"/destination.relative_to(ROOT/"docs")).with_suffix(".html") if destination.is_relative_to(ROOT/"docs") else None
            actual=anchors(built.read_text(encoding="utf-8")) if built and built.exists() else anchors(destination.read_text(encoding="utf-8"))
            if unquote(uri.fragment) not in actual:
                error="Missing Markdown/HTML anchor"
        if error:
            entry={"page":relative(page),"target":target,"error":error}
            old_page=subprocess.run(["git","show",f"HEAD:{relative(page)}"],cwd=ROOT,capture_output=True,encoding="utf-8")
            old_target=subprocess.run(["git","show",f"HEAD:{relative(destination)}"],cwd=ROOT,capture_output=True,encoding="utf-8")
            unchanged_anchors=old_target.returncode==0 and destination.is_file() and destination.suffix==".md" and (
                anchor_structure(old_target.stdout)==anchor_structure(destination.read_text(encoding="utf-8")))
            entry["preexisting_at_head"]=old_page.returncode==0 and target in old_page.stdout and (
                old_target.returncode!=0 or unchanged_anchors or bool(uri.fragment and unquote(uri.fragment) not in anchors(old_target.stdout)))
            report["broken_local_links"].append(entry)
        elif destination.parent==DIAGRAMS and destination.suffix==".png":
            consumed.add(destination.stem)
report["unreferenced_public_diagrams"]=sorted(pngs.keys()-consumed)

audit=json.loads((ROOT/"docs-diagram-audit.json").read_text(encoding="utf-8-sig"))
report["audit_sha256"]=digest(ROOT/"docs-diagram-audit.json")
report["planned_diagram_dispositions"]=dict(Counter(d["disposition"] for d in audit["diagrams"]))
report["planned_concept_dispositions"]=dict(Counter(d["disposition"] for d in audit["concepts"]))
report["disposition_residuals"]=[]
for item in audit["diagrams"]:
    name,disposition=item["name"],item["disposition"]
    present=name in sources or name in pngs
    if disposition in ("retain","redesign") and not present:
        report["disposition_residuals"].append({"name":name,"disposition":disposition,"error":"Required survivor absent"})
    if disposition in ("merge","remove") and present:
        report["disposition_residuals"].append({"name":name,"disposition":disposition,"target":item.get("target"),"error":"Retirement not complete"})
    target=item.get("target")
    if disposition in ("merge","reuse") and target and target not in pngs:
        report["disposition_residuals"].append({"name":name,"target":target,"error":"Reconciled target not published"})
report["plans"]=[]
repair=json.loads((HERE/"repair-plan.json").read_text(encoding="utf-8"))
repair_entries={entry["name"]:entry for entry in repair["entries"]}
for plan_path in sorted((ROOT/".github/skills/docs-diagram-audit/reports").glob("plan-*.json")):
    plan=json.loads(plan_path.read_text(encoding="utf-8-sig"))
    proposed=plan.get("proposed_diagram_names",[])
    report["plans"].append({"path":relative(plan_path),"area":plan["area"],
        "missing_proposed":[name for name in proposed if name not in pngs],
        "diagram_names":plan["diagram_names"],"proposed_diagram_names":proposed,"document_paths":plan["document_paths"],
        "candidate_statuses":dict(Counter(repair_entries[name]["status"] for name in plan["diagram_names"]+proposed if name in repair_entries))})
owned_pages={name for plan in report["plans"] for name in plan["document_paths"]}
for link in report["broken_local_links"]:
    link["plan_owned_consumer"]=link["page"] in owned_pages

schema=json.loads((ROOT/".github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json").read_text(encoding="utf-8-sig"))
validator=Draft202012Validator(schema)
cross=load_module("iteration_validator",ROOT/".github/skills/docs-diagram-iterate/scripts/validate_iteration_manifest.py")
report["manifests"]=[]
for path in sorted((DIAGRAMS/"reviews").rglob("iteration-manifest.json")):
    manifest=json.loads(path.read_text(encoding="utf-8-sig"))
    errors=[error.message for error in validator.iter_errors(manifest)]+cross.validate_manifest(manifest)
    for stage in [manifest.get("pitch",{})]+manifest.get("passes",[]):
        for field in ("drawio","png","change_record"):
            if stage.get(field) and not (path.parent/stage[field]).is_file() and not (ROOT/stage[field]).is_file():
                errors.append(f"Missing {field}: {stage[field]}")
    report["manifests"].append({"path":relative(path),"errors":errors,
        "assessment":"Historical structural evidence only; not current visual approval"})

changes=[]
raw=subprocess.check_output(["git","status","--porcelain=v1","-z","--untracked-files=all"],cwd=ROOT).decode("utf-8")
records=iter(raw.split("\0"))
for record in records:
    if not record:
        continue
    status,name=record[:2],record[3:]
    if "R" in status or "C" in status:
        next(records,None)
    if name.startswith(("docs/","scripts/docs/",".github/skills/docs-diagram-")) or name in ("README.md","docs-diagram-audit.json","docs-diagram-audit.md","docs-diagram-inventory.json"):
        changes.append({"status":status,"path":name})
report["worktree_changes_vs_head_including_stopped_shards"]=changes
report["worktree_counts_vs_head_including_stopped_shards"]=dict(Counter(
    "removed" if "D" in item["status"] else "added" if item["status"] in ("??","A "," A") else "changed"
    for item in changes))
public_changes=[item for item in changes if re.fullmatch(
    r"docs/diagrams/(?:[^/]+\.(?:png|hash\.txt)|src/[^/]+\.(?:json|drawio))",item["path"])
    and not item["path"].endswith(".schema.json")]
report["public_asset_counts_vs_head_including_stopped_shards"]=dict(Counter(
    "removed" if "D" in item["status"] else "added" if item["status"] in ("??","A "," A") else "changed"
    for item in public_changes))
report["summary"]={
    "xml_checked":report["xml"]["checked"],"xml_errors":len(report["xml"]["errors"]),
    "public_a5_checked":report["public_a5"]["checked"],"public_a5_errors":len(report["public_a5"]["errors"]),
    "png_checked":len(report["images"]),"stamp_errors":len(report["stamp_errors"]),
    "broken_images":len(report["broken_images"]),"readme_visuals":len(report["readme_visuals"]),
    "broken_local_links":len(report["broken_local_links"]),
    "new_broken_local_links_vs_head":sum(not link["preexisting_at_head"] for link in report["broken_local_links"]),
    "new_broken_plan_consumer_links_vs_head":sum(not link["preexisting_at_head"] and link["plan_owned_consumer"] for link in report["broken_local_links"]),
    "manifests_checked":len(report["manifests"]),
    "manifests_with_errors":sum(bool(m["errors"]) for m in report["manifests"]),
    "disposition_residuals":len(report["disposition_residuals"]),
}
failures = [key for key in ("xml_errors", "stamp_errors", "broken_images",
    "public_a5_errors", "new_broken_local_links_vs_head", "manifests_with_errors", "disposition_residuals")
    if report["summary"][key]]
failures += [key for key in ("missing_public_png", "png_without_source", "hash_without_source",
    "generated_without_source", "redundant_generated_drawio", "unreferenced_public_diagrams") if report[key]]
report["status"] = "failed" if failures else "passed"
report["failure_gates"] = failures
(HERE/"catalog-validation.json").write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
print(json.dumps(report["summary"],indent=2))
if failures:
    raise SystemExit("Catalog validation failed: " + ", ".join(failures))
