import sys
from xml.etree import ElementTree as ET
path = sys.argv[1]
root = ET.parse(path)
name = root.find("diagram").get("id")
cells = {c.get("id"): c for c in root.iter("mxCell")}
if name in ("00-system-overview-fig1", "agent-framework-fig1", "agent-framework-fig2", "agent-definition-fig1", "api-core-fig6"):
    for c in cells.values():
        if c.get("edge") != "1" or c.get("id") in ("blocked-to-port", "embedded-to-materialize"):
            continue
        a, b = (cells[c.get(key)].find("mxGeometry") for key in ("source", "target"))
        ax, ay, bx, by = map(float, (a.get("x"), a.get("y"), b.get("x"), b.get("y")))
        if ax == bx or ay == by:
            continue
        s = dict(t.split("=",1) for t in c.get("style").split(";") if "=" in t)
        source_y = .4 if ax < bx else .7
        target_y = .85
        s.update(exitY=str(source_y), entryY=str(target_y))
        c.set("style", ";".join(f"{k}={v}" for k,v in s.items())+";")
        points = c.find("mxGeometry/Array")
        points[0].set("y", str(ay+87*source_y))
        points[-1].set("y", str(by+87*target_y))
    if name == "00-system-overview-fig1":
        cells["mcp-to-api"].find("mxGeometry").set("x", "0.35")
    if name == "agent-framework-fig1":
        cells["adapter-to-port"].set("value", "")
        cells["port-to-decision"].find("mxGeometry").set("x", "-0.4")
        cells["state-to-mergeadapter"].find("mxGeometry").set("x", "0.15")
root.write(path, encoding="utf-8", xml_declaration=True)
