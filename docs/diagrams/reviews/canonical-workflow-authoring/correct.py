"""Save an immutable correction-only pass, never a redesigned composition."""
from pathlib import Path
import argparse
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
NAME = "canonical-workflow-authoring"
parser = argparse.ArgumentParser()
parser.add_argument("number", type=int, choices=[2, 3, 4])
args = parser.parse_args()
source = HERE / f"{NAME}-pass-{args.number-1:02}.drawio"
target = HERE / f"{NAME}-pass-{args.number:02}.drawio"
if target.exists():
    raise SystemExit(f"Refusing to overwrite preserved pass: {target}")
tree = ET.parse(source)
cells = {cell.get("id"): cell for cell in tree.iter("mxCell")}


def geometry(id):
    return cells[id].find("mxGeometry")


if args.number == 2:
    # Route the semantic return below the context connector, eliminating a crossing.
    route = geometry("e06").find("Array")
    for point in list(route):
        route.remove(point)
    for x, y in [(1100, 677), (1100, 565), (650, 565), (650, 430)]:
        ET.SubElement(route, "mxPoint", x=str(x), y=str(y))
    # Keep the return above neither the generator nor the context input.
    cells["e06"].set("style", cells["e06"].get("style").replace("entryY=0.24", "entryY=0.86"))
    route[-1].set("y", "520")
    geometry("e06").find("mxPoint").set("y", "0")
    # Make room for text without removing any original wording or hierarchy.
    geometry("generation-error").set("height", "145")
    geometry("generation-error-accent").set("height", "145")
    geometry("generation-error-badge").set("y", "925")
    for id in ["registry", "registry-accent", "reload-error", "reload-error-accent"]:
        geometry(id).set("height", "145")
    geometry("registry-badge").set("y", "1530")
    geometry("reload-error-badge").set("y", "1530")
    geometry("reload-error-meta").set("height", "46")
    cells["reload-error-meta"].set(
        "style", cells["reload-error-meta"].get("style").replace("verticalAlign=middle", "verticalAlign=top"))
    geometry("persistence-boundary").set("height", "600")
    cells["persistence-boundary"].set(
        "style", cells["persistence-boundary"].get("style").replace("align=left", "align=right")
        + "spacingRight=20;")
    geometry("e08").set("x", "-0.65")
elif args.number == 3:
    geometry("reload-error-meta").set("width", "195")
    geometry("e07").find("Array")[-1].set("y", "887.5")
    geometry("e07").set("x", "-0.3")

ET.indent(tree, space="  ")
tree.write(target, encoding="utf-8", xml_declaration=True)
print(target)
