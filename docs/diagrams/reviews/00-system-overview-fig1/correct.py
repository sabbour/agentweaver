import sys
from pathlib import Path
from xml.etree import ElementTree as ET

path = Path(sys.argv[1])
root = ET.parse(path)
cells = {c.get("id"): c for c in root.iter("mxCell")}
name = root.find("diagram").get("id")

def style(cell, **changes):
    parts = dict(token.split("=", 1) if "=" in token else (token, "1")
                 for token in cell.get("style", "").split(";") if token)
    parts.update({k: str(v) for k, v in changes.items()})
    cell.set("style", ";".join(f"{k}={v}" for k, v in parts.items())+";")

short = {
    "parse + compose": "compose", "retain logic;<br>replace planner": "use seam",
    "latest checkpoint": "checkpoint", "SendResponseAsync": "deliver",
    "rebuild then<br>resume": "resume", "policy satisfied": "allowed",
    "delegate /<br>map": "map", "matching<br>response": "decision",
    "read saved<br>output": "read state", "same review<br>port": "retry",
    "configure<br>services": "inject", "configure<br>fixture": "configure",
    "selected<br>endpoint": "endpoint", "metadata<br>valid": "valid",
    "matched route": "matched", "current session": "session",
    "eligible<br>entries": "eligible", "local instance": "local",
    "forward broker": "broker", "effective<br>definition": "definition",
    "nonempty,<br>cleared": "cleared", "eligible<br>handoff": "handoff",
    "blocked output": "blocked", "read counts": "counts",
}
for cell in list(cells.values()):
    if cell.get("edge") != "1":
        continue
    a = cells[cell.get("source")].find("mxGeometry")
    b = cells[cell.get("target")].find("mxGeometry")
    ax, ay, bx, by = [float(v) for v in (a.get("x"), a.get("y"), b.get("x"), b.get("y"))]
    geometry = cell.find("mxGeometry")
    points = geometry.find("Array")
    value = short.get(cell.get("value"), cell.get("value"))
    cell.set("value", value)
    if ax == bx and points is None:
        ET.SubElement(geometry, "mxPoint", {"x": "65", "y": "0", "as": "offset"})
    elif ax == bx and points is not None:
        cell.set("value", "")
    elif ay != by:
        geometry.set("x", "-0.35")
        if len(value.replace("<br>", "")) > 11:
            cell.set("value", value.split(" ")[0])

def reroute(edge_id, points, exit_x, exit_y, entry_x, entry_y):
    cell = cells[edge_id]
    cell.set("value", "")
    style(cell, exitX=exit_x, exitY=exit_y, entryX=entry_x, entryY=entry_y)
    geometry = cell.find("mxGeometry")
    for child in list(geometry):
        geometry.remove(child)
    array = ET.SubElement(geometry, "Array", {"as": "points"})
    for x, y in points:
        ET.SubElement(array, "mxPoint", {"x": str(x), "y": str(y)})

if name == "agent-framework-fig1":
    reroute("blocked-to-port", [(14,480.5),(14,211),(724.4,211)], 0,.5,.8,0)
    cells["state-to-mergeadapter"].find("mxGeometry").set("x", "0.0")
    cells["port-to-decision"].find("mxGeometry").set("x", "-0.55")
if name == "agent-definition-fig1":
    reroute("embedded-to-materialize", [(207.5,431),(552.9,431)], .5,1,.3,0)
if name in ("data-persistence-fig1", "canonical-testing-boundary"):
    for key in ("group-0", "group-1"):
        cells[key].find("mxGeometry").set("height", "337")
if name == "testing-strategy-fig1":
    style(cells["prerequisite-symbol"], size=5)
    cells["group-1-title"].set("value", "PROVIDER / PROCESS / AUTH / LIVE")
root.write(path, encoding="utf-8", xml_declaration=True)
