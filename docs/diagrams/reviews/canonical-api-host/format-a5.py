"""Normalize the template's working coordinates to draw.io's 100-dpi A5 sheet."""
import re
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

source, destination = map(Path, sys.argv[1:3])
root = ET.fromstring(source.read_text(encoding="utf-8"))
model = root.find("./diagram/mxGraphModel")
sx, sy = 827 / float(model.get("pageWidth")), 583 / float(model.get("pageHeight"))
model.set("pageWidth", "827")
model.set("pageHeight", "583")
scale = min(sx, sy)
for element in model.iter():
    if element.tag in {"mxGeometry", "mxPoint"}:
        relative = element.tag == "mxGeometry" and element.get("relative") == "1"
        for key in ("x", "y", "width", "height"):
            if key in element.attrib and not (relative and key in {"x", "y"}):
                factor = sx if key in {"x", "width"} else sy
                element.set(key, str(round(float(element.get(key)) * factor, 3)))
    if element.tag == "mxCell":
        style = element.get("style", "")
        style = re.sub(r"fontSize=(\d+(?:\.\d+)?);",
                       lambda m: f"fontSize={round(float(m[1]) * scale, 3)};", style)
        element.set("style", style)
        if element.get("id", "").endswith("-accent"):
            element.find("mxGeometry").set("width", "5")
        if "value" in element.attrib:
            element.set("value", re.sub(r"font-size:(\d+)px",
                        lambda m: f"font-size:{round(float(m[1]) * scale, 3)}px",
                        element.get("value")))
ET.indent(root, space="  ")
with destination.open("x", encoding="utf-8") as output:
    output.write('<?xml version="1.0" encoding="UTF-8"?>\n')
    output.write(ET.tostring(root, encoding="unicode") + "\n")
