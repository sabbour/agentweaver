"""Bounded artifact-only corrections; never regenerate an earlier review pass."""
import sys
from pathlib import Path
from xml.etree import ElementTree as ET
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
NAMES = ["guide-architecture-aks-fig1", "guide-architecture-aks-fig5",
         "canonical-aks-network", "guide-example-scenarios-fig3"]


def geometry(cell):
    return cell.find("mxGeometry")


def style(cell, **values):
    parts = dict(p.split("=", 1) if "=" in p else (p, "1")
                 for p in cell.get("style", "").split(";") if p)
    parts.update({key: str(value) for key, value in values.items()})
    cell.set("style", ";".join(f"{k}={v}" for k, v in parts.items()) + ";")


def route(cell, points, **anchors):
    style(cell, **anchors)
    geom = geometry(cell)
    old = geom.find("Array")
    if old is not None:
        geom.remove(old)
    array = ET.SubElement(geom, "Array", {"as": "points"})
    for x, y in points:
        ET.SubElement(array, "mxPoint", {"x": str(x), "y": str(y)})


action = sys.argv[1]
number = int(sys.argv[2])
for name in NAMES:
    folder = ROOT / name
    stem = f"{name}-pass-{number:02d}" if number else f"{name}-pitch"
    if action == "print":
        image = Image.open(folder / f"{stem}.png")
        image.resize((image.width // 2, image.height // 2), Image.Resampling.LANCZOS).save(
            folder / f"{stem}-print.png", dpi=(100, 100))
        continue
    if action != "correct" or number < 2:
        raise ValueError("Use print N or correct N>=2")
    target = folder / f"{stem}.drawio"
    if target.exists():
        raise FileExistsError(f"Review pass is immutable: {target}")
    source = folder / f"{name}-pass-{number-1:02d}.drawio"
    tree = ET.parse(source)
    cells = {c.get("id"): c for c in tree.iter("mxCell")}
    if number == 2:
        for cell in cells.values():
            st = cell.get("style", "")
            if "shape=mxgraph.kubernetes.icon;" in st:
                style(cell, strokeColor="none")
            elif cell.get("id", "").endswith("-icon") and (
                    "shape=process;" in st or "shape=cylinder3;" in st or "shape=cloud;" in st):
                style(cell, fillColor="#fdfbf8")
            if cell.get("edge") == "1" and cell.get("value"):
                # Move labels off short horizontal arrows so heads remain visible.
                a = geometry(cells[cell.get("source")])
                b = geometry(cells[cell.get("target")])
                if a.get("y") == b.get("y"):
                    geometry(cell).set("y", "-12")
        if name == NAMES[0]:
            route(cells["l6"], [(14, 296), (14, 458)],
                  exitX=0, exitY=0.5, entryX=0, entryY=0.5)
            geometry(cells["l6"]).set("y", "-36")
            route(cells["l8"], [(413, 520), (229, 520)],
                  exitX=0.5, exitY=1, entryX=0.85, entryY=1)
            geometry(cells["l8"]).set("y", "0")
            geometry(cells["l9"]).set("y", "0")
        elif name == NAMES[1]:
            cells["s3"].set("value", "credential")
            geometry(cells["s4"]).set("x", "-0.65")
            geometry(cells["s4"]).set("y", "-9")
            geometry(cells["s6"]).set("x", "0.45")
            geometry(cells["s6"]).set("y", "0")
            geometry(cells["s7"]).set("x", "-0.75")
            geometry(cells["s7"]).set("y", "-47")
            geometry(cells["s8"]).set("x", "0.5")
            geometry(cells["s8"]).set("y", "46")
            g = geometry(cells["s-delivery-title"])
            g.set("x", "555")
            g.set("width", "235")
        elif name == NAMES[2]:
            g = geometry(cells["n-egress-title"])
            g.set("x", "330")
            g.set("width", "460")
        else:
            for nid in ["auth", "project", "team"]:
                g = geometry(cells[f"{nid}-icon"])
                g.set("y", "6")
                g.set("width", "20")
                g.set("height", "20")
            g = geometry(cells["m-observe-title"])
            g.set("x", "305")
            g.set("width", "486")
            style(cells["m-confirm-body"], fontSize=8)
            route(cells["m5"], [(677, 380), (246, 380)],
                  exitX=0.5, exitY=1, entryX=0.92, entryY=0)
    elif number == 3:
        if name == NAMES[0]:
            route(cells["l6"], [], exitX=0.5, exitY=1, entryX=0.5, entryY=0)
            geometry(cells["l6"]).set("y", "0")
            g = geometry(cells["l-turns-title"])
            g.set("x", "330")
            g.set("width", "460")
        elif name == NAMES[1]:
            geometry(cells["s3"]).set("y", "-28")
            geometry(cells["s8"]).set("x", "-0.2")
            geometry(cells["s8"]).set("y", "-50")
        elif name == NAMES[2]:
            route(cells["n-public"], [(810, 290), (810, 374), (413, 374)],
                  exitX=1, exitY=0.5, entryX=0.5, entryY=0)
            g = geometry(cells["n-egress-title"])
            g.set("x", "470")
            g.set("width", "325")
    elif number == 4 and name == NAMES[3]:
        route(cells["m4"], [(413, 371), (211.1, 371)],
              exitX=0.5, exitY=1, entryX=0.77, entryY=0)
        route(cells["m5"], [(677, 380), (245.6, 380)],
              exitX=0.5, exitY=1, entryX=0.92, entryY=0)
    tree.write(target, encoding="utf-8", xml_declaration=True)
    print(target.name)
