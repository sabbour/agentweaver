"""Re-export the preserved lineage at draw.io's actual A5 page dimensions."""
from pathlib import Path
import copy
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

from PIL import Image

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
OUT = HERE / "a5-final"
CLI = Path(r"C:\Users\asabbour\.copilot\session-state\f6a87a42-fc18-4c23-a5f5-3e1c5c41d367\files\drawio-cli\app\draw.io.exe")
manifest = json.loads((HERE / "iteration-manifest.json").read_text())
stage_number = int(sys.argv[1])
stage = manifest["pitch"] if stage_number == 0 else manifest["passes"][stage_number - 1]
assert not stage["drawio"].startswith("a5-"), "Lineage has already been corrected"
OUT.mkdir(exist_ok=True)
target = OUT / stage["drawio"]
assert not target.exists(), "Never overwrite an earlier review export"
tree = ET.parse(HERE / stage["drawio"])
model = tree.find(".//mxGraphModel")
sx, sy = 583 / float(model.get("pageWidth")), 827 / float(model.get("pageHeight"))
scale = min(sx, sy)
model.set("pageWidth", "583")
model.set("pageHeight", "827")


def number(value):
    return f"{value:.4f}".rstrip("0").rstrip(".")


for element in tree.iter():
    if element.tag in ("mxGeometry", "mxPoint"):
        for attribute, factor in (("x", sx), ("y", sy), ("width", sx), ("height", sy)):
            if attribute in ("x", "y") and element.get("relative") == "1":
                continue
            if attribute in element.attrib:
                element.set(attribute, number(float(element.get(attribute)) * factor))
    if element.tag == "mxCell" and element.get("style"):
        if element.get("value") and "<" in element.get("value"):
            element.set("value", re.sub(
                r"([0-9.]+)px",
                lambda match: number(float(match.group(1)) * scale) + "px",
                element.get("value"),
            ))
        style = element.get("style")
        for key in ("fontSize", "strokeWidth", "spacing", "spacingLeft", "spacingRight",
                    "spacingTop", "spacingBottom", "jettySize", "jumpSize"):
            style = re.sub(
                rf"(?<=;){key}=([0-9.]+)(?=;)",
                lambda match, key=key: key + "=" + number(float(match.group(1)) * scale),
                ";" + style,
            ).lstrip(";")
        element.set("style", style)
for cell in tree.iter("mxCell"):
    if cell.get("id", "").endswith("-accent"):
        geometry = cell.find("mxGeometry")
        if geometry is not None:
            geometry.set("width", "5")
    if stage_number == 4 and cell.get("id") in ("generator-title", "registry-title"):
        geometry = cell.find("mxGeometry")
        geometry.set("x", number(float(geometry.get("x")) + 12))
        geometry.set("width", number(float(geometry.get("width")) - 12))
ET.indent(tree, space="  ")
tree.write(target, encoding="utf-8", xml_declaration=True)
image = OUT / stage["png"]
subprocess.run(
    [str(CLI), "--export", "--format", "png", "--border", "16", "--scale", "2",
     "--output", str(image), str(target)], check=True, timeout=120)
with Image.open(image) as raster:
    raster.thumbnail((583, 827), Image.Resampling.LANCZOS)
    print_image = Image.new("RGB", (583, 827), "#efeae7")
    print_image.paste(raster, ((583-raster.width)//2, (827-raster.height)//2))
    print_image.save(OUT / (image.stem + "-print.png"))
print(target)
print(image)
