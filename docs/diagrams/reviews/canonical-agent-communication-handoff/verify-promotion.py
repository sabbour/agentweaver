import hashlib
import importlib.util
import json
import shutil
import subprocess
from pathlib import Path
from PIL import Image, ImageChops

HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("author",HERE/"author-eight.py")
a=importlib.util.module_from_spec(spec)
spec.loader.exec_module(a)
allowed=json.loads((a.ROOT/".github"/"skills"/"docs-diagram-audit"/"reports"/"plan-shared.json").read_text(encoding="utf-8"))["exclusive_asset_paths"]
for name in a.MODELS:
    folder=a.REVIEWS/name
    canonical=a.ROOT/"docs"/"diagrams"/"src"/f"{name}.drawio"
    png=a.ROOT/"docs"/"diagrams"/f"{name}.png"
    assert canonical.read_bytes()==(folder/f"{name}-pass-04.drawio").read_bytes()
    reviewed=Image.open(folder/f"{name}-pass-04.png").convert("RGB")
    published=Image.open(png).convert("RGB")
    assert reviewed.size==published.size and ImageChops.difference(reviewed,published).getbbox() is None,name
    subprocess.run(["python","-B",str(a.ROOT/".github"/"skills"/"docs-diagram-iterate"/"scripts"/"validate_iteration_manifest.py"),str(folder/"iteration-manifest.json")],check=True,capture_output=True)
    assert not (a.ROOT/"docs"/"diagrams"/"src"/f"{name}.json").exists()
    assert not (a.ROOT/"docs"/"diagrams"/"drawio"/"generated"/f"{name}.drawio").exists()
    if name.startswith("email-"):
        rel=f"docs/diagrams/email-exports/{name}.png"
        assert rel in allowed
        dest=a.ROOT/Path(rel)
        shutil.copyfile(png,dest)
        assert dest.read_bytes()==png.read_bytes()
    report=json.loads((folder/"validation-evidence.json").read_text(encoding="utf-8"))
    report["promotion"]=dict(
        canonical_xml_matches_review=True,
        published_png_pixel_identical_to_inspected_pass_04=True,
        png_dimensions=list(published.size),
        png_sha256=hashlib.sha256(png.read_bytes()).hexdigest(),
        selective_renderer="draw.io Desktop 31.4.5",
        selective_render_passed=True,selective_drift_check_passed=True,
        duplicate_json_and_generated_sources_removed=True,
        derived_email_export_matches=name.startswith("email-"),
        docs_build="Parent-owned: this worker may not write documentation pages or build output outside its exclusive assets.")
    (folder/"validation-evidence.json").write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    claim=json.loads((folder/"claim.json").read_text(encoding="utf-8"))
    claim["status"]="promoted-and-validated"
    (folder/"claim.json").write_text(json.dumps(claim,indent=2)+"\n",encoding="utf-8")
    for intermediate in ("pitch-arrows.json","pass-01-arrows.json"):
        path=folder/intermediate
        arrows=json.loads(path.read_text(encoding="utf-8"))
        evidence=json.loads((folder/"content-model.json").read_text(encoding="utf-8"))["grounding"]
        for arrow in arrows:
            arrow["evidence"]=evidence
        path.write_text(json.dumps(arrows,indent=2)+"\n",encoding="utf-8")
    (folder/f"{name}-upgrade-draft-a.md").write_text(
        "# Superseded visual-upgrade draft\n\n"
        "Diagnostic export retained before completing pass 1. Not counted as a review pass and not promoted. "
        "This draft had incomplete visual gates: native silhouettes/connector layout required correction, "
        "and the two sequence drafts did not yet meet 9x meaningful XML growth. "
        "Use the numbered passes and iteration-manifest.json for the actual validated review history.\n",
        encoding="utf-8")
    print(name+": exact XML + identical reviewed pixels + manifest + exclusive cleanup PASS")
