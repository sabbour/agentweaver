import sys
from xml.etree import ElementTree as ET
path = sys.argv[1]
root = ET.parse(path)
name = root.find("diagram").get("id")
cells = {c.get("id"): c for c in root.iter("mxCell")}
if name == "testing-strategy-fig4":
    c = cells["state-to-seam"]
    c.set("id", "seam-to-state")
    c.set("source", "seam")
    c.set("target", "state")
    c.set("value", "writes")
    c.set("style", c.get("style").replace("exitY=1;", "exitY=0;").replace("entryY=0;", "entryY=1;"))
if name == "canonical-testing-boundary":
    c = cells["git-to-planning"]
    c.set("id", "host-to-planning")
    c.set("source", "host")
    c.set("value", "planning")
    c.set("style", c.get("style").replace("exitY=0.5;", "exitY=0.8;").replace("entryY=0.5;", "entryY=0.8;"))
    g = c.find("mxGeometry")
    g.set("x", "0")
    a = ET.SubElement(g, "Array", {"as": "points"})
    ET.SubElement(a, "mxPoint", {"x":"414","y":"187.6"})
    ET.SubElement(a, "mxPoint", {"x":"414","y":"407.6"})
root.write(path, encoding="utf-8", xml_declaration=True)
