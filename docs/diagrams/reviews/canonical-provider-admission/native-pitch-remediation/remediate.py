"""Exclusive nine-name remediation; immutable stages and explicit inspection records."""
import copy
import hashlib
import html
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[5]
REVIEWS = ROOT / "docs/diagrams/reviews"
NAMES = [
    "assistant-runtime-fig1", "auth-security-fig1", "auth-security-fig4",
    "canonical-aks-components", "canonical-memory-context", "canonical-sandbox-boundary",
    "canonical-sandbox-experience", "sandbox-browser-preview-fig1",
    "canonical-provider-admission",
]
LINEAGE = "native-pitch-remediation"
EXE = Path(r"C:\Users\asabbour\.copilot\session-state\f6a87a42-fc18-4c23-a5f5-3e1c5c41d367\files\drawio-cli\app\draw.io.exe")
ITERATE = ROOT / ".github/skills/docs-diagram-iterate"
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("growth", ITERATE / "scripts/check_xml_growth.py")
growth = importlib.util.module_from_spec(spec)
spec.loader.exec_module(growth)
PITCH = {
    "assistant-runtime-fig1": [
        ("Conversation control", "API owns history, approvals and broker renewal.", "Idle resumes; Completed stays sealed.", "API OWNERSHIP", "database"),
        ("Turn execution", "A held AgentHost starts a fresh MCP-only SDK session.", "Pod lifetime is not conversation lifetime.", "KATA POD", "pod"),
        "API configures each turn",
    ],
    "auth-security-fig1": [
        ("Authenticate", "Endpoint metadata selects eligible credentials.", "Entra is product identity; GitHub is capability.", "IDENTITY", "entra"),
        ("Authorize", "The endpoint and resource policy decide access.", "Authentication alone never grants project access.", "PERMISSION", "decision"),
        "Validate principal, then policy",
    ],
    "auth-security-fig4": [
        ("Issue scoped broker", "Entra-backed OAuth consent issues MCP credentials.", "Assistant turns use their own renewed broker.", "IDENTITY", "entra"),
        ("Validate and forward", "MCP checks the token; API checks it independently.", "Endpoint and resource permission still apply.", "MCP RESOURCE", "decision"),
        "Scoped broker, not GitHub sign-in",
    ],
    "canonical-aks-components": [
        ("AKS control plane", "Application routing fronts API and MCP workloads.", "API and worker share durable state.", "AZURE AKS", "aks"),
        ("Isolated execution", "Workers claim and configure AgentHost pods.", "Kata execution is separate from application ingress.", "KUBERNETES POD", "pod"),
        "Claim, configure and dispatch",
    ],
    "canonical-memory-context": [
        ("Select grounded context", "Decisions, ranked memory and open session converge.", "Memory budgets do not cap all context.", "COMPILATION", "database"),
        ("Serialize untrusted data", "The compiler labels JSON as data, not instructions.", "Embedded directives are not authority.", "TRUST BOUNDARY", "process"),
        "Compile with explicit boundaries",
    ],
    "canonical-sandbox-boundary": [
        ("Govern tool actions", "Governed calls need policy and containment approval.", "Native shell and URL handling are separate paths.", "FAIL CLOSED", "decision"),
        ("Contain execution", "Workspace and Kata boundaries constrain effects.", "Restricted git / gh may receive credentials.", "KATA POD", "pod"),
        "Dispatch only allowed operations",
    ],
    "canonical-sandbox-experience": [
        ("Run lifecycle", "The API admits a run and binds its SandboxClaim.", "Run identity differs from pod lifetime.", "CONTROL", "process"),
        ("Work and review", "AgentHost executes in the run-scoped workspace.", "Evidence returns; preview needs publication proof.", "EXECUTION", "pod"),
        "Configure authenticated turns",
    ],
    "sandbox-browser-preview-fig1": [
        ("Publish and prove", "API creates routing, then probes the public HTTPS URL.", "Failed publication rolls resources back.", "CONTROL", "decision"),
        ("Serve the browser", "Gateway and Service route to the sandbox application.", "API is not the preview data proxy.", "KUBERNETES SERVICE", "service"),
        "Return a proven ready URL",
    ],
    "canonical-provider-admission": [
        ("Prepare and accept", "Signed context binds operation, subject and provider.", "Acceptance re-resolves; mismatch rejects.", "ADMISSION", "decision"),
        ("Invoke within boundary", "Private run snapshots retain the accepted provider.", "Live capability fences still apply.", "RUN SNAPSHOT", "database"),
        "Capture the accepted execution boundary",
    ],
}
SYMBOLS = {
    "process": "shape=process;fillColor=#d2ccf8;strokeColor=#3f3682;",
    "decision": "shape=rhombus;fillColor=#a6e9ed;strokeColor=#00666d;",
    "database": "shape=cylinder;fillColor=#d2ccf8;strokeColor=#3f3682;",
    "pod": "shape=mxgraph.kubernetes.pod;fillColor=#326ce5;strokeColor=none;",
    "service": "shape=mxgraph.kubernetes.svc;fillColor=#326ce5;strokeColor=none;",
    "aks": "shape=image;image=img/lib/azure2/containers/Kubernetes_Services.svg;imageAspect=1;",
    "entra": "shape=image;image=img/lib/azure2/identity/Azure_Active_Directory.svg;imageAspect=1;",
}
DETAILS = {
    "assistant-runtime-fig1": [
        ("API durable history", "History survives pod release.", "AssistantRunService.cs"),
        ("Broker lifetime", "Renew before MCP tool calls.", "OperatorAssistantAgent.cs"),
        ("Conversation lifecycle", "Idle can wake; Completed cannot.", "AssistantRunService.cs"),
    ],
    "auth-security-fig1": [
        ("Endpoint eligibility", "Cookies and broker tokens are scoped.", "AgentweaverAuthentication.cs"),
        ("Resource authority", "Project membership comes from storage.", "ProjectAuthorization.cs"),
        ("Separate capability", "GitHub Apps do not sign users in.", "LegacyOAuthRetirementTests.cs"),
    ],
    "auth-security-fig4": [
        ("Exact MCP resource", "One audience; mcp:invoke scope.", "OAuthServerConfiguration.cs"),
        ("Keyed signature", "RS256 and nonempty key id required.", "McpBrokerAuthenticationHandler.cs"),
        ("Same broker forwarded", "API revalidates and authorizes.", "AgentweaverApiClient.cs"),
    ],
    "canonical-aks-components": [
        ("Azure AKS environment", "GatewayClass: approuting-istio.", "gateway.yaml"),
        ("Manifest facts", "Worker HPA is CPU-based, 2–3.", "worker-hpa.yaml"),
        ("Distinct identities", "AgentHost has no Key Vault role.", "serviceaccount-agenthost.yaml"),
    ],
    "canonical-memory-context": [
        ("Joint memory ordering", "Importance first; recency breaks ties.", "MemoryContextCompiler.cs"),
        ("Bounded selection", "Item / character limits cover memory.", "MemoryContextCompiler.cs"),
        ("Injection resistance", "Context is wrapped as untrusted JSON.", "MemoryContextCompilerSecurityTests.cs"),
    ],
    "canonical-sandbox-boundary": [
        ("Dispatch exceptions", "Native shell denied; URL path separate.", "CopilotAIAgent.cs"),
        ("Execution isolation", "Sidecar: separate PID namespace.", "sandbox-template-agenthost.yaml"),
        ("Credential reality", "No blanket shell credential inheritance.", "RunCommandTool.cs"),
    ],
    "canonical-sandbox-experience": [
        ("Workspace ownership", "Children use isolated worktrees.", "RunOrchestrator.cs"),
        ("Pod observation", "Pod telemetry is not branch ownership.", "sandbox-pod-execution.md"),
        ("Publication evidence", "Preview readiness proves public HTTPS.", "SandboxPreviewPublicationTests.cs"),
    ],
    "sandbox-browser-preview-fig1": [
        ("Public readiness", "Probe the exact generated HTTPS URL.", "SandboxPreviewService.cs"),
        ("Rollback on failure", "Unpublish failed preview resources.", "SandboxPreviewPublicationTests.cs"),
        ("Separate ingress", "DNS managed externally, not by API.", "preview-gateway.yaml"),
    ],
    "canonical-provider-admission": [
        ("Prepared key", "Five minutes; operation / subject bound.", "AiExecutionPlanService.cs"),
        ("Private snapshot", "DB ownership points to secret storage.", "RunModelProviderSnapshotStore.cs"),
        ("Live capability", "Recheck before and after mint / read.", "GitHubCapabilityBroker.cs"),
    ],
}


def here(name):
    path = REVIEWS / name / LINEAGE
    path.mkdir(exist_ok=True)
    return path


def save_new(path, text):
    with path.open("x", encoding="utf-8") as stream:
        stream.write(text)


def write_xml(path, tree):
    ET.indent(tree, space="  ")
    save_new(path, ET.tostring(tree.getroot(), encoding="unicode"))


def cell(root, cid, value, style, x, y, w, h, parent="1"):
    c = ET.SubElement(root, "mxCell", id=cid, value=value, style=style, vertex="1", parent=parent)
    ET.SubElement(c, "mxGeometry", x=str(x), y=str(y), width=str(w), height=str(h), **{"as": "geometry"})
    return c


def label_style(size=12, color="#272320"):
    return f"text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;fontFamily=Segoe UI;fontSize={size};fontColor={color};align=left;verticalAlign=middle;spacing=0;"


def pitch(name):
    model = json.loads((REVIEWS / name / "content-model.json").read_text(encoding="utf-8"))
    tree = ET.parse(ROOT / "docs/diagrams/drawio/fluent-template.drawio")
    graph = tree.find(".//mxGraphModel")
    graph.attrib.update(pageWidth="827", pageHeight="583", pageScale="1", background="#efeae7")
    root = graph.find("root")
    for c in list(root):
        if c.get("id") not in ("0", "1"):
            root.remove(c)
    title = f'<div style="font-size:27px;font-weight:600">{html.escape(model["title"])}</div><div style="font-size:15px;color:#635c57;margin-top:12px">{html.escape(model["takeaway"])}</div>'
    cell(root, "heading", title, "text;html=1;fillColor=none;strokeColor=none;align=left;fontFamily=Segoe UI;", 40, 24, 747, 100)
    for i, (title, desc, meta, badge, symbol) in enumerate(PITCH[name][:2]):
        color, tone = (("#3f3682", "#d2ccf8") if i == 0 else ("#00666d", "#a6e9ed"))
        value = f'<div style="font-size:21px;font-weight:600">{title}</div><div style="font-size:16px;color:#3f3935;margin-top:12px">{desc}</div><div style="font-size:12px;font-family:Consolas;color:#746d68;margin-top:12px">{meta}</div><div style="margin-top:16px"><span style="background:{tone};color:{color};border-radius:12px;padding:4px 10px;font-size:11px;font-weight:600">{badge}</span></div>'
        y = 147 + i * 219
        cell(root, f"p{i}", value, "rounded=1;absoluteArcSize=1;arcSize=16;html=1;whiteSpace=wrap;shadow=1;fillColor=#fdfbf8;strokeColor=#e2ddd9;fontFamily=Segoe UI;fontColor=#272320;align=left;spacingLeft=98;spacingRight=24;", 40, y, 747, 177)
        cell(root, f"a{i}", "", f"fillColor={color};strokeColor=none;", 40, y+16, 5, 145)
        cell(root, f"s{i}", "", SYMBOLS[symbol], 65, y+63, 50, 50)
    edge = ET.SubElement(root, "mxCell", id="responsibility", value=PITCH[name][2], style="edgeStyle=orthogonalEdgeStyle;rounded=1;endArrow=block;strokeColor=#746d68;fontFamily=Segoe UI;fontSize=12;labelBackgroundColor=#efeae7;exitX=0.5;exitY=1;entryX=0.5;entryY=0;", edge="1", source="p0", target="p1", parent="1")
    ET.SubElement(edge, "mxGeometry", relative="1", **{"as": "geometry"})
    path = here(name) / f"{name}-pitch.drawio"
    write_xml(path, tree)
    print(name, growth.analyze(path.read_text())["meaningful_count"])


def upgrade(name, version="pass-01"):
    tree = ET.parse(REVIEWS / name / f"{name}-pass-04.drawio")
    root = tree.find(".//mxGraphModel/root")
    cells = {c.get("id"): c for c in root}
    # Compact the original card diagram upward to reserve an evidence strip.
    for c in root:
        geo = c.find("mxGeometry")
        if geo is not None and c.get("vertex") == "1" and c.get("parent") == "1":
            y = float(geo.get("y", 0))
            if y >= 322:
                geo.set("y", str(y - 30))
    for c in root:
        geo = c.find("mxGeometry")
        if c.get("edge") == "1":
            for p in geo.findall(".//mxPoint"):
                if float(p.get("y", 0)) >= 322:
                    p.set("y", str(float(p.get("y")) - 30))
        if c.get("id", "").endswith("-title"):
            c.set("style", c.get("style").replace("fontSize=16;", "fontSize=14;"))
        if c.get("id", "").endswith("-metadata"):
            c.set("value", c.get("value", "").replace("\ufffd", "·"))
    if name in ("canonical-aks-components", "sandbox-browser-preview-fig1"):
        key = "n0-icon" if name == "canonical-aks-components" else "n3-icon"
        cells[key].set("style", SYMBOLS["aks"])
    if name == "canonical-aks-components":
        for key in ("n1-icon", "n2-icon", "n3-icon"):
            cells[key].set("style", "shape=mxgraph.kubernetes.deploy;fillColor=#326ce5;strokeColor=none;")
    if name == "auth-security-fig1":
        cells["n2-icon"].set("style", SYMBOLS["entra"])
    if name == "auth-security-fig4":
        cells["n1-icon"].set("style", SYMBOLS["entra"])
    # The evidence strip adds distinct assurance detail, not duplicate visual padding.
    for cid in ("footer",):
        if cid in cells:
            root.remove(cells[cid])
    for i, (title, fact, evidence) in enumerate(DETAILS[name]):
        x = 28 + i * 260
        cell(root, f"assurance-{i}", "", "rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;", x, 478, 251, 91)
        cell(root, f"assurance-{i}-label", title, label_style(11, "#3f3935") + "fontStyle=1;", x+12, 484, 227, 20)
        cell(root, f"assurance-{i}-fact", fact, label_style(10, "#635c57"), x+12, 507, 227, 24)
        cell(root, f"assurance-{i}-source", evidence, label_style(9, "#746d68"), x+12, 538, 227, 23)
    path = here(name) / f"{name}-{version}.drawio"
    write_xml(path, tree)
    result = growth.assess((here(name) / f"{name}-pitch.drawio").read_text(), path.read_text())
    save_new(here(name) / f"{version}-growth.json", json.dumps(result, indent=2))
    print(name, result["baseline_meaningful_xml"], result["result_meaningful_xml"], result["growth_ratio"], result["passed"])


def export(name, stage):
    path = here(name) / f"{name}-{stage}.drawio"
    png = path.with_suffix(".png")
    if png.exists():
        raise RuntimeError(f"Immutable export already exists: {png}")
    run = subprocess.run([str(EXE), "--export", "--format", "png", "--border", "16", "--scale", "2", "--output", str(png), str(path)], capture_output=True, text=True)
    save_new(path.with_suffix(".export.log"), run.stdout + run.stderr)
    if run.returncode or not png.is_file():
        raise RuntimeError(f"Export failed: {path}")
    image = Image.open(png).convert("RGB")
    image.resize((827, round(image.height * 827/image.width)), Image.Resampling.LANCZOS).save(path.with_name(path.stem+"-print.png"))
    print(name, stage, image.size)


def sheet(stage):
    # Each tile is a separately exported artifact at its 100-units/inch print scale.
    canvas = Image.new("RGB", (827*3, 610*3), "#efeae7")
    draw = ImageDraw.Draw(canvas)
    for i, name in enumerate(NAMES):
        p = here(name) / f"{name}-{stage}-print.png"
        im = Image.open(p)
        x, y = (i % 3)*827, (i//3)*610
        draw.text((x+12, y+3), name, fill="#272320")
        canvas.paste(im, (x, y+25))
    path = here(NAMES[-1]) / f"{stage}-print-contact.png"
    if path.exists():
        raise RuntimeError("Immutable contact sheet exists")
    canvas.save(path)


def correct_two(name):
    tree = ET.parse(here(name) / f"{name}-pass-01.drawio")
    root = tree.find(".//mxGraphModel/root")
    for c in root:
        cid = c.get("id", "")
        g = c.find("mxGeometry")
        if g is None:
            continue
        if cid in [f"n{i}" for i in range(6)]:
            g.set("height", "130")
            if int(cid[1:]) >= 3:
                g.set("y", "312")
        elif cid.startswith("boundary"):
            g.set("height", "158")
            if cid == "boundary1":
                g.set("y", "290")
        elif cid == "group-title1":
            g.set("y", "292")
        elif c.get("parent", "").startswith("n"):
            for suffix, y, height in [
                ("-subtitle", 44, 18), ("-fact0", 64, 16), ("-fact1", 80, 16),
                ("-metadata", 98, 15), ("-badge", 115, 13), ("-accent", 10, 110),
            ]:
                if cid.endswith(suffix):
                    g.set("y", str(y))
                    g.set("height", str(height))
        elif cid.startswith("relation-"):
            i = int(cid.split("-")[1])
            g.set("y", str(450 + (i//3)*16))
        elif cid == "assurance-band":
            g.set("y", "486")
            g.set("height", "28")
        elif cid == "assurance":
            g.set("y", "487")
            g.set("height", "26")
        elif cid.startswith("assurance-"):
            if cid[-1].isdigit():
                g.set("y", "520")
                g.set("height", "56")
            elif cid.endswith("-label"):
                g.set("y", "522")
                g.set("height", "15")
            elif cid.endswith("-fact"):
                g.set("y", "538")
                g.set("height", "18")
            elif cid.endswith("-source"):
                g.set("y", "558")
                g.set("height", "14")
        if c.get("edge") == "1":
            for point in g.findall(".//mxPoint"):
                y = float(point.get("y", 0))
                if y in (294, 300, 306, 318):
                    point.set("y", str({294: 270, 300: 276, 306: 282, 318: 288}[y]))
                elif y > 330:
                    point.set("y", str(round(312+(y-316)*130/146, 3)))
    write_xml(here(name) / f"{name}-pass-02.drawio", tree)


def correct_three(name):
    tree = ET.parse(here(name) / f"{name}-pass-02.drawio")
    cells = {c.get("id"): c for c in tree.findall(".//mxCell")}
    for c in cells.values():
        points = c.findall(".//mxPoint")
        if c.get("edge") != "1" or not points:
            continue
        style = growth.parse_style(c.get("style", ""))
        for prefix, point, endpoint in [
            ("entry", points[-1], c.get("target")),
            ("exit", points[0], c.get("source")),
        ]:
            if style.get(prefix+"X") in ("0", "1") and style.get(prefix+"Y") not in ("0", "1"):
                g = cells[endpoint].find("mxGeometry")
                y = float(g.get("y")) + float(g.get("height")) * float(style[prefix+"Y"])
                point.set("y", str(y))
    write_xml(here(name) / f"{name}-pass-03.drawio", tree)


def main():
    command = sys.argv[1]
    names = sys.argv[3:] if len(sys.argv)>3 else NAMES
    for name in names:
        assert name in NAMES
        if command == "pitch":
            pitch(name)
        elif command == "upgrade":
            upgrade(name, sys.argv[2])
        elif command == "export":
            export(name, sys.argv[2])
        elif command == "correct-two":
            correct_two(name)
        elif command == "correct-three":
            correct_three(name)
        elif command == "copy":
            stage = int(sys.argv[2])
            src = here(name) / f"{name}-pass-{stage-1:02}.drawio"
            save_new(here(name) / f"{name}-pass-{stage:02}.drawio", src.read_text(encoding="utf-8"))
        elif command == "correct-five":
            src = here(name) / f"{name}-pass-04.drawio"
            text = src.read_text(encoding="utf-8").encode("cp1252").decode("utf-8")
            assert text == (here(name) / f"{name}-pass-03.drawio").read_text(encoding="utf-8")
            save_new(here(name) / f"{name}-pass-05.drawio", text)
        elif command == "correct-citation":
            assert name == "sandbox-browser-preview-fig1"
            src = here(name) / f"{name}-pass-05.drawio"
            text = src.read_text(encoding="utf-8")
            assert text.count('value="preview-gateway.yaml"') == 1
            text = text.replace('value="preview-gateway.yaml"', 'value="gateway-preview.yaml"')
            save_new(here(name) / f"{name}-pass-06.drawio", text)
    if command == "sheet":
        sheet(sys.argv[2])


if __name__ == "__main__":
    main()
