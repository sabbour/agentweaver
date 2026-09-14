"""Fail-closed Fluent normalization and geometric validation for editable XML.

Unannotated semantic diagrams require an explicit role binding or structured source.
Never guess which prose can be deleted or which large native shape is a group.
"""
import argparse
import copy
import hashlib
from html import unescape
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SYSTEM = json.loads((ROOT / "docs/diagrams/drawio/design-system.json").read_text())
CARDS, TYPE, COLORS = SYSTEM["cards"], SYSTEM["typography"], SYSTEM["palette"]
ROLES = {"card", "surface", "accent", "shadow", "title", "subtitle", "meta", "icon",
         "badge", "group", "group-label", "connector", "junction", "edge-label",
         "lifeline", "anchor", "activation", "message", "note", "fragment", "section","canvas","layout","label-leader",
         "notation-node"}


def style(cell):
    return dict(p.split("=", 1) for p in cell.get("style", "").split(";") if "=" in p)


def text(cell):
    return unescape(re.sub("<[^>]*>", "", cell.get("value", ""))).strip()


def analyze(raw, bindings=None):
    tree = ET.fromstring(raw)
    if tree.tag != "mxfile" or not tree.findall("diagram/mxGraphModel/root"):
        raise ValueError("Expected uncompressed mxfile/diagram/mxGraphModel/root")
    if len(tree.findall("diagram")) != 1:
        raise ValueError("Canonical source must contain exactly one diagram")
    cells = {c.get("id"): c for c in tree.iter("mxCell")}
    if len(cells) != len(list(tree.iter("mxCell"))) or None in cells:
        raise ValueError("Duplicate or missing cell identifiers")
    snapshot = tree.get("approvedSnapshot")
    if snapshot:
        registry = json.loads((ROOT / "docs/diagrams/drawio/approved-snapshots.json").read_text())
        approved = registry.get(snapshot)
        fingerprint = hashlib.sha256(ET.canonicalize(raw, strip_text=True).encode("utf-8")).hexdigest()
        if (not approved or tree.find("diagram").get("id") != approved["diagram"]
                or fingerprint != approved["canonical_xml_sha256"]):
            return tree, {"status": "blocked", "errors": [{
                "code": "approved-snapshot-mismatch", "cell": "document",
                "detail": "The preserved pilot is immutable. Changed content, geometry or style requires explicit reinspection, not a profile-wide exemption."
            }], "uncertainty": []}
        return tree, {"status": "passed", "errors": [], "uncertainty": [],
                      "validation_profile": snapshot}
    errors, uncertain = [], []
    bindings = bindings or {}
    for identifier, role in bindings.items():
        if identifier not in cells or role not in ROLES:
            raise ValueError(f"Invalid explicit role binding {identifier}: {role}")
        cells[identifier].set("fluentRole", role)
    cache = {}

    def box(identifier, visited=None):
        if identifier in cache:
            return cache[identifier]
        visited = set() if visited is None else visited
        if identifier in visited:
            raise ValueError(f"Parent cycle at {identifier}")
        visited.add(identifier)
        c = cells[identifier]
        g = c.find("mxGeometry")
        if g is None:
            return (0, 0, 0, 0)
        x, y, w, h = (float(g.get(k, 0)) for k in ("x", "y", "width", "height"))
        parent = c.get("parent")
        if parent in cells and parent not in ("0", "1"):
            px, py, _, _ = box(parent, visited)
            x, y = x + px, y + py
        cache[identifier] = (x, y, w, h)
        return cache[identifier]

    def fail(code, identifier, detail):
        errors.append({"code": code, "cell": identifier, "detail": detail})

    for identifier, c in cells.items():
        if identifier in ("0", "1"):
            continue
        role = c.get("fluentRole")
        s, value = style(c), text(c)
        if "native-libraries:" in value or re.search(r"\b(?:debug|provenance):", value, re.I):
            fail("debug-text", identifier, value)
        if role not in ROLES:
            uncertain.append({"cell": identifier, "reason": "No explicit semantic/visual role; preserve source until bound or regenerated."})
            continue
        x, y, w, h = box(identifier)
        if value and role not in {"card", "group"}:
            expected_family = TYPE["monospaceFamily"] if role == "meta" else TYPE["family"]
            families = re.findall(r"font-family\s*:\s*([^;\"<>]+)", unescape(c.get("value", "")))
            if s.get("fontFamily") != expected_family or any(expected_family not in f for f in families):
                fail("font-family", identifier, expected_family)
            sizes = {"title": TYPE["titlePx"], "subtitle": TYPE["subtitlePx"], "meta": TYPE["metaPx"],
                     "edge-label": TYPE["connectorLabelPx"], "group-label": TYPE["titlePx"],
                     "badge": TYPE["badgePx"], "note": TYPE["metaPx"], "section": TYPE["metaPx"]}
            if role in sizes:
                css_sizes = re.findall(r"font-size\s*:\s*([\d.]+)px", unescape(c.get("value", "")))
                if float(s.get("fontSize", 0)) != sizes[role] or any(float(v) != sizes[role] for v in css_sizes):
                    fail("font-size", identifier, sizes[role])
            limit = 60 if role == "title" else 96 if role in ("subtitle", "meta") else 160
            if len(value) > limit:
                fail("prose-block", identifier, f"{len(value)} characters exceeds {limit}; edit structured copy, never truncate.")
        if role == "card" and (w != CARDS["widthPx"] or h not in (CARDS["minimumHeightPx"],CARDS["threeLineHeightPx"])):
            fail("card-geometry", identifier, [w, h])
        if role == "surface":
            if s.get("fillColor") != COLORS["surface"] or s.get("absoluteArcSize") != "1" or float(s.get("arcSize", 0)) != CARDS["radiusPx"]*2:
                fail("card-surface", identifier, "Incorrect surface/radius")
            if float(c.find("mxGeometry").get("x", 0)) != CARDS["semanticAccentPx"]:
                fail("accent-width", identifier, "Foreground inset must expose exactly the semantic accent")
        if role == "title" and float(c.find("mxGeometry").get("x", 0)) != CARDS["textInsetPx"]:
            fail("card-padding", identifier, "Incorrect title inset")
        if role in ("title","subtitle","meta"):
            parent=c.get("parent")
            if parent in cells and cells[parent].get("fluentRole") == "card":
                px,py,pw,ph=box(parent)
                if y<py+CARDS["paddingPx"]-3 or y+h>py+ph-CARDS["paddingPx"]+3 or x+w>px+pw-CARDS["paddingPx"]+.1:
                    fail("text-overflow",identifier,"Copy does not fit the calibrated card; shorten only after semantic review")
        if role in ("title","group-label","edge-label") and value:
            weights=re.findall(r"font-weight\s*:\s*([\d]+)",unescape(c.get("value","")))
            if not weights or any(int(weight)!=TYPE["titleWeight"] for weight in weights):
                fail("font-weight",identifier,TYPE["titleWeight"])
        if role == "icon":
            if w != CARDS["iconPx"] or h != CARDS["iconPx"] or cells.get(c.get("parent"), ET.Element("none")).get("fluentRole") != "card":
                fail("unstyled-symbol", identifier, "Icons must be normalized within a Fluent card")
            elif abs(x-box(c.get("parent"))[0]-CARDS["iconInsetPx"])>.1 or abs(y-box(c.get("parent"))[1]-(box(c.get("parent"))[3]-h)/2)>.1:
                fail("icon-alignment",identifier,"Incorrect inset or optical center")
        if role == "shadow" and float(s.get("opacity", 100)) > 7:
            fail("shadow", identifier, "Heavy/default draw.io shadow")
        if role == "accent" and s.get("fillColor") not in {tone["foreground"] for tone in SYSTEM["badges"].values()}:
            fail("semantic-color",identifier,"Accent is outside the semantic palette")
        if role == "group" and s.get("fillColor") not in (COLORS["groupTier1"],COLORS["groupTier2"]):
            fail("group-surface",identifier,"Group does not use a warm tier surface")
        if role == "edge-label" and s.get("fillColor") != COLORS["surface"]:
            fail("label-background", identifier, "Missing matching-surface chip")
        if role in ("connector", "message") and s.get("jumpStyle") != "arc":
            fail("missing-bridges", identifier, "Explicit rounded crossing bridge policy required")
        if role in ("connector","message") and s.get("endArrow") in ("block","classic","blockThin","classicThin"):
            if s.get("endArrow")!=SYSTEM["connectors"]["arrowType"] or float(s.get("endSize",0))!=SYSTEM["connectors"]["arrowSizePx"]:
                fail("arrow-token",identifier,"Use the approved calibrated marker and size")
        if role == "junction" and (w != 5 or h != 5):
            fail("junction-size", identifier, [w, h])

    card_boxes = {i: box(i) for i,c in cells.items() if c.get("fluentRole") in ("card", "notation-node")}
    for identifier,bounds in card_boxes.items():
        if cells[identifier].get("fluentRole") != "card":
            continue
        children=[c for c in cells.values() if c.get("parent")==identifier]
        for role,count in (("title",1),("surface",1),("accent",1),("icon",1),("shadow",len(CARDS["shadowLayers"]))):
            if sum(c.get("fluentRole")==role for c in children)!=count:
                fail("card-anatomy",identifier,f"Expected {count} {role} cells")
        layers=[c for c in children if c.get("fluentRole")=="shadow"]
        for c,expected in zip(layers,CARDS["shadowLayers"]):
            g=c.find("mxGeometry")
            if (float(g.get("x",0))!=0 or float(g.get("y",0))!=expected["offsetY"]
                    or float(style(c).get("opacity",0))!=expected["opacity"]):
                fail("shadow",c.get("id"),"Incorrect layered elevation token")

    def intersects(a, b):
        return a[0] < b[0]+b[2]-.1 and b[0] < a[0]+a[2]-.1 and a[1] < b[1]+b[3]-.1 and b[1] < a[1]+a[3]-.1

    for i, (identifier, bounds) in enumerate(card_boxes.items()):
        for other, other_bounds in list(card_boxes.items())[i+1:]:
            if intersects(bounds, other_bounds):
                fail("card-collision", identifier, other)
    routes, arrow_ends = [], {}
    for identifier, c in cells.items():
        if c.get("edge") != "1" or c.get("fluentRole") == "lifeline":
            continue
        source, target = c.get("source"), c.get("target")
        if c.get("fluentRole")=="label-leader" and not source and not target:
            parent=c.get("parent")
            px,py,_,_=box(parent) if parent in cells else (0,0,0,0)
            ps=[]
            for kind in ("sourcePoint","targetPoint"):
                point=c.find(f"mxGeometry/mxPoint[@as='{kind}']")
                if point is None:
                    fail("label-leader-endpoint",identifier,kind)
                else:ps.append((float(point.get("x"))+px,float(point.get("y"))+py))
            if len(ps)==2:
                a,b=ps
                if abs(a[0]-b[0])>.01 and abs(a[1]-b[1])>.01:fail("non-orthogonal-label-leader",identifier,ps)
                bounds=(min(a[0],b[0])-.01,min(a[1],b[1])-.01,abs(a[0]-b[0])+.02,abs(a[1]-b[1])+.02)
                for other,card in card_boxes.items():
                    if intersects(bounds,card):fail("label-leader-collision",identifier,other)
            continue
        if source not in cells or target not in cells:
            uncertain.append({"cell": identifier, "reason": "Explicit endpoint bindings required."})
            continue
        s = style(c)
        sx, sy, sw, sh = box(source)
        tx, ty, tw, th = box(target)
        start = (sx+sw*float(s.get("exitX", .5)), sy+sh*float(s.get("exitY", .5)))
        end = (tx+tw*float(s.get("entryX", .5)), ty+th*float(s.get("entryY", .5)))
        parent=c.get("parent")
        px,py,_,_=box(parent) if parent in cells else (0,0,0,0)
        ps = [start] + [(float(p.get("x"))+px, float(p.get("y"))+py) for p in c.findall("mxGeometry/Array/mxPoint")] + [end]
        routes.append((identifier, c, ps))
        if s.get("endArrow", "none") != "none":
            key = (target, round(end[0], 3), round(end[1], 3))
            if key in arrow_ends:
                fail("double-arrowhead", identifier, arrow_ends[key])
            arrow_ends[key] = identifier
        for a,b in zip(ps, ps[1:]):
            if abs(a[0]-b[0]) > .01 and abs(a[1]-b[1]) > .01:
                fail("non-orthogonal", identifier, [a,b])
            for card, bounds in card_boxes.items():
                inner = (bounds[0]+1, bounds[1]+1, bounds[2]-2, bounds[3]-2)
                segment = (min(a[0],b[0])-.01, min(a[1],b[1])-.01, abs(a[0]-b[0])+.02, abs(a[1]-b[1])+.02)
                if intersects(segment, inner):
                    fail("connector-collision", identifier, card)
    for identifier,c in cells.items():
        if c.get("fluentRole") in ("edge-label","group-label"):
            for other,bounds in card_boxes.items():
                if intersects(box(identifier),bounds):
                    fail("label-collision", identifier, other)
        if c.get("fluentRole")=="group-label":
            for other,_,ps in routes:
                for a,b in zip(ps,ps[1:]):
                    bounds=(min(a[0],b[0])-.01,min(a[1],b[1])-.01,abs(a[0]-b[0])+.02,abs(a[1]-b[1])+.02)
                    if intersects(box(identifier),bounds):
                        fail("group-label-route-collision",identifier,other)
        if c.get("fluentRole")=="label-leader":
            x,y,w,h=box(identifier)
            if w and h:
                fail("non-orthogonal-label-leader",identifier,[x,y,w,h])
            for other,bounds in card_boxes.items():
                if intersects((x-.1,y-.1,w+.2,h+.2),bounds):
                    fail("label-leader-collision",identifier,other)
    def on_segment(point, a, b):
        return (abs(a[0]-b[0]) < .01 and abs(point[0]-a[0]) < .1 and min(a[1],b[1])-.1 <= point[1] <= max(a[1],b[1])+.1
                or abs(a[1]-b[1]) < .01 and abs(point[1]-a[1]) < .1 and min(a[0],b[0])-.1 <= point[0] <= max(a[0],b[0])+.1)

    def directions(point, ps):
        result=set()
        for a,b in zip(ps,ps[1:]):
            if not on_segment(point,a,b):
                continue
            for end in (a,b):
                if abs(end[0]-point[0]) > .1:
                    result.add("right" if end[0]>point[0] else "left")
                if abs(end[1]-point[1]) > .1:
                    result.add("down" if end[1]>point[1] else "up")
        return result

    dots={i:(box(i)[0]+box(i)[2]/2,box(i)[1]+box(i)[3]/2)
          for i,c in cells.items() if c.get("fluentRole") == "junction"}
    for identifier,point in dots.items():
        rays=set().union(*(directions(point,ps) for _,_,ps in routes))
        if len(rays)<3:
            fail("false-junction",identifier,"Dot does not mark a physical split/merge")
    candidates={p for _,_,ps in routes for p in ps[1:-1]}
    for point in candidates:
        related=[(i,c,ps) for i,c,ps in routes if directions(point,ps)]
        if len(related)<2 or len(set().union(*(directions(point,ps) for _,_,ps in related)))<3:
            continue
        if any(b[0]-.1<=point[0]<=b[0]+b[2]+.1 and b[1]-.1<=point[1]<=b[1]+b[3]+.1 for b in card_boxes.values()):
            continue
        shared_source=len({c.get("source") for _,c,_ in related})==1
        shared_target=len({c.get("target") for _,c,_ in related})==1
        if (shared_source or shared_target) and not any(abs(p[0]-point[0])<3 and abs(p[1]-point[1])<3 for p in dots.values()):
            fail("missing-junction",related[0][0],point)
    for index,(identifier,c,ps) in enumerate(routes):
        for other,other_cell,other_ps in routes[index+1:]:
            for a,b in zip(ps,ps[1:]):
                for q,r in zip(other_ps,other_ps[1:]):
                    av,bv=abs(a[0]-b[0])<.01,abs(q[0]-r[0])<.01
                    if av==bv:
                        axis=1 if av else 0
                        fixed=0 if av else 1
                        overlap=min(max(a[axis],b[axis]),max(q[axis],r[axis]))-max(min(a[axis],b[axis]),min(q[axis],r[axis]))
                        if abs(a[fixed]-q[fixed])<.1 and overlap>1 and (b[axis]-a[axis])*(r[axis]-q[axis])<0:
                            fail("opposed-trunks",identifier,other)
                        continue
                    vertical=(a,b) if av else (q,r)
                    horizontal=(q,r) if av else (a,b)
                    cross=(vertical[0][0],horizontal[0][1])
                    if not on_segment(cross,*vertical) or not on_segment(cross,*horizontal):
                        continue
                    if any(abs(p[0]-cross[0])<3 and abs(p[1]-cross[1])<3 for p in dots.values()):
                        continue
                    distances=[abs(p[0]-cross[0])+abs(p[1]-cross[1]) for p in (*vertical,*horizontal)]
                    if min(distances)>.1 and min(distances)<18:
                        fail("ambiguous-crossing",identifier,{"other":other,"point":cross,"reason":"Crossing too near an elbow for a real bridge"})
    for model in tree.iter("mxGraphModel"):
        pw,ph=float(model.get("pageWidth",0)),float(model.get("pageHeight",0))
        scale=float(model.get("pageScale",1))
        if (pw,ph) not in ((559,794),(794,559)) or scale<=0:
            fail("paper-size","page","One A5 portrait or landscape page is required")
        canvases=[box(i) for i,c in cells.items() if c.get("fluentRole")=="canvas"]
        area = sum(b[2]*b[3] for b in canvases) if canvases else pw*ph*scale*scale
        if card_boxes and area and sum(w*h for x,y,w,h in card_boxes.values())/area < .12:
            fail("weak-density", "page", "Card area below 12%; human review must justify sparse notation.")
    return tree, {"status": "blocked" if errors or uncertain else "passed", "errors": errors, "uncertainty": uncertain}


def normalize(raw, bindings=None):
    tree, before = analyze(raw, bindings)
    if tree.get("approvedSnapshot"):
        return (raw if before["status"] == "passed" else None), before
    if before["uncertainty"]:
        return None, before
    candidate = copy.deepcopy(tree)
    for c in candidate.iter("mxCell"):
        role = c.get("fluentRole")
        if not role:
            continue
        s = style(c)
        if role in ("title", "subtitle", "meta", "edge-label", "group-label", "badge", "note", "section"):
            key = {"title":"titlePx", "subtitle":"subtitlePx", "meta":"metaPx", "edge-label":"connectorLabelPx",
                   "group-label":"titlePx", "badge":"badgePx", "note":"metaPx", "section":"metaPx"}[role]
            size = TYPE[key]
            family = TYPE["monospaceFamily"] if role == "meta" else TYPE["family"]
            s.update(fontFamily=family, fontSize=str(size))
            value = re.sub(r"font-size\s*:\s*[\d.]+px", f"font-size:{size}px", c.get("value", ""))
            value = re.sub(r"font-family\s*:[^;\"<>]+", f"font-family:{family}", value)
            c.set("value", value)
        if role == "surface":
            s.update(fillColor=COLORS["surface"], absoluteArcSize="1", arcSize=str(CARDS["radiusPx"]*2), shadow="0")
        if role == "edge-label":
            s.update(fillColor=COLORS["surface"], strokeColor=COLORS["stroke"])
        if role in ("connector", "message"):
            s.update(jumpStyle="arc", rounded="1", strokeWidth=str(SYSTEM["connectors"]["strokeWidthPx"]))
            if s.get("endArrow") in ("block","classic","blockThin","classicThin"):
                s.update(endArrow=SYSTEM["connectors"]["arrowType"],endSize=str(SYSTEM["connectors"]["arrowSizePx"]))
        named_styles=[p for p in c.get("style","").split(";") if p and "=" not in p]
        c.set("style", ";".join(named_styles+[f"{k}={v}" for k,v in sorted(s.items())])+";")
    result = ET.tostring(candidate, encoding="unicode")
    _, after = analyze(result)
    if after["errors"]:
        after["remedy"] = "Regenerate from a reviewed structured source; normalization will not guess reflow or delete semantic text."
        return None, after
    return result, after


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("--bindings", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--json", action="store_true")
    args=parser.parse_args()
    if str(args.source) == "-":
        raw=sys.stdin.read()
    else:
        with args.source.open("r", encoding="utf-8", newline="") as source:
            raw=source.read()
    bindings=json.loads(args.bindings.read_text()) if args.bindings else None
    try:
        if args.output:
            output, report=normalize(raw,bindings)
        else:
            _,report=analyze(raw,bindings)
            output=None
    except (ET.ParseError,ValueError) as error:
        output=None
        report={"status":"blocked","errors":[{"code":"invalid-xml","cell":"document","detail":str(error)}],"uncertainty":[]}
    if args.report:
        args.report.parent.mkdir(parents=True,exist_ok=True)
        args.report.write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    if output is not None:
        serialized=output if output.endswith(("\n","\r")) else output+"\n"
        with args.output.open("w", encoding="utf-8", newline="") as destination:
            destination.write(serialized)
    print(json.dumps(report if args.json else {"status":report["status"],"errors":len(report["errors"]),"uncertainty":len(report["uncertainty"])}))
    return 0 if report["status"]=="passed" else 1


if __name__ == "__main__":
    sys.exit(main())
