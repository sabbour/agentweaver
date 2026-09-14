import importlib.util
from pathlib import Path
from xml.etree import ElementTree as E

HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("author",HERE/"author-eight.py")
a=importlib.util.module_from_spec(spec)
spec.loader.exec_module(a)
for name in a.MODELS:
    folder=a.REVIEWS/name
    source=folder/f"{name}-pass-03.drawio"
    assert not source.exists()
    tree=E.parse(folder/f"{name}-pass-02.drawio")
    if name=="email-architecture":
        c=next(c for c in tree.iter("mxCell") if c.get("id")=="e1")
        c.set("value","HTTP")
        c.set("style",c.get("style")+"exitX=0;exitY=0.2;entryX=1;entryY=0.2;")
    E.indent(tree,space="  ")
    tree.write(source,encoding="utf-8",xml_declaration=True)
    a.export(source)
