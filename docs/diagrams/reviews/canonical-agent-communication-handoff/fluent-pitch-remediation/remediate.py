"""Bounded, review-local authoring; no product, inventory, or documentation writes."""
import argparse
import copy
import hashlib
import importlib.util
import json
import shutil
import subprocess
import sys
from pathlib import Path
from xml.etree import ElementTree as E
from PIL import Image

HERE = Path(__file__).resolve().parent
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("previous_author", HERE.parent / "author-eight.py")
a = importlib.util.module_from_spec(spec)
spec.loader.exec_module(a)
ROOT = a.ROOT
NAMES = list(a.MODELS)
LINEAGE = "fluent-pitch-remediation"
ROWS = {
 "canonical-agent-communication-handoff": [
  [("Input", "Desired outcome"), ("Scope", "Human intent"), ("Gate", "Confirm or revise"), ("Owner", "Coordinator intake")],
  [("State", "Persisted contract"), ("Fields", "Scope / assumptions"), ("Gate", "Human confirmation"), ("Next", "Workflow selection")],
  [("Model", "Subtasks + edges"), ("Owner", "Bounded assignee"), ("Ready", "Dependencies satisfied"), ("Store", "Persisted WorkPlan")],
  [("Binding", "ParentRunId / SubtaskId"), ("Files", "Per-child worktree"), ("Input", "Charter + decisions"), ("Output", "Result to parent")],
  [("Binding", "ParentRunId / SubtaskId"), ("Ready", "Eligible frontier only"), ("Failure", "Blocks dependents"), ("Chat", "No sibling channel")],
  [("Input", "Settled child branches"), ("Action", "Integrate collective work"), ("Gates", "Configured checks"), ("Review", "One human decision")],
 ],
 "canonical-board-lifecycle": [
  [("Entity", "BacklogTask"), ("State", "Backlog"), ("Run", "Not required"), ("Action", "Move to Ready")],
  [("Entity", "BacklogTask"), ("State", "Ready"), ("Blocked", "Dependency metadata"), ("Pickup", "Atomic claim")],
  [("Input", "Coordinator run"), ("Status", "Non-review default"), ("Plan", "Dispatch / assembly"), ("Approval", "Separate pending flag")],
  [("Run", "Failed / Declined"), ("Merge", "MergeFailed"), ("Assembly", "Blocked / Failed"), ("Also", "AssemblyDeclined")],
  [("Run", "AwaitingReview"), ("Plan", "InReview"), ("Assembly", "Review stage"), ("Approve", "Resumes execution")],
  [("Run", "Completed / Merged"), ("Also", "AssembleReady"), ("Plan", "Complete"), ("Assembly", "Done stage")],
 ],
 "canonical-coordinator-journey": [
  [("Input", "Human goal"), ("Artifact", "OutcomeSpec"), ("Gate", "Confirm or revise"), ("Scope", "Explicit assumptions")],
  [("Select", "Workflow choice"), ("Artifact", "WorkPlan DAG"), ("Owners", "Named subtasks"), ("Store", "Persist dependencies")],
  [("Ready", "Satisfied dependencies"), ("Files", "Child-owned worktree"), ("Observe", "Child status / results"), ("Failure", "Blocks dependents")],
  [("Merge", "Reviewed integration"), ("Then", "Collective Scribe"), ("Record", "Promote decisions"), ("Decline", "Skips Scribe")],
  [("Approve", "Proceed to merge"), ("Revise", "Steer / redispatch"), ("Decline", "No Scribe path"), ("Blocked", "Recoverable state")],
  [("Input", "Child branches"), ("Target", "Integration branch"), ("Gates", "Selected checks"), ("Output", "Collective review")],
 ],
 "canonical-durable-event-stream": [
  [("Input", "RunStreamEntry"), ("Identity", "runId + event type"), ("Body", "Structured payload"), ("Ack", "After durable commit")],
  [("Lock", "Per-run advisory lock"), ("Next", "MAX(Sequence) + 1"), ("Write", "Save transaction"), ("Commit", "Before acknowledgement")],
  [("Table", "RunEvents"), ("Key", "RunId + Sequence"), ("Order", "Ascending sequence"), ("Reuse", "Same type / payload")],
  [("Client", "Web or MCP"), ("Resume", "Last delivered cursor"), ("Replica", "No sticky requirement"), ("History", "Durable ordered events")],
  [("Frame", "id + event + data"), ("Cursor", "Last-Event-ID"), ("Delivery", "Yield ordered events"), ("Close", "After batch is drained")],
  [("Query", "Sequence > cursor"), ("Idle", "Poll after 250 ms"), ("State", "Shared durable table"), ("Blocked", "Retryable: keep open")],
 ],
 "email-components": [
  [("Kind", "Executable project"), ("Uses", "AgentRuntime"), ("Data", "Api.Data"), ("Catalog", "Squad")],
  [("Kind", "Executable project"), ("Uses", "AgentRuntime"), ("Also", "Domain"), ("Role", "Sandbox turn host")],
  [("Kind", "Executable project"), ("Calls", "HTTP API client"), ("Graph", "No selected refs"), ("Data", "Not a database layer")],
  [("Uses", "AgentTools"), ("Contracts", "Domain"), ("Exec", "SandboxExec"), ("Files", "SandboxFs")],
  [("Contracts", "Domain"), ("Exec", "SandboxExec"), ("Files", "SandboxFs"), ("Role", "Tool adapters")],
  [("Kind", "Shared contracts"), ("Used by", "AgentRuntime"), ("Also", "AgentTools / AgentHost"), ("Arrow", "Toward dependency")],
 ],
 "email-architecture": [
  [("Services", "API + worker"), ("Data", "Postgres persistence"), ("Files", "CSI-mounted storage"), ("Control", "Configure + A2A")],
  [("Surface", "Authenticated tools"), ("Credential", "Validated broker token"), ("Downstream", "HTTP API"), ("Storage", "No database connector")],
  [("Host", "AgentHost workload"), ("Transport", "Authenticated A2A"), ("Credential", "Constrained repo access"), ("Scope", "Bound to execution")],
  [("Role", "User authentication"), ("Methods", "Sign-in / sessions"), ("Access", "Role authorization"), ("Not", "Repository authority")],
  [("Role", "Repository capability"), ("Grant", "Purpose-bound access"), ("Delivery", "Constrained credentials"), ("Not", "Product sign-in")],
  [("Role", "Durable persistence"), ("Writers", "API + worker"), ("Stores", "Runs / events / projects"), ("Separate", "CSI-mounted files")],
 ],
}

PITCH = {
 "canonical-agent-communication-handoff": ("Coordinator", "Own the confirmed plan and dependency frontier.", "Persisted OutcomeSpec / WorkPlan", "CONTROL", "component", "Bounded child run", "Execute one eligible subtask; return results to the parent.", "Per-child worktree / ParentRunId", "EXECUTION", "component", "dispatch subtask"),
 "canonical-board-lifecycle": ("Persisted records", "Tasks, runs and assembly state remain authoritative.", "BacklogTask / Run / WorkPlan", "STATE", "cylinder", "Board projection", "Map state to columns; blocked Ready remains metadata.", "Default buckets or declared stages", "READ MODEL", "mxgraph.flowchart.process", "project state"),
 "canonical-coordinator-journey": ("Plan & execute", "Confirm intent, then dispatch dependency-ready children.", "OutcomeSpec / WorkPlan / child worktrees", "COORDINATOR", "mxgraph.flowchart.process", "Review & finish", "Integrate collectively; approved merge proceeds to Scribe.", "Decline skips Scribe / no PR promise", "COLLECTIVE", "mxgraph.flowchart.process", "assemble settled work"),
 "canonical-durable-event-stream": ("Durable event log", "Commit ordered RunEvents before acknowledging append.", "PostgreSQL / RunId + Sequence", "PERSISTENCE", "cylinder", "Cursor subscriber", "Read after the cursor on any API replica; emit SSE.", "EF durable query / idle poll 250 ms", "DELIVERY", "component", "ordered durable batch"),
 "email-components": ("Agentweaver.Api", "Control-plane executable references the runtime package.", ".NET executable / ProjectReference", "EXECUTABLE", "component", "AgentRuntime", "Shared agent execution depends on tools and domain contracts.", ".NET package / selected dependency", "PACKAGE", "component", "ProjectReference"),
 "email-architecture": ("Control plane", "API and workers orchestrate constrained execution.", "AKS workloads / Postgres + CSI", "CONTROL", "mxgraph.kubernetes.pod", "AgentHost", "Run an authenticated agent turn with purpose-bound access.", "Sandbox pod / repository credentials exist", "EXECUTION", "mxgraph.kubernetes.pod", "configure / A2A"),
 "canonical-durable-event-stream-sequence": ("Producer", "Submit a structured run event.", "Run execution / replica A", "CALLER", "component", "EF / Postgres", "Serialize the append and commit durable history.", "Per-run lock / assigned sequence", "DURABLE", "cylinder", "append event"),
 "email-coordinator-workflow": ("Human", "Submit the desired outcome and confirm scope.", "Goal / assumptions / confirmation", "OWNER", "umlActor", "Coordinator", "Persist intent before dispatching bounded work.", "OutcomeSpec / WorkPlan", "CONTROL", "component", "submit goal"),
}

def folder(name):
    p = a.REVIEWS / name / LINEAGE
    p.mkdir(exist_ok=True)
    return p

def text(d, id, value, x, y, w, h, size=11, color="#635c57", bold=False):
    return d.text(id, value, x, y, w, h, size, color, bold)

def pitch(name):
    model = a.MODELS[name]
    d = a.Drawing(model)
    d.graph.set("pageScale", "1")
    v = PITCH[name]
    seq = name in a.SEQUENCES
    d.vertex("scope", "RESPONSIBILITY VIEW  /  " + model["identity"], 28, 116, 771, 438,
             "rounded=1;arcSize=16;absoluteArcSize=1;fillColor=#f8f4f1;strokeColor=#e2ddd9;fontFamily=Segoe UI;fontSize=12;fontColor=#3f3935;align=left;verticalAlign=top;spacingLeft=16;spacingTop=12;")
    d.vertex("heading", f'<div style="font-size:27px;font-weight:600">{model["title"]}</div><div style="font-size:15px;color:#635c57;margin-top:10px">{model["subtitle"]}</div>',
             28, 22, 771, 82, "text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;align=left;fontFamily=Segoe UI;fontColor=#272320;")
    for i in range(2):
        title, sub, meta, badge, shape = v[i*5:i*5+5]
        x = 48+i*395
        bg, fg = a.PALETTE["lavender" if i == 0 else "teal"]
        h = 177 if seq else 264
        y = 163 if seq else 190
        label = f'<div style="font-size:{20 if seq else 22}px;font-weight:600">{title}</div><div style="font-size:{14 if seq else 16}px;color:#635c57;margin-top:{8 if seq else 14}px">{sub}</div><div style="font-family:Cascadia Code;font-size:{10 if seq else 12}px;color:#746d68;margin-top:{8 if seq else 16}px">{meta}</div>'
        d.vertex(f"role{i}", label, x, y, 330, h, "rounded=1;arcSize=16;absoluteArcSize=1;html=1;whiteSpace=wrap;fillColor=#fdfbf8;strokeColor=#ece7e3;shadow=1;align=left;spacingLeft=55;spacingRight=14;spacingTop=18;fontFamily=Segoe UI;fontColor=#272320;")
        d.vertex(f"accent{i}", "", x, y+12, 5, h-24, f"fillColor={fg};strokeColor=none;")
        d.vertex(f"native{i}", "", x+14, y+64, 28, 34, f"shape={shape};fillColor={bg};strokeColor={fg};")
        d.vertex(f"badge{i}", badge, x+55, y+h-29, 235, 19, f"rounded=1;arcSize=100;fillColor={bg};strokeColor=none;fontFamily=Segoe UI;fontSize=11;fontStyle=1;fontColor={fg};")
        if seq:
            d.vertex(f"lifeline{i}", "", x+164, y+h, 2, 184, "shape=umlLifeline;participant=none;size=0;fillColor=none;strokeColor=#746d68;dashed=1;")
    if seq:
        for i in range(2):
            for j,y in enumerate((394,478)):
                d.vertex(f"activation{i}-{j}","",209+i*395,y-9,8,18,"fillColor=#fdfbf8;strokeColor=#746d68;")
        d.edge("request", "activation0-0", "activation1-0", v[10], style="exitX=1;exitY=0.5;entryX=0;entryY=0.5;jettySize=0;")
        d.edge("response", "activation1-1", "activation0-1", "committed sequence" if "durable" in name else "confirm or revise?", style="exitX=0;exitY=0.5;entryX=1;entryY=0.5;jettySize=0;dashed=1;strokeColor=#d39300;endArrow=open;")
    else:
        style = "exitX=1;exitY=0.5;entryX=0;entryY=0.5;"
        if name == "email-components":
            style += "dashed=1;endArrow=open;endFill=0;"
        label = {"email-components":"references","canonical-durable-event-stream":"batch","canonical-coordinator-journey":"assemble"}.get(name,v[10].replace(" ", "<br>", 1))
        d.edge("handoff", "role0", "role1", label, style=style+"fontSize=10;")
        if name == "canonical-agent-communication-handoff":
            d.edge("result","role1","role0","results",points=((608,505),(213,505)),
                   style="exitX=0.5;exitY=1;entryX=0.5;entryY=1;dashed=1;strokeColor=#d39300;endArrow=open;")
    out = folder(name) / f"{name}-pitch.drawio"
    d.save(out)
    return out

def full(name):
    d = E.parse(a.REVIEWS/name/f"{name}-pass-04.drawio")
    graph = d.find(".//mxGraphModel")
    graph.set("pageScale", "1")
    root = graph.find("root")
    cells = {c.get("id"):c for c in root}
    model = a.MODELS[name]
    helper = a.Drawing(model)
    helper.tree, helper.graph, helper.root = d, graph, root
    for id,c in cells.items():
        if id.endswith("-icon"):
            g=c.find("mxGeometry")
            g.set("y","8");g.set("height","23");g.set("width","24")
    if name not in a.SEQUENCES:
        for n, rows in zip(model["nodes"], ROWS[name]):
            id = n["id"]
            for c in list(root):
                if c.get("id", "").startswith(id+"-") and any(s in c.get("id") for s in ("-fact", "-bullet")):
                    root.remove(c)
            for suffix, y, h in (("title",8,27),("sub",34,18),("meta",51,12),("pill",123,13)):
                c = cells[id+"-"+suffix]
                g = c.find("mxGeometry")
                g.set("y",str(y));g.set("height",str(h))
                if suffix == "meta":
                    c.set("style",c.get("style")+"fontSize=8;fontFamily=Cascadia Code;")
            for i,(key,value) in enumerate(rows):
                y=67+i*13
                helper.vertex(f"{id}-rule{i}","",12,y,193,1,"fillColor=#ece7e3;strokeColor=none;",id)
                helper.text(f"{id}-key{i}",key,12,y+1,51,11,8,"#746d68",True,parent=id)
                helper.text(f"{id}-value{i}",value,65,y+1,140,11,8.5,"#3f3935",parent=id)
    else:
        # Participant responsibilities replace empty space with source-backed context,
        # leaving message order, activation attachments and routing intact.
        facts = (
            [("Input","Structured event"),("Ack","After commit"),("Fence","Local late delta")],
            [("Write","Advisory run lock"),("Assign","MAX + 1"),("Reuse","Matching payload")],
            [("Key","RunId / Sequence"),("Read","Ordered history"),("Scope","Cross-replica")],
            [("Resume","Last-Event-ID"),("Emit","id / event / data"),("Close","Drain full batch")],
            [("Keep","Delivered cursor"),("Retry","Any API replica"),("Blocked","Retryable stays open")],
        ) if "durable" in name else (
            [("Own","Goal and scope"),("Decide","Collective review"),("Approve","Not yet Done")],
            [("Persist","OutcomeSpec"),("Plan","Dependency DAG"),("Failure","Blocks dependents")],
            [("Own","Child worktree"),("Return","Result to parent"),("Gate","Trimmed pipeline")],
            [("Build","Integration branch"),("Gate","Configured checks"),("Recover","Blocked may resume")],
            [("Merge","Reviewed work"),("Record","Learn / decisions"),("Decline","Skip Scribe")],
        )
        for n, rows in zip(model["participants"], facts):
            id = n["id"]
            for suffix,y,h in (("title",6,26),("sub",32,13),("meta",45,11),("pill",99,13)):
                g = cells[id+"-"+suffix].find("mxGeometry")
                g.set("y",str(y));g.set("height",str(h))
            for i,(key,value) in enumerate(rows):
                y=58+i*13
                helper.vertex(f"{id}-rule{i}","",12,y,116,1,"fillColor=#ece7e3;strokeColor=none;",id)
                helper.text(f"{id}-key{i}",key,12,y+2,35,11,8,"#746d68",True,parent=id)
                helper.text(f"{id}-value{i}",value,48,y+2,82,11,8,"#3f3935",parent=id)
        # Native UML lifelines, rather than lines merely described as UML.
        for i,n in enumerate(model["participants"]):
            old = cells["life"+str(i)]
            root.remove(old)
            line=helper.vertex("life"+str(i),"",97+i*158,235,2,260,
                              "shape=umlLifeline;participant=none;size=0;fillColor=none;strokeColor=#b7afa8;dashed=1;")
            root.remove(line);root.insert(2,line)
        returns=(3,4,7) if "durable" in name else (2,5)
        for number in returns:
            c=cells["message"+str(number)]
            c.set("style",c.get("style")+"dashed=1;strokeColor=#d39300;endArrow=open;endFill=0;")
        helper.text("sequence-reading-order","TIME ↓   /   Message numbers define the order",43,238,390,11,8,"#746d68",True)
    out=folder(name)/f"{name}-pass-01.drawio"
    helper.save(out)
    return out

def export(source):
    a.export(source)
    print_view(source)

def print_view(source):
    image=Image.open(source.with_suffix(".png"))
    image=image.crop((16,16,image.width-16,image.height-16))
    image=image.resize((round(image.width/2),round(image.height/2)),Image.Resampling.LANCZOS)
    assert image.width<=827 and image.height<=583,(source,image.size)
    page=Image.new("RGB",(827,583),"#efeae7")
    page.paste(image,((827-image.width)//2,(583-image.height)//2))
    page.save(source.with_name(source.stem+"-print.png"))

def main():
    p=argparse.ArgumentParser()
    p.add_argument("action",choices=["pitch","full","next","export"])
    p.add_argument("--name",action="append")
    p.add_argument("--number",type=int)
    args=p.parse_args()
    for name in args.name or NAMES:
        if args.action=="pitch": path=pitch(name)
        elif args.action=="full": path=full(name)
        elif args.action=="next":
            path=folder(name)/f"{name}-pass-{args.number:02}.drawio"
            assert not path.exists()
            shutil.copyfile(folder(name)/f"{name}-pass-{args.number-1:02}.drawio",path)
            if args.number==2:
                tree=E.parse(path)
                cells={c.get("id"):c for c in tree.findall(".//mxCell")}
                if name=="canonical-coordinator-journey":
                    for id,label in (("e1","confirm"),("e5","approve")):
                        cells[id].set("value",label)
                        cells[id].set("style",cells[id].get("style")+"fontSize=9;")
                if name=="email-coordinator-workflow":
                    for id in ("context8","context-accent8","context-badge8","context-detail8"):
                        g=cells[id].find("mxGeometry")
                        g.set("y",str(float(g.get("y"))+4))
                E.indent(tree,space="  ")
                tree.write(path,encoding="utf-8",xml_declaration=True)
        else:
            path=folder(name)/f"{name}-pass-{args.number:02}.drawio"
        export(path)

if __name__=="__main__":
    main()
