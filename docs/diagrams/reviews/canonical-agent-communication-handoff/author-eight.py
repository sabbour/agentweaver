"""Review-local authoring helper for the eight exclusively assigned shared assets."""
import argparse
import copy
import json
import subprocess
import sys
from pathlib import Path
from xml.etree import ElementTree as E

ROOT = Path(__file__).resolve().parents[4]
REVIEWS = ROOT / "docs" / "diagrams" / "reviews"
CLI = Path(r"C:\Users\asabbour\.copilot\session-state\f6a87a42-fc18-4c23-a5f5-3e1c5c41d367\files\drawio-cli\app\draw.io.exe")
PALETTE = {
    "lavender": ("#d2ccf8", "#3f3682"),
    "teal": ("#a6e9ed", "#00666d"),
    "green": ("#9fd89f", "#0e700e"),
    "marigold": ("#f9e2ae", "#835b00"),
    "neutral": ("#e7e1dc", "#635c57"),
}
EVIDENCE = {
    "handoff": "apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs; CoordinatorOrchestratorExecutor.cs; docs/deep-dive/agent-communication.md:126-200",
    "journey": "apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs; CoordinatorWorkflowFactory.cs:14-27,81-96; packages/Agentweaver.Squad/Workflows",
    "board": "apps/Agentweaver.Api/Runs/BoardProjectionService.cs:65-145; apps/Agentweaver.Api/Runs/WorkflowStageProjector.cs:21-80; packages/Agentweaver.Domain/BacklogTaskState.cs; apps/web/src/api/board.ts:20-44",
    "events": "apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:15-36,66-87,145-190,220-320; apps/Agentweaver.Api/Endpoints/RunEndpoints.cs",
    "deployment": "k8s/base/api-deployment.yaml:53-76,337-349; k8s/base/worker-deployment.yaml:54-76,146-170; k8s/base/mcp-deployment.yaml:21; apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs:10-36; apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs",
    "components": "apps/Agentweaver.Api/Agentweaver.Api.csproj; apps/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj; packages/Agentweaver.AgentRuntime/Agentweaver.AgentRuntime.csproj; packages/Agentweaver.AgentTools/Agentweaver.AgentTools.csproj",
}

def card(id, title, sub, meta, pill, tone="lavender", shape="hexagon", facts=()):
    return dict(id=id, title=title, sub=sub, meta=meta, pill=pill, tone=tone, shape=shape, facts=list(facts))

MODELS = {
    "canonical-agent-communication-handoff": dict(
        title="Handoffs, not peer chat", subtitle="The Coordinator owns the dependency frontier and assembles child results.",
        identity="DAG / CONTROL FLOW", disposition="retain", evidence="handoff",
        groups=("Intent → execution contract", "Isolated work → collective assembly"),
        nodes=[
            card("goal","Human goal","Define the desired outcome","intent input","REQUEST","teal","umlActor",("Outcome, scope, assumptions","Coordinator drafts the contract")),
            card("spec","OutcomeSpec","Confirm before dispatch","confirmation gate","CONTRACT","lavender","hexagon",("Human confirms or revises","Persisted intent, not execution")),
            card("plan","WorkPlan DAG","Subtasks + dependencies","eligible frontier","PLAN","lavender","hexagon",("One owner per bounded subtask","Only satisfied dependencies run")),
            card("a","Child run A","One assigned subtask","isolated worktree","EXECUTION","green","hexagon",("Active decisions + charter","Result returned to Coordinator")),
            card("b","Child run B","Another eligible subtask","isolated worktree","EXECUTION","green","hexagon",("Parallel only when eligible","No direct child-to-child chat")),
            card("assembly","Collective assembly","Integrate settled work","one reviewed integration","ASSEMBLY","teal","hexagon",("Child results flow upward","Failed / RAI child blocks dependents")),
        ],
        edges=[("goal","spec","draft"),("spec","plan","confirm"),("plan","a","dispatch A"),("plan","b","dispatch B"),("a","assembly","result A"),("b","assembly","result B")],
        notes=("DEPENDENCIES ARE CONTROL","A dependency edge is scheduling, not a conversation channel.","A2A transports one agent turn between worker and sandbox; it is not peer chat."),
    ),
    "canonical-board-lifecycle": dict(
        title="The board is a projection", subtitle="Columns reflect persisted task and run state—not a separate workflow engine.",
        identity="LIFECYCLE / STATE PROJECTION", disposition="redesign", evidence="board",
        groups=("Before and during execution", "Review and terminal outcomes"),
        nodes=[
            card("backlog","Backlog","Captured task; not queued","task: backlog","CAPTURE","neutral","mxgraph.flowchart.process",("No run is required yet","Move to Ready to queue work")),
            card("ready","Ready","Queued task","task: ready","QUEUE","teal","mxgraph.flowchart.process",("Unresolved dependencies stay here","Blocked is a flag, not a column")),
            card("progress","Active","Work is in progress","default non-review bucket","EXECUTE","green","mxgraph.flowchart.process",("Claimed tasks link to their run","Pending approval is a separate flag")),
            card("failed","Problems","Failed / declined / merge failed","or assembly blocked / failed","ATTENTION","marigold","mxgraph.flowchart.process",("Blocked assembly is recoverable","Not every problem is terminal")),
            card("review","Human Review","AwaitingReview / InReview","or assembly stage: Review","GATE","lavender","rhombus",("Approval resumes the workflow","Approval alone is not Done")),
            card("done","Done","Completed / Merged / AssembleReady","or plan Complete / stage Done","RESULT","green","mxgraph.flowchart.terminator",("Persisted-state mapping","Not merely a clicked approval")),
        ],
        edges=[("backlog","ready","queue"),("ready","progress","claim + start"),("progress","review","await review"),("review","done","finish"),("progress","failed","problem")],
        notes=("DEFAULT BUCKETS · NOT A NEW STATE MACHINE","Configured workflow stages may replace the default run columns. Arrows summarize typical changes, not every path.","The board polls persisted state. Ready tasks with unmet dependencies stay Ready; blocked is a flag."),
    ),
    "canonical-coordinator-journey": dict(
        title="One goal, one collective review", subtitle="Confirm intent, dispatch bounded work, then integrate and review the whole result.",
        identity="COORDINATOR JOURNEY", disposition="redesign", evidence="journey",
        groups=("Plan and execute", "Integrate, review, finish"),
        nodes=[
            card("intent","Confirm intent","Draft the OutcomeSpec","human confirmation","01 · INTENT","teal","hexagon",("Scope and assumptions are explicit","Revision reopens the intent gate")),
            card("plan","Plan the work","Persist a WorkPlan DAG","subtasks + dependencies","02 · PLAN","lavender","hexagon",("Outcome-complete decomposition","Bounded work with named owners")),
            card("dispatch","Dispatch children","Run the eligible frontier","per-child worktrees","03 · EXECUTE","green","hexagon",("Observe child status and results","Failure / RAI blocks dependents")),
            card("finish","Merge + Scribe","Approved integration path","MergeWorktree → Scribe","06 · FINISH","green","hexagon",("Decline skips Scribe","No automatic PR claim here")),
            card("review","Collective review","One human decision","approve / revise / decline","05 · REVIEW","lavender","rhombus",("Changes can redispatch work","Blocked is recoverable, not terminal")),
            card("integrate","Integrate + gates","Assemble child branches","configured checks / review","04 · ASSEMBLE","teal","hexagon",("Collective—not per-child delivery","Merge failure may still run Scribe")),
        ],
        edges=[("intent","plan","confirmed"),("plan","dispatch","dispatch"),("dispatch","integrate","settled work"),("integrate","review","request review"),("review","finish","approved")],
        notes=("DO NOT CONFUSE ASSEMBLY WITH PUBLICATION","The collective workflow reaches MergeWorktree and Scribe; this graphic does not promise PR creation.","A blocked assembly can be recovered. Review approval does not itself mark the run complete."),
    ),
    "canonical-durable-event-stream": dict(
        title="Postgres is the event relay", subtitle="Any API replica can serve a cursor over durable RunEvents—no sticky session required.",
        identity="DURABLE EVENT ARCHITECTURE", disposition="redesign", evidence="events",
        groups=("Write path · replica A", "Read path · replica B"),
        nodes=[
            card("producer","Run producer","Append a structured event","runId + type + payload","PRODUCER","green","hexagon",("Coordinator or run execution","Acknowledgement follows commit")),
            card("append","EF event stream","Serialize writes per run","pg_advisory_xact_lock","APPEND","teal","component",("Allocate MAX(Sequence) + 1","Save and commit transaction")),
            card("store","RunEvents","Shared PostgreSQL table","(RunId, Sequence)","DURABLE","teal","cylinder3",("Cross-replica ordered history","Explicit duplicates must match payload")),
            card("client","Web / MCP watcher","Consume ordered events","last delivered cursor","CONSUMER","lavender","umlActor",("Reconnect from the cursor","No local channel dependency")),
            card("sse","SSE endpoint","Emit id + event + data","ordered response frames","STREAM","teal","component",("Cursor advances after delivery","Drain batch before terminal close")),
            card("reader","EF subscriber","Read Sequence > cursor","idle poll: 250 ms","SUBSCRIBE","teal","component",("Query the shared durable table","Retryable assembly_blocked stays open")),
        ],
        edges=[("producer","append","append"),("append","store","commit"),("store","reader","ordered batch"),("reader","sse","yield"),("sse","client","SSE frames")],
        notes=("POSTGRES LANE ONLY","SQLite register-channel / replay / tail is a separate implementation—not this architecture.","Late-delta suppression is process-local; do not read it as a database-wide terminal fence."),
    ),
    "email-components": dict(
        title="Components and project references", subtitle="Arrows mean compile-time ProjectReference—not HTTP calls or deployment routing.",
        identity="UML COMPONENT / DEPENDENCY", disposition="retain", evidence="components",
        groups=("Executable projects", "Shared package dependencies · selected edges"),
        nodes=[
            card("api","Agentweaver.Api","Control plane + workers","apps/Agentweaver.Api","EXECUTABLE","lavender","component",("References AgentRuntime","References Api.Data and Squad")),
            card("host","Agentweaver.AgentHost","Sandbox turn host","apps/Agentweaver.AgentHost","EXECUTABLE","green","component",("References AgentRuntime","Also directly references Domain")),
            card("mcp","Agentweaver.Mcp","Remote tool surface","apps/Agentweaver.Mcp","EXECUTABLE","teal","component",("No project-reference arrows here","API client—not a database layer")),
            card("runtime","AgentRuntime","Agent workflow execution","packages/Agentweaver.AgentRuntime","PACKAGE","lavender","component",("References AgentTools","Also Domain / SandboxExec / SandboxFs")),
            card("tools","AgentTools","Agent-facing tool adapters","packages/Agentweaver.AgentTools","PACKAGE","teal","component",("References Domain","References SandboxExec / SandboxFs")),
            card("domain","Domain","Shared domain contracts","packages/Agentweaver.Domain","PACKAGE","neutral","component",("Referenced by runtime and tools","Dependency direction is toward Domain")),
        ],
        edges=[("api","runtime","references"),("host","runtime","references"),("runtime","tools","references"),("tools","domain","references")],
        notes=("SELECTED DEPENDENCIES, NOT AN EXHAUSTIVE BUILD GRAPH","UML component symbols retain the component identity; omitted direct references are listed inside cards.","The web SPA / Web host, Api.Data, migrations, and Squad remain separate projects; no network inference."),
    ),
    "email-architecture": dict(
        title="Identity, control, and execution", subtitle="Entra authenticates people; GitHub capabilities authorize purpose-bound repository access.",
        identity="DEPLOYMENT / TRUST BOUNDARIES", disposition="retain", evidence="deployment",
        groups=("AKS · control and execution workloads", "External identity, capabilities, and durable data"),
        nodes=[
            card("api","API + worker","Control plane services","Postgres + CSI mounted data","CONTROL","lavender","mxgraph.kubernetes.pod",("API hosts execution orchestration","MCP calls API; no MCP database")),
            card("mcp","MCP service","Authenticated tool surface","HTTP API client","TOOLS","teal","mxgraph.kubernetes.pod",("Broker-token validation","No database connector from MCP")),
            card("sandbox","AgentHost pod","Constrained turn execution","per-run bearer / sandbox","EXECUTION","green","mxgraph.kubernetes.pod",("Worker configures and streams A2A","Repository credential flow exists")),
            card("entra","Microsoft Entra","User identity","sign-in / sessions / roles","IDENTITY","teal","mxgraph.azure2.azure_active_directory",("Authentication is not repo authority","Distinct from GitHub capability grants")),
            card("github","GitHub","Purpose-bound capabilities","repository authorization","CAPABILITY","lavender","cloud",("Constrained credentials can reach execution","Not a credential-free sandbox")),
            card("postgres","PostgreSQL","Shared durable state","API + worker persistence","DATA","teal","cylinder3",("Run state / events / project data","CSI storage serves mounted files")),
        ],
        edges=[("mcp","api","HTTP API"),("api","sandbox","configure / A2A"),("entra","api","identity"),("github","api","capability"),("api","postgres","read / write")],
        notes=("TRUST FLOWS ARE DELIBERATELY DISTINCT","GitHub authorization constrains repository credentials used during execution; Entra establishes user identity.","API and worker use PostgreSQL and CSI-mounted storage. MCP is an API client, not another database owner."),
    ),
}

SEQUENCES = {
    "canonical-durable-event-stream-sequence": dict(
        title="Commit first. Replay by cursor.", subtitle="EF / PostgreSQL sequence · durable delivery across API replicas",
        identity="UML SEQUENCE / EF POSTGRES", disposition="redesign", evidence="events",
        participants=[
            card("producer","Producer","run execution","replica A","WRITE","green"),
            card("stream","EF stream","append + subscribe","service","EF","teal","component"),
            card("store","Postgres","RunEvents","shared store","DATA","teal","cylinder3"),
            card("sse","SSE","run endpoint","replica B","READ","teal","component"),
            card("client","Watcher","web / MCP","cursor","CLIENT","lavender","umlActor"),
        ],
        messages=[
            ("producer","stream","1  append(runId, event)"),
            ("stream","store","2  lock run · allocate MAX + 1 · save"),
            ("store","stream","3  commit succeeds"),
            ("stream","producer","4  acknowledge assigned sequence"),
            ("client","sse","5  reconnect with last delivered cursor"),
            ("sse","store","6  query Sequence > cursor"),
            ("store","sse","7  return ordered durable batch"),
            ("sse","client","8  emit id + event + data; advance cursor"),
        ],
        notes=("LOOP · repeat durable reads; idle wait = 250 ms","Drain the whole batch before terminal close. Retryable assembly_blocked is not terminal.","Explicit-sequence reuse is idempotent only for matching type/payload. SQLite live channels are a separate lane."),
    ),
    "email-coordinator-workflow": dict(
        title="The Coordinator, over time", subtitle="A sequence of bounded handoffs and one collective review—not a process-map substitute",
        identity="UML SEQUENCE / COLLECTIVE WORKFLOW", disposition="redesign", evidence="journey",
        participants=[
            card("human","Human","goal + review","user","OWNER","teal","umlActor"),
            card("coord","Coordinator","spec + WorkPlan","persisted intent","CONTROL","lavender"),
            card("children","Children","DAG frontier","own worktrees","EXECUTE","green"),
            card("assembly","Assembly","integrate + gates","collective result","REVIEW","lavender"),
            card("scribe","Git / Scribe","merge + learn","approved path","FINISH","teal"),
        ],
        messages=[
            ("human","coord","1  submit goal; draft and persist OutcomeSpec"),
            ("coord","human","2  request confirmation / revision"),
            ("human","coord","3  confirm; persist WorkPlan DAG"),
            ("coord","children","4  dispatch dependency-ready children"),
            ("children","coord","5  report settled results; block failed dependents"),
            ("coord","assembly","6  integrate branches; run configured gates"),
            ("assembly","human","7  request one collective review"),
            ("human","assembly","8  approve reviewed integration"),
            ("assembly","scribe","9  MergeWorktree → Scribe"),
        ],
        notes=("APPROVED PATH · alternatives remain explicit below","Changes may redispatch work. Decline skips Scribe. Blocked assembly is recoverable—not terminal.","Merge failure may still run Scribe. No automatic PR publication is claimed by this collective workflow."),
    ),
}
MODELS.update(SEQUENCES)

class Drawing:
    def __init__(self, model, pitch=False):
        self.model = model
        self.pitch = pitch
        self.tree = E.parse(ROOT / "docs" / "diagrams" / "drawio" / "fluent-template.drawio")
        library = (ROOT / "docs" / "diagrams" / "drawio" / "fluent-library.xml").read_text(encoding="utf-8")
        self.library = json.loads(library.split("<mxlibrary>",1)[1].split("</mxlibrary>",1)[0])
        self.graph = self.tree.find(".//mxGraphModel")
        self.graph.set("pageWidth", "827")
        self.graph.set("pageHeight", "583")
        self.graph.set("background", "#efeae7")
        self.root = self.graph.find("root")
        for cell in list(self.root):
            if cell.get("id") not in ("0", "1"):
                self.root.remove(cell)
        self.tree.find(".//diagram").set("name", model["title"])
        self.pos = {}
        self.arrows = []
        self.counter = 0

    def vertex(self, id, value, x, y, w, h, style, parent="1"):
        cell = E.SubElement(self.root, "mxCell", id=id, value=value, style=style, vertex="1", parent=parent)
        E.SubElement(cell, "mxGeometry", x=str(x), y=str(y), width=str(w), height=str(h), **{"as":"geometry"})
        self.pos[id] = (x,y,w,h)
        return cell

    def text(self, id, value, x, y, w, h, size=13, color="#635c57", bold=False, align="left", parent="1"):
        return self.vertex(id,value,x,y,w,h,
            f"text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;fontFamily=Segoe UI;fontSize={size};fontColor={color};fontStyle={1 if bold else 0};align={align};verticalAlign=middle;spacing=0;", parent)

    def edge(self, id, source, target, label="", points=(), style="", ends=None):
        base = "edgeStyle=orthogonalEdgeStyle;rounded=1;curved=0;orthogonalLoop=1;jettySize=12;html=1;endArrow=block;endFill=1;strokeColor=#746d68;strokeWidth=1.5;fontFamily=Segoe UI;fontSize=12;fontColor=#3f3935;labelBackgroundColor=#fdfbf8;jumpStyle=arc;jumpSize=8;"
        if self.pitch:
            base = "endArrow=block;fontFamily=Segoe UI;fontSize=14;"
        attrs = dict(id=id, value=label, edge="1", parent="1", style=base+style)
        if not ends:
            attrs.update(source=source, target=target)
        cell = E.SubElement(self.root,"mxCell",**attrs)
        geo = E.SubElement(cell,"mxGeometry",relative="1",**{"as":"geometry"})
        if ends:
            for key, (x,y) in zip(("sourcePoint","targetPoint"),ends):
                E.SubElement(geo,"mxPoint",x=str(x),y=str(y),**{"as":key})
        if points:
            arr = E.SubElement(geo,"Array",**{"as":"points"})
            for x,y in points:
                E.SubElement(arr,"mxPoint",x=str(x),y=str(y))
        if source and target:
            self.arrows.append(dict(id=id,source=source,target=target,relationship=label,evidence=EVIDENCE[self.model["evidence"]],result="clean"))
        return cell

    def header(self):
        self.text("title", self.model["title"],28,19,770,33,26,"#272320",True)
        self.text("subtitle", self.model["subtitle"],28,58,770,26,14)
        self.text("notation", self.model["identity"],28,91,750,16,10,"#746d68",True)

    def rich_card(self,n,x,y,w=217,h=145,small=False):
        bg,fg = PALETTE[n["tone"]]
        id = n["id"]
        self.vertex(id,"",x,y,w,h,"rounded=1;arcSize=16;absoluteArcSize=1;whiteSpace=wrap;html=1;fillColor=#fdfbf8;strokeColor=#e2ddd9;strokeWidth=1;shadow=1;")
        self.vertex(id+"-accent","",0,10,5,h-20,f"rounded=1;arcSize=100;fillColor={fg};strokeColor=none;",id)
        shape = "cylinder" if n["shape"]=="cylinder3" else n["shape"]
        native = f"shape={shape};"
        if n["id"]=="entra":
            native="shape=image;image=img/lib/azure2/identity/Azure_Active_Directory.svg;"
        self.vertex(id+"-icon","",14,15,24,26,f"{native}size=6;fillColor={bg};strokeColor={fg};strokeWidth=1.4;",id)
        title=n["title"].replace("Agentweaver.","") if self.model["identity"]=="UML COMPONENT / DEPENDENCY" else n["title"]
        self.text(id+"-title",title,49,12,w-59,31,13 if small else 16,"#272320",True,parent=id)
        self.text(id+"-sub",n["sub"],14,46,w-26,22,11 if small else 12,parent=id)
        self.text(id+"-meta",n["meta"],14,68 if small else 65,w-26,16,9 if small else 10,"#746d68",parent=id)
        if not small:
            for i,fact in enumerate(n["facts"]):
                self.vertex(id+f"-bullet{i}","",15,89+i*15,4,4,f"shape=ellipse;fillColor={fg};strokeColor=none;",id)
                self.text(id+f"-fact{i}",fact,24,85+i*15,w-34,14,9,"#3f3935",parent=id)
        self.vertex(id+"-pill",n["pill"],14,h-20,w-28,15,f"rounded=1;arcSize=100;html=1;fillColor={bg};strokeColor=none;fontFamily=Segoe UI;fontSize=9;fontStyle=1;fontColor={fg};",id)

    def footer(self):
        title,line1,line2=self.model["notes"]
        self.vertex("assurance","",28,507,771,59,"rounded=1;arcSize=16;absoluteArcSize=1;fillColor=#f8f4f1;strokeColor=#e2ddd9;")
        self.vertex("assurance-accent","",28,516,5,40,"fillColor=#d39300;strokeColor=none;")
        self.text("assurance-title",title,43,511,739,15,10,"#835b00",True)
        self.text("assurance-line1",line1,43,529,739,14,10,"#3f3935")
        self.text("assurance-line2",line2,43,546,739,14,10,"#635c57")

    def graph_pitch(self):
        n = self.model["nodes"]
        self.text("pitch-title",self.model["title"],40,50,740,55,24,"#272320",True)
        for i,item in enumerate((n[0],n[1],n[2])):
            self.vertex(item["id"],item["title"],40+i*265,200,217,95,"rounded=1;fillColor=#fdfbf8;fontFamily=Segoe UI;fontSize=20;")
        self.text("takeaway",self.model["subtitle"],40,390,740,80,20)
        # A concept strip is not a flow: retain only source-grounded pitch relationships.
        ids={n[0]["id"],n[1]["id"],n[2]["id"]}
        for i,(a,b,label) in enumerate(self.model["edges"]):
            if a in ids and b in ids:
                self.edge("pitch-edge"+str(i),a,b,label)

    def graph_full(self):
        self.header()
        for i,title in enumerate(self.model["groups"]):
            y=118+i*194
            self.vertex("group"+str(i),"",28,y,771,183,"rounded=1;arcSize=16;absoluteArcSize=1;fillColor=#f8f4f1;strokeColor=#e2ddd9;strokeWidth=1;")
            self.text("group-title"+str(i),title,40,y+8,740,19,12,"#3f3935",True)
        for i,n in enumerate(self.model["nodes"]):
            self.rich_card(n,40+(i%3)*265,151+(i//3)*194,h=139)
        for i,(a,b,label) in enumerate(self.model["edges"]):
            ax,ay,aw,ah=self.pos[a]; bx,by,bw,bh=self.pos[b]
            pts=[]; st=""
            if ay == by and abs(ax-bx)<=270:
                st="exitX=1;exitY=0.5;entryX=0;entryY=0.5;" if ax<bx else "exitX=0;exitY=0.5;entryX=1;entryY=0.5;"
            elif ay == by:
                yy=493 if ay>300 else 301
                pts=[(ax+aw+18,ay+ah/2),(ax+aw+18,yy),(bx+bw+18,yy),(bx+bw+18,by+bh/2)]
                st="exitX=1;exitY=0.5;entryX=1;entryY=0.5;"
            else:
                yy=299+(i%3)*6
                gx=bx+bw+18
                pts=[(ax+aw+18,ay+ah/2),(ax+aw+18,yy),(gx,yy),(gx,by+bh/2)]
                st="exitX=1;exitY=0.5;entryX=1;entryY=0.5;"
            if self.model["identity"]=="DEPLOYMENT / TRUST BOUNDARIES":
                custom={
                    ("api","sandbox"):([(275,200),(275,298),(805,298),(805,200)],"exitX=1;exitY=0.35;entryX=1;entryY=0.35;"),
                    ("entra","api"):([(18,414),(18,220)],"exitX=0;exitY=0.5;entryX=0;entryY=0.5;"),
                    ("github","api"):([(283,414),(283,235)],"exitX=0;exitY=0.5;entryX=1;entryY=0.6;"),
                    ("api","postgres"):([(267,255),(267,306),(797,306),(797,430)],"exitX=1;exitY=0.75;entryX=1;entryY=0.6;"),
                }
                pts,st=custom.get((a,b),(pts,st))
            label=label.replace("claim + start","claim<br>+ start").replace("request review","request<br>review").replace("SSE frames","SSE<br>frames").replace("settled work","settled<br>work").replace("ordered batch","ordered<br>batch").replace("references","ref.").replace("HTTP API","HTTP<br>API")
            if self.model["identity"]=="UML COMPONENT / DEPENDENCY":
                st+="dashed=1;endArrow=open;endFill=0;"
            self.edge("e"+str(i+1),a,b,label,pts,st+"fontSize=10;")
        self.footer()

    def seq_pitch(self):
        self.text("pitch-title",self.model["title"],28,30,770,50,25,"#272320",True)
        ns=self.model["participants"]
        for i,n in enumerate(ns):
            x=28+i*158
            self.vertex(n["id"],n["title"],x,115,140,55,"rounded=1;fontFamily=Segoe UI;fontSize=17;fillColor=#fdfbf8;")
            self.edge("life"+str(i),None,None,"",style="endArrow=none;dashed=1;",ends=((x+70,170),(x+70,435)))
        for i,(a,b,label) in enumerate(self.model["messages"][:3]):
            ax=self.pos[a][0]+70;bx=self.pos[b][0]+70;y=225+i*75
            self.edge("message"+str(i+1),a,b,label,ends=((ax,y),(bx,y)))
        self.text("pitch-takeaway",self.model["subtitle"],28,460,770,50,18)

    def seq_full(self):
        self.header()
        ns=self.model["participants"]
        for i,n in enumerate(ns):
            x=28+i*158
            self.rich_card(n,x,119,140,116,True)
            self.edge("life"+str(i),None,None,"",style="endArrow=none;dashed=1;strokeColor=#b7afa8;",ends=((x+70,235),(x+70,495)))
        self.vertex("fragment","",36,248,755,245,"fillColor=none;strokeColor=#e2ddd9;rounded=1;arcSize=8;")
        count=len(self.model["messages"])
        step=25 if count==9 else 28
        details = [
            ("WRITE", "Event enters append"),
            ("LOCK", "Per-run serialized write"),
            ("DURABLE", "Transaction committed"),
            ("ACK", "Assigned sequence returns"),
            ("RESUME", "No sticky replica needed"),
            ("CURSOR", "Read strictly after cursor"),
            ("ORDER", "Durable sequence ordering"),
            ("DRAIN", "Then evaluate terminal"),
        ] if count==8 else [
            ("INTENT", "Scope and assumptions"),
            ("GATE", "Human confirms / revises"),
            ("PLAN", "Subtasks + dependencies"),
            ("FRONTIER", "Only eligible tasks run"),
            ("RESULT", "No peer-chat handoff"),
            ("GATES", "Configured review checks"),
            ("REVIEW", "One collective decision"),
            ("APPROVE", "Not completion by itself"),
            ("FINISH", "No automatic PR promise"),
        ]
        short_labels = [
            "1  append event","2  lock · MAX + 1 · save","3  commit succeeds","4  acknowledge sequence",
            "5  reconnect with cursor","6  Sequence > cursor","7  ordered batch","8  SSE frame; advance cursor",
        ] if count==8 else [
            "1  submit goal","2  confirm or revise?","3  confirm; create plan","4  dispatch ready work",
            "5  report child results","6  integrate + gates","7  request collective review",
            "8  approve integration","9  MergeWorktree → Scribe",
        ]
        for i,(a,b,label) in enumerate(self.model["messages"]):
            ax=self.pos[a][0]+70;bx=self.pos[b][0]+70;y=267+i*step
            # Each message attaches to visible UML activation bars, not to a header or container.
            for who,x in ((a,ax),(b,bx)):
                self.vertex(f"activation{i}-{who}","",x-3,y-8,6,16,"fillColor=#fdfbf8;strokeColor=#746d68;strokeWidth=1;")
            direction="exitX=1;exitY=0.5;entryX=0;entryY=0.5;" if ax<bx else "exitX=0;exitY=0.5;entryX=1;entryY=0.5;"
            self.edge("message"+str(i+1),f"activation{i}-{a}",f"activation{i}-{b}","",style=direction+"jettySize=0;")
            self.arrows[-1].update(source=a,target=b,relationship=label)
            self.text("message-label"+str(i+1),short_labels[i],min(ax,bx)+8,y-17,abs(bx-ax)-16,16,9,"#3f3935")
            nx=600 if max(ax,bx)<590 else 43
            nw=181 if nx==600 else 345
            if count==9 and i in (6,7):
                nx,nw=615,166
            self.vertex(f"context{i}","",nx,y-18,nw,23,"rounded=1;arcSize=8;fillColor=#f8f4f1;strokeColor=#e2ddd9;")
            self.vertex(f"context-accent{i}","",nx,y-14,3,15,"fillColor=#a6e9ed;strokeColor=none;")
            badge,detail=details[i]
            self.text(f"context-badge{i}",badge,nx+8,y-17,60,10,8,"#00666d",True)
            self.text(f"context-detail{i}",detail,nx+8,y-6,nw-16,10,8,"#635c57")
        self.footer()

    def save(self,path):
        E.indent(self.tree,space="  ")
        self.tree.write(path,encoding="utf-8",xml_declaration=True)

def export(path):
    dest=path.with_suffix(".png")
    result=subprocess.run([str(CLI),"--export","--format","png","--border","16","--scale","2","--output",str(dest),str(path)],capture_output=True,text=True)
    if result.returncode or not dest.exists():
        raise RuntimeError(result.stdout+result.stderr)
    print(dest.name,flush=True)

def main():
    p=argparse.ArgumentParser()
    p.add_argument("action",choices=["pitch","upgrade","next","export"])
    p.add_argument("--name",action="append")
    p.add_argument("--pass",dest="pass_number",type=int)
    args=p.parse_args()
    names=args.name or list(MODELS)
    for name in names:
        model=MODELS[name];folder=REVIEWS/name;folder.mkdir(parents=True,exist_ok=True)
        if args.action in ("pitch","upgrade"):
            pitch=args.action=="pitch"
            stage="pitch" if pitch else "pass-01"
            drawing=Drawing(model,pitch)
            if name in SEQUENCES:
                drawing.seq_pitch() if pitch else drawing.seq_full()
            else:
                drawing.graph_pitch() if pitch else drawing.graph_full()
            source=folder/f"{name}-{stage}.drawio"
            drawing.save(source)
            (folder/f"{stage}-arrows.json").write_text(json.dumps(drawing.arrows,indent=2)+"\n",encoding="utf-8")
            if pitch:
                (folder/"claim.json").write_text(json.dumps(dict(owner="shared-eight-survivors",name=name,disposition=model["disposition"],scope="exclusive_asset_paths only; no inventory writes",status="pitch-in-progress"),indent=2)+"\n",encoding="utf-8")
                (folder/"content-model.json").write_text(json.dumps(model,indent=2)+"\n",encoding="utf-8")
            export(source)
        elif args.action=="next":
            number=args.pass_number
            assert number>=2
            source=folder/f"{name}-pass-{number:02}.drawio"
            assert not source.exists(), "Never overwrite an earlier pass"
            source.write_bytes((folder/f"{name}-pass-{number-1:02}.drawio").read_bytes())
            export(source)
        elif args.action=="export":
            export(folder/f"{name}-pass-{args.pass_number:02}.drawio")

if __name__=="__main__":
    main()
