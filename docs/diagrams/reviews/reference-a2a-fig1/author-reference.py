"""Bounded authoring recipe for the two plan-reference diagrams, not a catalog generator."""
from pathlib import Path
import argparse
import copy
import json
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[4]
NAMES = ("reference-a2a-fig1", "reference-scaling-data-layer-fig1")
PAPER, CARD, INK, SECONDARY, MUTED = "#efeae7", "#fdfbf8", "#272320", "#3f3935", "#635c57"
TONES = {
    "lavender": ("#d2ccf8", "#3f3682"),
    "teal": ("#a6e9ed", "#00666d"),
    "green": ("#9fd89f", "#0e700e"),
    "marigold": ("#f9e2ae", "#835b00"),
    "neutral": ("#e7e1dc", "#635c57"),
}


class Diagram:
    def __init__(self, name):
        self.name = name
        self.doc = ET.parse(ROOT / "docs/diagrams/drawio/fluent-template.drawio")
        self.doc.getroot().set("agent", "GPT-6 Astra; reference plan")
        page = self.doc.find("diagram")
        page.set("id", name)
        page.set("name", name)
        self.model = page.find("mxGraphModel")
        self.model.set("pageWidth", "794")
        self.model.set("pageHeight", "559")
        self.model.set("background", PAPER)
        self.cells = self.model.find("root")
        for cell in list(self.cells)[2:]:
            self.cells.remove(cell)
        library = (ROOT / "docs/diagrams/drawio/fluent-library.xml").read_text()
        self.library = json.loads(library.split("<mxlibrary>", 1)[1].split("</mxlibrary>", 1)[0])
        self.trace = []
        self.cell("paper", "", 0, 0, 794, 559, f"fillColor={PAPER};strokeColor=none;")

    def cell(self, ident, label, x, y, w, h, style, parent="1"):
        c = ET.SubElement(self.cells, "mxCell", {
            "id": ident, "value": label, "style": style, "vertex": "1", "parent": parent,
        })
        ET.SubElement(c, "mxGeometry", {
            "x": str(x), "y": str(y), "width": str(w), "height": str(h), "as": "geometry",
        })
        return c

    def text(self, ident, label, x, y, w, h, size=11, color=MUTED, bold=False, mono=False):
        return self.cell(ident, label, x, y, w, h,
            f"text;html=0;whiteSpace=wrap;fillColor=none;strokeColor=none;"
            f"fontFamily={'Cascadia Code' if mono else 'Segoe UI'};fontSize={size};"
            f"fontColor={color};fontStyle={1 if bold else 0};align=left;verticalAlign=middle;spacing=0;")

    def pill(self, ident, label, x, y, w, tone="neutral"):
        bg, fg = TONES[tone]
        self.cell(ident, label, x, y, w, 17,
            f"rounded=1;arcSize=100;fillColor={bg};strokeColor=none;"
            f"fontFamily=Segoe UI;fontSize=9;fontStyle=1;fontColor={fg};")

    def group(self, ident, title, subtitle, x, y, w, h):
        self.cell(ident, "", x, y, w, h,
            "rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;strokeWidth=1;")
        self.text(ident+"-title", title, x+16, y+10, w-32, 18, 13, SECONDARY, True)
        self.text(ident+"-subtitle", subtitle, x+16, y+29, w-32, 15, 10)

    def card(self, ident, title, subtitle, meta, x, y, w, h, tone, symbol, badge, detail=None):
        bg, fg = TONES[tone]
        self.cell(ident, "", x, y, w, h,
            f"rounded=1;absoluteArcSize=1;arcSize=16;fillColor={CARD};strokeColor=#ece7e3;"
            "strokeWidth=1;shadow=1;")
        self.cell(ident+"-accent", "", x, y+8, 5, h-16,
            f"rounded=1;arcSize=100;fillColor={fg};strokeColor=none;")
        self.cell(ident+"-icon", "", x+12, y+12, 23, 23,
            f"shape={symbol};fillColor={bg};strokeColor={fg};strokeWidth=1.3;")
        self.text(ident+"-title", title, x+43, y+9, w-55, 20, 13, INK, True)
        self.text(ident+"-subtitle", subtitle, x+43, y+30, w-55,
                  14 if h < 80 or (meta and h < 90) else 27, 10.5)
        if meta:
            self.text(ident+"-meta", meta, x+14, y+h-38, w-28, 15, 9.5, "#746d68", mono=True)
        self.pill(ident+"-badge", badge, x+14, y+h-20, min(w-28, max(65, len(badge)*5.6)), tone)
        if detail:
            self.text(ident+"-detail", detail, x+14, y+60, w-28, h-102, 10.5)

    def arrow(self, ident, source, target, label, evidence, anchors="", points=(), both=False, offset=None):
        style = ("edgeStyle=orthogonalEdgeStyle;rounded=1;orthogonalLoop=1;jettySize=auto;"
            "endArrow=block;endFill=1;strokeColor=#746d68;strokeWidth=1.5;"
            "fontFamily=Segoe UI;fontSize=10;fontColor=#3f3935;labelBackgroundColor=#fdfbf8;"
            "labelBorderColor=none;jumpStyle=arc;jumpSize=7;" + anchors)
        if both:
            style += "startArrow=block;startFill=1;"
        c = ET.SubElement(self.cells, "mxCell", {
            "id": ident, "value": label, "style": style, "edge": "1",
            "parent": "1", "source": source, "target": target,
        })
        g = ET.SubElement(c, "mxGeometry", {"relative": "1", "as": "geometry"})
        if points:
            a = ET.SubElement(g, "Array", {"as": "points"})
            for x, y in points:
                ET.SubElement(a, "mxPoint", {"x": str(x), "y": str(y)})
        if offset:
            ET.SubElement(g, "mxPoint", {"x": str(offset[0]), "y": str(offset[1]), "as": "offset"})
        self.trace.append(dict(id=ident, source=source, target=target, relationship=label,
                               evidence=evidence, result="clean"))

    def save(self, stage):
        directory = ROOT / "docs/diagrams/reviews" / self.name
        directory.mkdir(parents=True, exist_ok=True)
        output = directory / f"{self.name}-{stage}.drawio"
        if output.exists():
            raise FileExistsError(f"Earlier artifact must not be overwritten: {output}")
        ET.indent(self.doc, space="  ")
        self.doc.write(output, encoding="utf-8", xml_declaration=True)
        if stage == "pass-01":
            (directory / "arrow-evidence.json").write_text(json.dumps(self.trace, indent=2)+"\n")
        print(output.relative_to(ROOT))


def pitch(name):
    d = Diagram(name)
    labels = (["Platform caller", "AgentHost turn", "Platform persistence"] if name == NAMES[0]
              else ["API + worker", "PostgreSQL + Azure Files", "Per-run local execution"])
    coords = [(45, 120, 260, 100), (475, 120, 270, 100), (45, 360, 260, 100)]
    for i, (label, (x,y,w,h)) in enumerate(zip(labels, coords)):
        d.cell("n"+str(i), label, x,y,w,h,
            f"rounded=1;fillColor={CARD};strokeColor=#e2ddd9;fontFamily=Segoe UI;"
            f"fontSize=18;fontColor={INK};")
    d.arrow("pitch-transport", "n0", "n1", "remote turn" if name == NAMES[0] else "shared persistence",
            "See three saved source-grounded research reports.")
    d.arrow("pitch-state", "n0", "n2", "checkpoint / resume" if name == NAMES[0] else "configure / execute",
            "See three saved source-grounded research reports.")
    d.save("pitch")


def a2a():
    d = Diagram(NAMES[0])
    d.text("title", "A2A: a remote turn, not a remote workflow", 20, 18, 750, 29, 23, INK, True)
    d.text("subtitle", "Transport and capabilities stop at the pod boundary. Checkpoints stay with orchestration.",
           20, 51, 750, 20, 12)
    d.group("platform", "PLATFORM / AKS", "API and worker are permitted AgentHost callers", 18, 84, 259, 376)
    d.group("pod", "PER-RUN AGENTHOST POD", "Kata boundary / listener :8088 / no workflow DB", 445, 84, 331, 376)
    d.card("api", "API caller", "Sandbox lifecycle and control", None, 34, 134, 227, 76,
           "green", "mxgraph.kubernetes.pod", "CONTROL")
    d.card("worker", "Worker / remote proxy", "Owns the orchestration graph", "RemoteAgentProxy", 34, 236, 227, 105,
           "lavender", "mxgraph.kubernetes.pod", "ONE TURN AT A TIME")
    d.card("checkpoint", "Checkpoint manager", "JSON workflow state / resume", None, 34, 375, 227, 69,
           "teal", "process", "WORKER-OWNED")
    d.card("database", "Durable platform store", "Checkpoints + run events", None, 34, 475, 227, 72,
           "teal", "cylinder3;size=6;boundedLbl=1;backgroundOutline=1", "POSTGRES IN PRODUCTION")
    d.card("configure", "Configure once", "Live Copilot capability OR BYOK", "/healthz, then /configure", 461, 134, 299, 82,
           "green", "process", "BINDS RUN + TURN BEARER")
    d.card("turn", "Turn gate", "Run-scoped bearer", None, 461, 246, 140, 82,
           "lavender", "mxgraph.flowchart.decision", "message:stream")
    d.card("card", "Card gate", "Separate option", None, 617, 246, 143, 82,
           "neutral", "mxgraph.flowchart.document", "v1/card")
    d.card("bridge", "A2ATurnBridgeAgent", "Per-turn prompt / skills / API context", "purpose-routing runner", 461, 356, 299, 88,
           "lavender", "process", "WORKFLOW OR OPERATOR")
    d.text("card-qualifier", "Empty CardBearerToken:\ncard gate disabled.", 617, 332, 143, 23, 9.5)
    d.text("transport-title", "TRANSPORT CONTROLS", 285, 91, 154, 20, 11, SECONDARY, True)
    d.text("tls", "Mounted certificates;\npinned CA validation.\nBase: mTLS off.\nProduction overlay: on.", 287, 198, 149, 57, 11)
    d.text("policy", "NetworkPolicy is not\na gateway hop.\nAPI + worker: TCP/8088.", 287, 315, 149, 47, 11)
    d.text("additive", "Policies are additive:\npreview ingress range\nalso includes 8088.", 287, 383, 149, 49, 10.5)
    d.text("credential-note", "Separate authorities", 295, 482, 455, 19, 12, SECONDARY, True)
    d.text("credential-detail", "TLS identity, card token, turn token and model capability are not interchangeable.\nPod returns text / RunEvent DataParts; writable turns can return a writeback receipt.",
           295, 506, 455, 34, 10.5)
    d.arrow("configure-call", "api", "configure", "health / configure",
            "apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:635-707",
            "exitX=1;exitY=0.45;entryX=0;entryY=0.42;")
    d.arrow("turn-stream", "worker", "turn", "turn / ordered stream",
            "packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:174-204,307-329",
            "exitX=1;exitY=0.48;entryX=0;entryY=0.5;", both=True)
    d.arrow("checkpoint-write", "worker", "checkpoint", "",
            "apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:181-188",
            "exitX=0.5;exitY=1;entryX=0.5;entryY=0;")
    d.trace[-1]["relationship"] = "Worker owns checkpoint manager"
    d.arrow("checkpoint-store", "checkpoint", "database", "save / resume",
            "apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:181-188",
            "exitX=0.5;exitY=1;entryX=0.5;entryY=0;", both=True)
    d.arrow("bridge-call", "turn", "bridge", "",
            "apps/Agentweaver.AgentHost/Program.cs:145-178,386-401",
            "exitX=0.5;exitY=1;entryX=0.22;entryY=0;")
    d.trace[-1]["relationship"] = "Authorized turn reaches bridge and purpose router"
    d.save("pass-01")


def scaling():
    d = Diagram(NAMES[1])
    d.text("title", "Scale durable state, isolate execution", 20, 18, 750, 29, 23, INK, True)
    d.text("subtitle", "Checked-in AKS topology, not a live-cluster observation or a future migration proposal.",
           20, 51, 750, 20, 12)
    d.group("platform", "PLATFORM REPLICAS", "Same application image / RollingUpdate", 18, 84, 430, 266)
    d.group("sandbox", "PER-RUN KATA POD", "AgentHost control + model-execution sidecar", 476, 84, 300, 411)
    d.cell("persistence", "", 18, 362, 430, 133,
           "rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;strokeWidth=1;")
    d.card("api", "API", "HTTP / auth / SSE\nSandbox + preview control", "agentweaver-api", 34, 135, 186, 110,
           "green", "mxgraph.kubernetes.pod", "2 REPLICAS")
    d.card("worker", "Worker", "Runs / orchestration\nValidate + apply writeback", "agentweaver-worker", 246, 135, 186, 110,
           "lavender", "mxgraph.kubernetes.pod", "HPA 2-3")
    d.card("agenthost", "AgentHost", "One-time configure; A2A turns\nReturns prepared-writeback receipt", "listener :8088", 492, 135, 268, 110,
           "lavender", "mxgraph.kubernetes.pod", "RUN-BOUND")
    d.card("local", "Pod-local checkout", "Verified source commit + tree\nModel tools edit detached checkout", "/local-workspace", 492, 286, 268, 115,
           "marigold", "mxgraph.kubernetes.vol", "DISK-BACKED emptyDir")
    d.card("postgres", "PostgreSQL", "State / memory / events\nCheckpoints + leases", None, 34, 384, 186, 104,
           "teal", "cylinder3;size=6;boundedLbl=1;backgroundOutline=1", "AZURE FLEXIBLE SERVER")
    d.card("files", "Azure Files", "Shared repo + authoritative\nchild worktrees", None, 246, 384, 186, 104,
           "green", "image;image=img/lib/azure2/storage/Azure_Fileshare.svg", "RWX /workspace")
    d.text("hpa-detail", "HPA: CPU 70%, memory 80%\nWorker PDB: minAvailable 1", 32, 270, 191, 36, 11)
    d.text("lease-detail", "Run lease: 5 min; renew halfway.\nPlan ownership: CAS + heartbeat.", 245, 270, 187, 36, 10.5)
    d.text("storage-label", "SHARED PERSISTENCE", 32, 319, 190, 20, 11, SECONDARY, True)
    d.text("database-boundary", "No sandbox DB connection.", 246, 319, 186, 20, 10)
    d.text("writeback-title", "VERIFIED PUBLICATION", 492, 418, 263, 18, 12, SECONDARY, True)
    d.text("writeback-detail", "Temporary ref goes to the shared repo, not GitHub.\nWorker verifies receipt, then applies --ff-only.\nAn unchanged tree produces a no-change receipt.",
           492, 445, 265, 43, 10.5)
    d.text("footer", "SQLite RWO PVC is retained but unmounted. HPA is active; queue-depth KEDA remains guidance.\nShared RWX storage is persistence, not a per-run isolation boundary.",
           20, 512, 750, 31, 11)
    d.cell("caller-junction", "", 228, 251, 8, 8, "ellipse;fillColor=#746d68;strokeColor=none;")
    d.cell("access-junction", "", 228, 351, 8, 8, "ellipse;fillColor=#746d68;strokeColor=none;")
    d.arrow("api-access", "api", "caller-junction", "",
            "k8s/base/api-deployment.yaml:338-354,404-409",
            "exitX=0.9;exitY=1;entryX=0;entryY=0.5;endArrow=none;", [(201,255)])
    d.trace[-1]["relationship"] = "API shares persistent services"
    d.arrow("worker-access", "worker", "caller-junction", "",
            "k8s/base/worker-deployment.yaml:146-160,256-260",
            "exitX=0.1;exitY=1;entryX=1;entryY=0.5;endArrow=none;", [(265,255)])
    d.trace[-1]["relationship"] = "Worker shares persistent services"
    d.arrow("shared-access", "caller-junction", "access-junction", "",
            "k8s/base/api-deployment.yaml:338-354; k8s/base/worker-deployment.yaml:146-160",
            "exitX=0.5;exitY=1;entryX=0.5;entryY=0;endArrow=none;")
    d.trace[-1]["relationship"] = "Both platform roles access both persistent services"
    d.arrow("database-access", "access-junction", "postgres", "state / leases",
            "apps/Agentweaver.Api/Program.cs:1027-1074",
            "exitX=0;exitY=0.5;entryX=0.5;entryY=0;", [(127,355)])
    d.arrow("workspace-access", "access-junction", "files", "workspace",
            "k8s/base/pvc-workspace.yaml:34-51",
            "exitX=1;exitY=0.5;entryX=0.5;entryY=0;", [(339,355)])
    d.arrow("a2a-control", "worker", "agenthost", "A2A",
            "k8s/base/networkpolicy-agenthost.yaml:35-48; apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:326-353",
            "exitX=1;exitY=0.45;entryX=0;entryY=0.45;", both=True)
    d.arrow("local-execution", "agenthost", "local", "execute",
            "k8s/base/sandbox-template-agenthost.yaml:151-160,332-371",
            "exitX=0.5;exitY=1;entryX=0.5;entryY=0;")
    d.arrow("source-fetch", "files", "local", "fetch",
            "apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:98-135",
            "exitX=1;exitY=0.3;entryX=0;entryY=0.35;", [(455,432),(455,326)])
    d.arrow("temp-ref", "local", "files", "publish ref",
            "apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:328-348",
            "exitX=0;exitY=0.8;entryX=1;entryY=0.8;", [(468,378),(468,467)])
    d.save("pass-01")


def correction(number):
    for name in NAMES:
        directory = ROOT / "docs/diagrams/reviews" / name
        previous = directory / f"{name}-pass-{number-1:02d}.drawio"
        output = directory / f"{name}-pass-{number:02d}.drawio"
        if output.exists():
            raise FileExistsError(output)
        doc = ET.parse(previous)
        if number == 2 and name == NAMES[1]:
            doc.find(".//mxCell[@id='temp-ref']").set("value", "temp ref")
        ET.indent(doc, space="  ")
        doc.write(output, encoding="utf-8", xml_declaration=True)
        print(output.relative_to(ROOT))


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("stage", choices=["pitch", "pass-01", "pass-02", "pass-03", "pass-04"])
    args = p.parse_args()
    if args.stage == "pitch":
        for name in NAMES:
            pitch(name)
    elif args.stage == "pass-01":
        a2a()
        scaling()
    else:
        correction(int(args.stage[-2:]))
