"""Extract explicit card bindings, containing groups and complete visible copy."""
import argparse
from html import unescape
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def plain(value):
    return re.sub(r"\s+"," ",unescape(re.sub(r"<[^>]*>"," ",value or ""))).strip()


def recover(path,nodes):
    tree=ET.parse(path)
    cells={c.get("id"):c for c in tree.iter("mxCell")}
    cache={}
    def box(identifier):
        if identifier in cache:
            return cache[identifier]
        c=cells[identifier];g=c.find("mxGeometry")
        if g is None:return (0,0,0,0)
        x,y,w,h=[float(g.get(k,0)) for k in ("x","y","width","height")]
        parent=c.get("parent")
        if parent in cells and parent not in ("0","1"):
            px,py,_,_=box(parent);x+=px;y+=py
        cache[identifier]=(x,y,w,h)
        return cache[identifier]
    def norm(value):
        return re.sub(r"(-card|-node)$","",re.sub(r"^(node-|n-)","",value)).lower()
    def child_value(identifier,suffixes):
        return next((plain(cells[identifier+suffix].get("value")) for suffix in suffixes if identifier+suffix in cells),"")
    if not nodes:
        nodes=[{"id":i,"label":child_value(i,["-title"]), "subLabel":child_value(i,["-subtitle","-sub"]),
                "meta":child_value(i,["-meta","-metadata"])} for i,c in cells.items()
               if i+"-title" in cells and i+"-accent" in cells and c.get("vertex")=="1"]
    bindings={}
    for node in nodes:
        matches=[i for i,c in cells.items() if norm(i)==norm(node["id"]) and c.get("vertex")=="1"]
        if not matches:
            matches=[i for i,c in cells.items() if i+"-title" in cells and
                     plain(cells[i+"-title"].get("value")).lower()==node["label"].lower()]
        if not matches:
            matches=[i for i,c in cells.items() if i+"-title" in cells and re.match(
                rf"^(?:id\s*[:=]\s*)?{re.escape(node['id'])}(?:$|[\s|:\u2022])",
                child_value(i,["-meta","-metadata","-code"]),re.I)]
        if len(matches)==1:bindings[node["id"]]=matches[0]
    native_shapes={};native_styles={};tones={}
    tone_by_color={"#3f3682":"lavender","#00666d":"teal","#0e700e":"green","#835b00":"marigold","#635c57":"neutral"}
    for node,identifier in bindings.items():
        icon=cells.get(identifier+"-icon")
        if icon is not None:
            match=re.search(r"(?:^|;)shape=([^;]+)",icon.get("style",""))
            if match:
                native_shapes[node]=match.group(1)
                native_styles[node]=icon.get("style","")
        accent=cells.get(identifier+"-accent")
        if accent is not None:
            match=re.search(r"(?:^|;)fillColor=([^;]+)",accent.get("style",""))
            if match and match.group(1).lower() in tone_by_color:tones[node]=tone_by_color[match.group(1).lower()]
    groups=[]
    for identifier,c in cells.items():
        if identifier in bindings.values() or c.get("vertex")!="1":continue
        x,y,w,h=box(identifier)
        members=[n for n,i in bindings.items() if (lambda b:b[0]>=x-.1 and b[1]>=y-.1 and b[0]+b[2]<=x+w+.1 and b[1]+b[3]<=y+h+.1)(box(i))]
        if not members:continue
        candidates=[identifier+"-title",identifier+"-label",identifier.replace("group-","group-title-"),
                    re.sub(r"^boundary","group-title",identifier),
                    re.sub(r"^group(?=\d+$)","group-title",identifier)]
        title=plain(c.get("value")) or next((plain(cells[i].get("value")) for i in candidates if i in cells and plain(cells[i].get("value"))),"")
        if not title or len(title)>110:continue
        groups.append({"id":identifier.removeprefix("group-"),"source_cell":identifier,"label":title,"members":members,"bounds":[x,y,w,h]})
    for group in groups:
        parents=[g for g in groups if g!=group and set(group["members"])<set(g["members"])]
        if parents:group["parent"]=min(parents,key=lambda g:len(g["members"]))["id"]
    membership={}
    for node in nodes:
        candidates=[g for g in groups if node["id"] in g["members"]]
        if candidates:membership[node["id"]]=min(candidates,key=lambda g:len(g["members"]))["id"]
    visible=[{"cell":i,"text":plain(c.get("value"))} for i,c in cells.items()
             if plain(c.get("value")) and "native-libraries:" not in plain(c.get("value"))]
    reverse={value:key for key,value in bindings.items()}
    edges=[];edge_geometry=[];unbound=[]
    junctions={i for i,c in cells.items() if c.get("vertex")=="1" and
               "ellipse" in c.get("style","") and 0<box(i)[2]<=20 and 0<box(i)[3]<=20}
    raw_edges={i:c for i,c in cells.items() if c.get("edge")=="1"}
    paths=[]
    def follow(identifier,chain):
        c=raw_edges[identifier];target=c.get("target")
        if identifier in chain:raise ValueError("Cyclic junction bus requires explicit semantic review")
        chain=chain+[identifier]
        if target in reverse:
            paths.append(chain)
        elif target in junctions:
            for next_id,next_cell in raw_edges.items():
                if next_cell.get("source")==target:follow(next_id,chain)
    for identifier,c in raw_edges.items():
        if c.get("source") in reverse:follow(identifier,[])
    used={identifier for chain in paths for identifier in chain}
    unbound=[i for i in raw_edges if i not in used]
    for chain in paths:
        identifier=chain[0];c=raw_edges[identifier];last=raw_edges[chain[-1]]
        source,target=c.get("source"),last.get("target")
        s=c.get("style","")
        labels=[plain(raw_edges[i].get("value")) or child_value(i,["-label"]) for i in chain]
        edges.append({"from":reverse[source],"to":reverse[target],"label":" / ".join(dict.fromkeys(t for t in labels if t)),
                      "source_edges":chain,
                      "loopback":"#d39300" in s,"bidirectional":bool(re.search(r"startArrow=(?!none)[^;]+",s))})
        props=dict(part.split("=",1) for part in s.split(";") if "=" in part)
        final_props=dict(part.split("=",1) for part in last.get("style","").split(";") if "=" in part)
        points=[]
        for index,edge_id in enumerate(chain):
            edge=raw_edges[edge_id]
            px,py,_,_=box(edge.get("parent")) if edge.get("parent") in cells else (0,0,0,0)
            points.extend({"x":float(p.get("x"))+px,"y":float(p.get("y"))+py} for p in edge.findall("mxGeometry/Array/mxPoint"))
            if index<len(chain)-1:
                x,y,w,h=box(edge.get("target"));points.append({"x":x+w/2,"y":y+h/2})
        edge_geometry.append({"id":identifier,"from":reverse[source],"to":reverse[target],
            "exitX":float(props.get("exitX",.5)),"exitY":float(props.get("exitY",1)),
            "entryX":float(final_props.get("entryX",.5)),"entryY":float(final_props.get("entryY",0)),
            "points":points,"source_edges":chain})
    for node in nodes:
        if node["id"] in native_shapes:node["shape"]=native_shapes[node["id"]]
    return {"bindings":bindings,"groups":groups,"membership":membership,"visible_copy":visible,
            "native_shapes":native_shapes,"native_styles":native_styles,"tones":tones,
            "node_boxes":{node:box(identifier) for node,identifier in bindings.items()},
            "edge_geometry":edge_geometry,
            "page":{"width":float(tree.find("diagram/mxGraphModel").get("pageWidth")),
                    "height":float(tree.find("diagram/mxGraphModel").get("pageHeight"))},
            "model":{"nodes":nodes,"edges":edges},"unbound_edges":unbound,
            "junction_expansion":{"junctions":sorted(junctions),"paths":[p for p in paths if len(p)>1]}}


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("source",type=Path)
    args=parser.parse_args()
    nodes=json.loads(__import__("sys").stdin.read())
    print(json.dumps(recover(args.source,nodes)))
