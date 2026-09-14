import importlib.util
import sys
from pathlib import Path
from xml.etree import ElementTree as E

HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("author",HERE/"author-eight.py")
a=importlib.util.module_from_spec(spec)
spec.loader.exec_module(a)

def reroute(cell,points,ports):
    cell.set("style",cell.get("style")+ports)
    geo=cell.find("mxGeometry")
    old=geo.find("Array")
    if old is not None:
        geo.remove(old)
    if points:
        arr=E.SubElement(geo,"Array",**{"as":"points"})
        for x,y in points:
            E.SubElement(arr,"mxPoint",x=str(x),y=str(y))

for name,model in a.MODELS.items():
    if len(sys.argv)>1 and name not in sys.argv[1:]:
        continue
    folder=a.REVIEWS/name
    tree=E.parse(folder/f"{name}-pass-01.drawio")
    cells={c.get("id"):c for c in tree.iter("mxCell")}
    for id,c in cells.items():
        if id.endswith("-icon"):
            st=c.get("style").replace("size=6;","")
            if "shape=cylinder;" in st:
                st+="size=0.15;"
            c.set("style",st)
    short={
        "canonical-agent-communication-handoff":"Children and assembly",
        "canonical-board-lifecycle":"Review / terminal outcomes",
        "email-components":"Shared packages · selected references",
        "email-architecture":"External services",
    }
    if name in short:
        cells["group-title1"].set("value",short[name])
    if name=="canonical-agent-communication-handoff":
        reroute(cells["e3"],[(812,199.65),(812,310),(268,310),(268,386.7)],"exitX=1;exitY=0.35;entryX=1;entryY=0.3;")
        reroute(cells["e4"],[(800,248.3),(800,299),(536,299),(536,386.7)],"exitX=1;exitY=0.7;entryX=1;entryY=0.3;")
        reroute(cells["e5"],[(148.5,494),(678.5,494)],"exitX=0.5;exitY=1;entryX=0.5;entryY=1;")
    elif name=="canonical-board-lifecycle":
        cells["done-sub"].set("style",cells["done-sub"].get("style")+"fontSize=10;")
        reroute(cells["e3"],[(812,206.6),(812,310),(536,310),(536,386.7)],"exitX=1;exitY=0.4;entryX=1;entryY=0.3;")
        reroute(cells["e5"],[(800,248.3),(800,299),(268,299),(268,386.7)],"exitX=1;exitY=0.7;entryX=1;entryY=0.3;")
    elif name=="email-components":
        cells["group-title1"].find("mxGeometry").set("width","200")
        cells["group-title1"].set("value","Shared packages")
        reroute(cells["e1"],[(19,234.4),(19,414.5)],"exitX=0;exitY=0.6;entryX=0;entryY=0.5;")
        reroute(cells["e2"],[(413.5,304),(268,304),(268,386.7)],"exitX=0.5;exitY=1;entryX=1;entryY=0.3;")
    elif name=="email-architecture":
        cells["e2"].find("mxGeometry").set("x","-0.35")
        cells["e5"].find("mxGeometry").set("x","0.35")
    path=folder/f"{name}-pass-02.drawio"
    assert not path.exists()
    E.indent(tree,space="  ")
    tree.write(path,encoding="utf-8",xml_declaration=True)
    a.export(path)
