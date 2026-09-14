"""Bounded authoring helper. Writes only this assignment's per-name review assets."""
from pathlib import Path
import argparse
import copy
import html
import json
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[4]
REVIEWS = ROOT / "docs" / "diagrams" / "reviews"
CLI = Path(r"C:\Users\asabbour\.copilot\session-state\f6a87a42-fc18-4c23-a5f5-3e1c5c41d367\files\drawio-cli\app\draw.io.exe")
COLORS = [
    ("#3f3682", "#d2ccf8"), ("#00666d", "#a6e9ed"),
    ("#0e700e", "#9fd89f"), ("#835b00", "#f9e2ae"),
    ("#635c57", "#e7e1dc"), ("#00666d", "#a6e9ed"),
]
SYMBOLS = {
    "person": ("umlActor", "native:uml"),
    "pod": ("mxgraph.kubernetes.pod", "native:kubernetes"),
    "service": ("mxgraph.kubernetes.svc", "native:kubernetes"),
    "database": ("cylinder", "native:database"),
    "process": ("process", "native:flowchart"),
    "decision": ("rhombus", "native:flowchart"),
    "cloud": ("cloud", "native:cloud"),
    "azure": ("mxgraph.networks.router", "native:networking"),
    "custom": ("hexagon", "custom:agentweaver"),
}


def node(title, subtitle, lines, meta, badge, symbol, evidence):
    return dict(title=title, subtitle=subtitle, lines=lines, meta=meta,
                badge=badge, symbol=symbol, evidence=evidence)


DATA = {
    "assistant-runtime-fig1": dict(
        title="A conversation survives its pod",
        takeaway="API-owned history; a held AgentHost runs a fresh, MCP-only SDK session each turn.",
        groups=["CONVERSATION CONTROL", "DURABILITY / EXECUTION"],
        footer="Pod quiet 5 min: release  •  Conversation quiet 30 min: Idle, resumable  •  Completed: sealed",
        page="docs/deep-dive/assistant-runtime.md",
        nodes=[
            node("Sessions UI", "Entra-authenticated caller", ["Start or append a message", "Approval replies stay at API"], "/api/assistant/runs", "CALLER", "person", "apps/Agentweaver.Api/Endpoints/AssistantEndpoints.cs; docs/deep-dive/assistant-runtime.md:36-40"),
            node("Assistant API", "Durable conversation owner", ["Serialize turns per run", "Issue + renew MCP broker"], "broker lifetime: 5 min", "CONTROL", "custom", "apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs"),
            node("Held AgentHost", "Pod reused across turns", ["One-shot /configure", "Per-turn broker refresh"], "A2A turn bearer", "POD", "pod", "apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:77-83; apps/Agentweaver.AgentHost/Program.cs:205-229"),
            node("Run + event store", "Authoritative conversation", ["Append AgentMessage events", "Reload latest 24 messages"], "Idle → InProgress (CAS)", "DURABLE", "database", "docs/deep-dive/assistant-runtime.md:39-42; apps/Agentweaver.Api/Assistant/AssistantRunService.cs"),
            node("MCP server", "Broker-only tool boundary", ["Validate issuer + audience", "Consequential tools gated"], "RS256 • mcp:invoke", "TOOLS", "process", "apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85; packages/Agentweaver.AgentRuntime/OperatorToolApprovalPolicy.cs"),
            node("Fresh SDK session", "OperatorAssistantAgent", ["Seed reconstructed history", "No native shell / files"], "SDK session store: off", "TURN", "custom", "packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs; docs/deep-dive/assistant-runtime.md:104-121"),
        ],
        edges=[(0,1,"message"),(1,2,"configure / turn"),(2,5,"run turn"),(5,4,"MCP tools"),(1,3,"append / reload"),(4,1,"API authorization")],
        pitch=[0,1,2],
    ),
    "auth-security-fig1": dict(
        title="Authenticate, then authorize",
        takeaway="Endpoint metadata selects a credential handler; persisted permissions decide resource access.",
        groups=["IDENTITY BOUNDARY", "AUTHORIZATION BOUNDARY"],
        footer="GitHub connections are execution capabilities, never platform identity. Unclassified endpoints fail closed.",
        page="docs/deep-dive/auth-security.md",
        nodes=[
            node("Protected request", "Endpoint classification", ["Read endpoint metadata", "Read bearer / browser cookie"], "No GitHubLegacy scheme", "REQUEST", "process", "apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:20-57"),
            node("Scheme selector", "Ordered, not permissive", ["Internal / run first", "Broker / cookie if eligible"], "Entra is the default", "ROUTING", "decision", "apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:28-57"),
            node("Entra handler", "Platform caller identity", ["Validate tenant + audience", "Map subject and app roles"], "Invalid identity: reject", "IDENTITY", "process", "apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:131-153"),
            node("Scoped handlers", "Metadata-limited alternatives", ["Internal key • run capability", "Broker token • browser session"], "Not a fallback chain", "SCOPED", "process", "apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:32-55; 159-250"),
            node("Resource authorizer", "After authentication", ["Read persisted membership", "Enforce platform / project role"], "Caller ≠ resource grant", "ACCESS", "decision", "apps/Agentweaver.Api/Security/ProjectAuthorization.cs:56-85,114-135; docs/deep-dive/auth-security.md:5-23"),
            node("Protected operation", "Authorized resource scope", ["Project, run or self endpoint", "Deny insufficient permission"], "Capability ≠ identity", "RESULT", "process", "docs/deep-dive/auth-security.md:5-23; apps/Agentweaver.Api/Security"),
        ],
        edges=[(0,1,"classify"),(1,2,"otherwise"),(1,3,"eligible"),(2,4,"authenticated"),(3,4,"authenticated"),(4,5,"authorized")],
        pitch=[0,1,5],
    ),
    "auth-security-fig4": dict(
        title="MCP trusts the broker, not GitHub",
        takeaway="OAuth and assistant turns obtain Agentweaver broker tokens; MCP and API validate independently.",
        groups=["BROKER ISSUANCE", "VALIDATION AND RESOURCE ACCESS"],
        footer="Exact issuer + one resource audience + keyed RS256 + lifetime + subject + mcp:invoke. No raw GitHub bearer.",
        page="docs/deep-dive/auth-security.md",
        nodes=[
            node("External MCP client", "OAuth 2.1 + PKCE S256", ["Discovery and authorization", "Code exchange / token refresh"], "Entra user sign-in", "CLIENT", "person", "docs/deep-dive/auth-security.md:25-31; apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs"),
            node("Authorization server", "Agentweaver-issued broker", ["Entra-backed user consent", "Sign scoped access token"], "Revocable refresh family", "ISSUER", "process", "apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs; docs/deep-dive/auth-security.md:25-31"),
            node("Assistant API", "Separate internal issuance", ["5-minute broker + renewal", "Not the browser bearer"], "Held pod gets refreshed", "ASSISTANT", "custom", "apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs; apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813"),
            node("MCP validation", "OpenIddict + strict checks", ["Key id + RS256 signature", "Exact issuer / audience"], "subject • TTL • scope", "VERIFY", "decision", "apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85"),
            node("API broker handler", "Revalidate forwarded token", ["PlatformOrMcp endpoints", "AuthenticatedSelfOrMcp too"], "Same broker, not GitHub", "VERIFY", "process", "apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:45-49; 194-250"),
            node("Resource authorization", "Persisted access checks", ["Project roles still enforced", "Revoked access fails closed"], "No implicit project grant", "ACCESS", "decision", "docs/deep-dive/auth-security.md:29-31; apps/Agentweaver.Api/Security"),
        ],
        edges=[(0,1,"OAuth exchange"),(1,3,"broker token"),(2,3,"turn broker"),(3,4,"forward broker"),(4,5,"authorize")],
        pitch=[1,3,4],
    ),
    "canonical-aks-components": dict(
        title="AKS separates control from execution",
        takeaway="Replicated API and workers share durable services; AgentHost pods execute isolated turns.",
        groups=["APPLICATION CONTROL", "EXECUTION / DURABLE STATE"],
        footer="Application and preview Gateways are separate. AgentHost has no Key Vault-role identity or CSI secret mount.",
        page="docs/aks-deployment.md",
        nodes=[
            node("Application ingress", "Frontend deployment", ["AKS App Routing Gateway", "Frontend: 2 replicas"], "Preview gateway separate", "EDGE", "azure", "k8s/base/frontend-deployment.yaml:10; k8s/base/gateway.yaml; k8s/base/gateway-preview.yaml"),
            node("API deployment", "Request and run control", ["2 API replicas", "Postgres + CSI secrets"], "Shared workspace mount", "CONTROL", "pod", "k8s/base/api-deployment.yaml:13,67-72,415-419"),
            node("MCP deployment", "Broker-authenticated tools", ["1 MCP replica", "Forwards requests to API"], "No CSI secret mount", "TOOLS", "pod", "k8s/base/mcp-deployment.yaml:10; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs"),
            node("Worker deployment", "Background orchestration", ["2 baseline replicas", "HPA scales from 2 to 3"], "Postgres + CSI secrets", "WORKERS", "pod", "k8s/base/worker-deployment.yaml:13,67-72,266-270; k8s/base/worker-hpa.yaml:65-66"),
            node("AgentHost pods", "SandboxClaim warm pool", ["Per-run /configure", "Kata-isolated agent turns"], "No ambient user secrets", "SANDBOX", "pod", "k8s/base/sandbox-warmpool-agenthost.yaml; apps/Agentweaver.AgentHost/Program.cs:232-285"),
            node("Durable services", "Postgres + Azure Files", ["Run state / events in DB", "RWX project workspace"], "Key Vault via API/worker CSI", "STATE", "database", "k8s/base/api-deployment.yaml:67-72,415-419; k8s/base/pvc-workspace.yaml; k8s/base/worker-deployment.yaml:266-270"),
        ],
        edges=[(0,1,"HTTPS"),(2,1,"API tools"),(1,5,"persist / mount"),(3,5,"persist / mount"),(3,4,"claim + dispatch")],
        pitch=[1,4,5],
    ),
    "canonical-sandbox-boundary": dict(
        title="Several checks contain each action",
        takeaway="Native shell is denied; governed tools combine AGT policy, direct containment and execution isolation.",
        groups=["TOOL SELECTION / POLICY", "POINT-OF-USE CONTAINMENT"],
        footer="Current code delivers repository credentials into Host/tool options; the normative no-credential contract is NOT met.",
        page="docs/deep-dive/sandbox.md",
        nodes=[
            node("Model tool request", "Permission dispatch", ["Native shell: always denied", "URL approvals handled apart"], "Custom reporting bypass", "DISPATCH", "decision", "packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:1850-1920,2079-2139"),
            node("Governance", "Deny-by-default policy", ["AGT policy must allow", "Direct backend must allow"], "Both checks, not either", "POLICY", "decision", "packages/Agentweaver.AgentRuntime/SandboxGovernance.cs:114-162"),
            node("Registered tools", "Explicit capability surface", ["Files revalidate at use", "run_command gates shell"], "Unknown tools denied", "TOOLS", "process", "packages/Agentweaver.AgentRuntime/SandboxGovernance.cs; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs"),
            node("Workspace boundary", "Sandbox filesystem", ["Lexical + real-path checks", "Reject symlink escapes"], "Bounded / redacted output", "FILES", "process", "packages/Agentweaver.SandboxFs; docs/deep-dive/sandbox.md:72-110"),
            node("Execution boundary", "Selected isolation backend", ["Shell policy + approval", "Kata pod in AKS"], "Direct mode is opt-in", "PROCESS", "pod", "apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs; docs/deep-dive/sandboxed-execution.md:20-40"),
            node("Credential handling", "Current implementation", ["Host + tool options hold token", "Direct git status / allowed gh"], "No blanket shell injection", "CURRENT", "process", "apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs:92-95,172-174; packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:446-453; docs/deep-dive/sandboxed-execution.md:113-130"),
        ],
        edges=[(0,1,"governed calls"),(1,2,"both allow"),(2,3,"file operation"),(2,4,"run_command"),(4,5,"eligible git / gh")],
        pitch=[0,1,4],
    ),
    "canonical-sandbox-experience": dict(
        title="From run request to reviewable work",
        takeaway="A run gets isolated execution, visible progress and bounded tools—not an unrestricted host shell.",
        groups=["ADMIT AND PREPARE", "EXECUTE AND RETURN EVIDENCE"],
        footer="Credentials arrive via /configure, not ambient stores. Approval, policy and isolation remain independent checks.",
        page="docs/deep-dive/sandbox-pod-execution.md",
        nodes=[
            node("Authorized run", "User requests repository work", ["Provider acceptance first", "API owns run lifecycle"], "Project role required", "REQUEST", "person", "apps/Agentweaver.Api/Endpoints/RunEndpoints.cs; docs/deep-dive/sandbox-pod-execution.md"),
            node("SandboxClaim", "Bind a warm AgentHost pod", ["Resolve claim-bound pod", "Configure identity once"], "Claim state is shared", "PREPARE", "custom", "apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs; apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs"),
            node("AgentHost", "Model and governed tools", ["A2A authenticated turns", "Run-scoped workspace"], "Copilot OR BYOK", "EXECUTE", "pod", "apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs"),
            node("Run experience", "Events and human decisions", ["Progress / tool evidence", "Approve consequential work"], "Approval ≠ policy bypass", "OBSERVE", "custom", "docs/deep-dive/sandbox.md:65-70; packages/Agentweaver.AgentRuntime/InMemoryToolApprovalGate.cs"),
            node("Isolated workspace", "File and shell results", ["Contain paths and processes", "Bound / redact tool output"], "Network policy also applies", "WORK", "process", "docs/deep-dive/sandbox.md:12-24; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs"),
            node("Preview or review", "Inspect resulting work", ["Preview needs publication proof", "Review artifacts before merge"], "Release / reap compute", "OUTCOME", "process", "apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-330; docs/deep-dive/sandbox-pod-execution.md"),
        ],
        edges=[(0,1,"start"),(1,2,"configure"),(2,4,"tools"),(2,3,"events / approvals"),(4,5,"work artifacts")],
        pitch=[0,2,5],
    ),
    "sandbox-browser-preview-fig1": dict(
        title="Preview readiness follows the public path",
        takeaway="Provision the route, then probe its exact HTTPS URL; object creation alone is not ready.",
        groups=["CONTROL: PROVISION + PROBE", "GATEWAY DATA PATH"],
        footer="No API → pod TCP readiness probe. Publication failure rolls back; DNS convergence has a bounded retry window.",
        page="docs/deep-dive/sandbox-browser-preview.md",
        nodes=[
            node("Preview API", "Resolve bound SandboxClaim", ["Patch run selector on pod", "Create Service + HTTPRoute"], "State from cluster, not cache", "PROVISION", "custom", "apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:192-305"),
            node("Publication probe", "Exact generated HTTPS URL", ["Wait for managed DNS", "Check Gateway + application"], "Only then return ready", "VERIFY", "decision", "apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-380"),
            node("Browser preview", "Open the returned URL", ["Run-scoped capability host", "Keepalive via API"], "Iframe: no-referrer", "CLIENT", "person", "docs/deep-dive/sandbox-browser-preview.md:64-68"),
            node("Preview Gateway", "Separate shared Gateway", ["HTTPS host match", "HTTPRoute selects Service"], "Not API port-forward", "ROUTE", "azure", "k8s/base/gateway-preview.yaml; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:253-284"),
            node("ClusterIP Service", "Per-preview target selector", ["Service :80 → public port", "Routes to bound sandbox pod"], "Allowed ports 3000–9000", "NETWORK", "service", "apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:223-250; k8s/base/networkpolicy-sandbox.yaml"),
            node("Sandbox preview app", "AgentHost pod-local path", ["Live preview: TCP forwarder", "0.0.0.0 → loopback app"], "Manual: chosen target port", "POD", "pod", "apps/Agentweaver.AgentHost/TcpPortForwarder.cs; docs/deep-dive/sandbox-browser-preview.md:71-98"),
        ],
        edges=[(0,1,"after create"),(1,2,"ready URL"),(1,3,"HTTPS probe"),(2,3,"HTTPS"),(3,4,"route"),(4,5,"public port")],
        pitch=[0,3,5],
    ),
    "canonical-memory-context": dict(
        title="Context is selected data, not instructions",
        takeaway="Approved decisions, jointly ranked memories and the open session converge into untrusted JSON.",
        groups=["SCOPED INPUTS", "SELECTION AND SERIALIZATION"],
        footer="Defaults: 20 memory items / ≈4,000 tokens. That budget bounds selected memories—not decisions or the entire context.",
        page="docs/deep-dive/memory-decisions.md",
        nodes=[
            node("Active decisions", "Project-wide boundaries", ["Approved architecture / scope", "Oldest-created first"], "Child prompts: decisions only", "DECISIONS", "custom", "apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:54-63,154-176"),
            node("Core + learnings", "Agent-scoped candidates", ["Core: exclude legacy trust", "High learning / pattern"], "Approved cross-team allowed", "MEMORIES", "database", "apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:65-89"),
            node("Open session", "Latest active session", ["Focus / issues / summary", "Ended sessions excluded"], "Latest StartedAt wins", "SESSION", "custom", "apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:94-104"),
            node("Joint rank + budget", "One combined memory list", ["Importance, then recency", "Stop at item / char limit"], "Approximation: 4 chars/token", "SELECT", "process", "apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:107-140"),
            node("Context compiler", "Assemble scoped sections", ["Decisions + selected memory", "Add current session"], "Empty inputs → null", "COMPILE", "custom", "apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:101-104,179-213"),
            node("Untrusted JSON", "Historical data, not authority", ["Explicit boundary markers", "Ignore embedded instructions"], "untrusted-context.v1", "OUTPUT", "process", "apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:185-225"),
        ],
        edges=[(1,3,"combine / sort"),(0,4,"approved"),(2,4,"latest open"),(3,4,"selected"),(4,5,"serialize")],
        pitch=[1,4,5],
    ),
    "canonical-provider-admission": dict(
        title="Accept a provider before invoking it",
        takeaway="Signed admission context freezes execution choice; live capability checks remain separate.",
        groups=["PREPARE AND ACCEPT", "RUN BOUNDARY AND LIVE FENCES"],
        footer="Mismatch rejects with replacement context. A frozen provider snapshot does not bypass live GitHub capability fences.",
        page="docs/deep-dive/auth-security.md",
        nodes=[
            node("Prepare context", "Resolve effective provider", ["Bind operation + project", "Bind subject + provider key"], "Signed • expires in 5 min", "PREPARE", "process", "apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:205-207,256-290"),
            node("Accept request", "Re-resolve and compare", ["Verify signature + expiry", "Reject mismatched context"], "Replacement context on error", "ACCEPT", "decision", "apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293-358"),
            node("Accepted plan", "One execution provider", ["Freeze BYOK configuration", "Provider choice is immutable"], "Copilot OR BYOK", "BOUNDARY", "custom", "apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:121-183; apps/Agentweaver.Api/Auth/EffectiveModelProviderResolver.cs"),
            node("Run snapshot", "Private durable ownership", ["Database owner → secret ref", "Secret store holds snapshot"], "Child / retry inheritance", "DURABLE", "database", "apps/Agentweaver.Api/Auth/RunModelProviderSnapshotStore.cs:45-77,83-155; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:796-825"),
            node("Invocation guard", "Check accepted run boundary", ["Match operation and provider", "Reject inconsistent execution"], "No silent provider fallback", "GUARD", "decision", "apps/Agentweaver.Api/Auth/RunModelInvocationGuard.cs:11-48; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:826-855"),
            node("Capability fences", "Separate live permission checks", ["Before / after mint or read", "Reject revoked or changed grant"], "Snapshot is not a bypass", "LIVE", "decision", "apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:35-123; apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs"),
        ],
        edges=[(0,1,"signed context"),(1,2,"match"),(2,3,"capture"),(3,4,"load boundary"),(4,5,"Copilot capability")],
        pitch=[0,1,4],
    ),
}


def start(name):
    tree = ET.parse(ROOT / "docs/diagrams/drawio/fluent-template.drawio")
    raw = (ROOT / "docs/diagrams/drawio/fluent-library.xml").read_text(encoding="utf-8")
    library = json.loads(raw.removeprefix("<mxlibrary>").strip().removesuffix("</mxlibrary>"))
    if len(library) != 4:
        raise ValueError("unexpected Fluent library contents")
    root = tree.getroot()
    root.set("compressed", "false")
    diagram = root.find("diagram")
    diagram.set("id", name)
    diagram.set("name", DATA[name]["title"])
    model = diagram.find("mxGraphModel")
    model.set("pageWidth", "827")
    model.set("pageHeight", "583")
    model.set("background", "#efeae7")
    cells = model.find("root")
    cells.clear()
    ET.SubElement(cells, "mxCell", id="0")
    ET.SubElement(cells, "mxCell", id="1", parent="0")
    return tree, cells


def vertex(root, ident, value, box, style, parent="1"):
    cell = ET.SubElement(root, "mxCell", id=ident, value=value, style=style,
                         vertex="1", parent=parent)
    x,y,w,h = box
    ET.SubElement(cell, "mxGeometry", x=str(x), y=str(y), width=str(w), height=str(h),
                  **{"as":"geometry"})
    return cell


def text(root, ident, value, box, size=13, color="#635c57", bold=False, parent="1", mono=False):
    return vertex(root, ident, value, box,
                  f"text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;"
                  f"fontFamily={'Cascadia Code' if mono else 'Segoe UI'};fontSize={size};"
                  f"fontColor={color};fontStyle={1 if bold else 0};align=left;verticalAlign=middle;"
                  "spacing=0;", parent)


POSITIONS = [(40,126),(302,126),(564,126),(40,344),(302,344),(564,344)]


def edge(root, ident, source, target, label, pitch=False):
    style = ("edgeStyle=orthogonalEdgeStyle;rounded=1;orthogonalLoop=1;jettySize=14;"
             "html=1;endArrow=block;endFill=1;strokeColor=#746d68;strokeWidth=1.6;"
             "fontFamily=Segoe UI;fontSize=11;fontColor=#3f3935;"
             "labelBackgroundColor=#fdfbf8;jumpStyle=arc;jumpSize=6;")
    number=int(ident[1:]) if not pitch else 0
    cell = ET.SubElement(root, "mxCell", id=ident, source=f"n{source}", target=f"n{target}",
                         value=str(number+1) if not pitch else label, style=style, edge="1", parent="1")
    geometry = ET.SubElement(cell, "mxGeometry", relative="1", **{"as":"geometry"})
    if not pitch:
        sx,sy = POSITIONS[source]
        tx,ty = POSITIONS[target]
        if sy<ty:
            lane = 294 + (number % 3)*12
            exit_fraction=0.35 if number%2 else 0.65
            points = ET.SubElement(geometry, "Array", **{"as":"points"})
            if target==3:
                route=[(sx+224*exit_fraction,lane),(282,lane),(282,ty+44)]
                ending="entryX=1;entryY=0.3;"
            else:
                entry_fraction=0.35 if number%2 else 0.65
                route=[(sx+224*exit_fraction,lane),(tx+224*entry_fraction,lane)]
                ending=f"entryX={entry_fraction};entryY=0;"
            for x,y in route:
                ET.SubElement(points,"mxPoint",x=str(x),y=str(y))
            cell.set("style", style+f"exitX={exit_fraction};exitY=1;"+ending)
        elif sy == ty:
            if abs(sx-tx)>262:
                points=ET.SubElement(geometry,"Array",**{"as":"points"})
                for x,y in [(sx+145,332),(tx+145,332)]:
                    ET.SubElement(points,"mxPoint",x=str(x),y=str(y))
                cell.set("style",style+"exitX=0.65;exitY=0;entryX=0.65;entryY=0;")
            else:
                cell.set("style", style + ("exitX=1;exitY=0.5;entryX=0;entryY=0.5;" if sx<tx
                                          else "exitX=0;exitY=0.5;entryX=1;entryY=0.5;"))
        else:
            cell.set("style",style+"exitX=0.8;exitY=0;entryX=0.8;entryY=1;")
    return cell


def make_pitch(name):
    data = DATA[name]
    tree, root = start(name)
    text(root,"title",data["title"],(40,38,747,50),24,"#272320",True)
    for i,which in enumerate(data["pitch"]):
        n=data["nodes"][which]
        color,fill=COLORS[i]
        vertex(root,f"n{i}",n["title"],(45+i*260,230,214,104),
               f"rounded=1;arcSize=16;whiteSpace=wrap;html=1;fillColor=#fdfbf8;"
               f"strokeColor={color};fontFamily=Segoe UI;fontSize=19;fontColor=#272320;")
    edge(root,"p0",0,1,"",True)
    edge(root,"p1",1,2,"",True)
    text(root,"takeaway",data["takeaway"],(45,410,735,62),17)
    return tree


def make_upgrade(name):
    data=DATA[name]
    tree,root=start(name)
    text(root,"title",data["title"],(32,22,763,35),24,"#272320",True)
    text(root,"takeaway",data["takeaway"],(32,62,763,32),13)
    for j,title in enumerate(data["groups"]):
        y=104+j*218
        vertex(root,f"boundary{j}","",(24,y,779,178),
               f"rounded=1;arcSize=16;fillColor={'#f8f4f1' if j==0 else '#e7e1dc'};"
               "strokeColor=#e2ddd9;strokeWidth=1;")
        text(root,f"group-title{j}",title,(40,y+2,224,19),9,"#3f3935",True)
    for i,n in enumerate(data["nodes"]):
        x,y=POSITIONS[i]
        color,fill=COLORS[i]
        parent=f"n{i}"
        vertex(root,parent,"",(x,y,224,146),
               "rounded=1;absoluteArcSize=1;arcSize=16;whiteSpace=wrap;html=1;"
               "fillColor=#fdfbf8;strokeColor=#ece7e3;strokeWidth=1;shadow=1;")
        vertex(root,parent+"-accent","",(0,12,5,122),
               f"rounded=1;arcSize=100;fillColor={color};strokeColor=none;",parent)
        shape,classification=SYMBOLS[n["symbol"]]
        vertex(root,parent+"-icon","",(14,15,26,28),
               f"shape={shape};html=1;fillColor={color if n['symbol'] in ['pod','service'] else fill};strokeColor={color};strokeWidth=1.5;size=6;",parent)
        text(root,parent+"-title",n["title"],(49,10,165,38),16,"#272320",True,parent)
        text(root,parent+"-subtitle",n["subtitle"],(14,52,196,20),12,"#3f3935",False,parent)
        for row,line in enumerate(n["lines"]):
            text(root,parent+f"-fact{row}",line,(14,74+row*18,198,18),12,"#635c57",False,parent)
        text(root,parent+"-metadata",n["meta"],(14,110,199,17),10,"#746d68",False,parent,True)
        vertex(root,parent+"-badge",n["badge"],(14,128,100,15),
               f"rounded=1;arcSize=100;whiteSpace=wrap;html=1;fillColor={fill};strokeColor=none;"
               f"fontColor={color};fontFamily=Segoe UI;fontSize=9;fontStyle=1;align=center;",parent)
    for i,(s,t,label) in enumerate(data["edges"]):
        edge(root,f"e{i}",s,t,label)
        text(root,f"relation-{i}",f"{i+1}  {label}",(40+(i%3)*262,506+(i//3)*17,248,16),10,"#3f3935")
    vertex(root,"assurance-band","",(24,546,779,31),
           "rounded=1;arcSize=16;fillColor=#f9e2ae;strokeColor=#e2ddd9;")
    text(root,"assurance",data["footer"],(36,548,752,27),11,"#635c57")
    return tree


def write_source(tree,path):
    ET.indent(tree,space="  ")
    tree.write(path,encoding="utf-8",xml_declaration=True)


def export(path):
    output=path.with_suffix(".png")
    subprocess.run([str(CLI),"--export","--format","png","--border","16","--scale","2",
                    "--output",str(output),str(path)],check=True,capture_output=True)
    from PIL import Image
    image=Image.open(output)
    print_view=output.with_name(output.stem+"-print.png")
    scaled=image.resize((image.width//2,image.height//2),Image.Resampling.LANCZOS)
    page=Image.new("RGB",(827,583),"#efeae7")
    page.paste(scaled,((827-scaled.width)//2,(583-scaled.height)//2))
    page.save(print_view)
    print(f"{output.parent.name}: {output.name} ({image.width}x{image.height})",flush=True)


def research_record(name):
    data=DATA[name]
    lines=[f"# {name}: evidence and exclusive claim","",
           "Owner: assigned implementation agent; disposition: redesign (provider admission: new).",
           "Scope: this name's review directory, canonical draw.io, PNG/hash, and retired legacy source only.",
           "Global inventories and document pages are deliberately not modified.",
           "",f"Target page: `{data['page']}`",f"Takeaway: {data['takeaway']}",
           "Orientation: A5 landscape, 827 × 583 units at 100 units/inch.",
           "", "## Research provenance",
           "The coordinator reports exactly three independent bounded GPT-6 Astra research threads completed before this assignment: components, flows, assurance. No additional research agents were launched.",
           "Their supplied reconciled findings are used below and checked against local source. Original tool-output transcripts were supplied at temporary paths and were not accessed; no claim is made to preserve those full transcripts here.",
           "", "## Reconciled assurance findings",
           "- No GitHubLegacy or raw GitHub platform authentication. Endpoint metadata selects internal/run/broker/cookie; otherwise Entra. Broker eligibility includes AuthenticatedSelfOrMcp. Authentication precedes persisted resource authorization.",
           "- MCP requires exact issuer and single audience, keyed RS256, lifetime, subject and mcp:invoke. Assistant uses a separate five-minute broker plus renewal. Idle is resumable; Completed is sealed.",
           "- Current /configure delivers RepositoryAccessToken into AgentHost and tool options. Constrained direct git status / allowlisted gh get process-scoped credentials, not blanket shell injection. This does not meet the normative no-credential contract. No Key Vault-role AgentHost identity.",
           "- Native shell denial, URL approval and custom reporting bypass are distinct paths. Remaining governance requires both AGT and direct containment.",
           "- AKS base counts: API 2, frontend 2, MCP 1, worker 2; worker HPA 2–3. API/worker use Postgres and CSI; MCP has no CSI. Application and preview gateways are distinct.",
           "- Preview provisioning is followed by exact HTTPS URL probing before ready, rollback on failure, and no API direct TCP readiness probe.",
           "- Memory candidates converge into importance/recency joint sorting. Budgets cover selected memories, not the entire context. Output is explicitly untrusted JSON.",
           "- Provider prepare binds signed five-minute operation/project/subject/provider key. Accept re-resolves; mismatch returns replacement context. Private run snapshots use database ownership plus secret storage; invocation guard and live capability fences are separate.",
           "", "## Scope qualifications",
           "- Authorization overview: persisted membership applies to resource checks; explicitly permitted trusted internal-service endpoints can bypass that lookup (ProjectAuthorization.cs:56-85). The diagram is not a claim that every internal call queries project membership.",
           "- Provider invocation: operation matching means the supported operation/model-provider boundary and expected provider identity, not a claim that RunModelInvocationGuard literally compares operation-name strings. RunOrchestrator.cs:826-855 selects the accepted operation; RunModelInvocationGuard.cs:11-48 checks the durable boundary and live Copilot capability.",
           "- API/store arrows describe the API's append/reload operation, not a claim that returned history travels from API into storage. Request/response exchanges are aggregated; no unshown reverse connector is implied to exist.",
           "- Native networking router glyphs identify Gateway API routing, not Azure Application Gateway. Durable services is an explicitly labeled aggregation of Postgres and Azure Files, not a claim that Azure Files is a database.",
           "", "## Node and symbol evidence"]
    for i,n in enumerate(data["nodes"]):
        lines.append(f"- n{i} **{n['title']}** — `{SYMBOLS[n['symbol']][1]}`; {n['evidence']}")
    lines+=["","## Every connector's evidence map"]
    for i,(s,t,label) in enumerate(data["edges"]):
        lines.append(f"- e{i}: n{s} → n{t}: {label}. Evidence: {data['nodes'][s]['evidence']}; {data['nodes'][t]['evidence']}")
    lines+=["","## Visual sources and rights",
            "Started from `docs/diagrams/drawio/fluent-template.drawio`; loaded all four JSON entries from `fluent-library.xml`. The custom card entry contains malformed embedded XML (an unescaped closing div), so it is used as a style reference, not imported as executable XML. The valid template supplies editable primitives. Removed the template's invisible metadata vertex and all template example content; resized to true A5.",
            "Warm Fluent tones, Segoe UI, rounded 16-unit near-white cards, 5-unit semantic accents, tiered boundaries, metadata and badges derive from the committed design-system assets. Native draw.io bundled UML, Kubernetes, Azure, database and flowchart shapes remain editable. No downloaded logos or third-party raster assets.",
            "Native symbols are bundled with the pinned draw.io Desktop 31.4.5 distribution (https://github.com/jgraph/drawio-desktop; Apache-2.0 application license); vendor marks remain their owners' marks and are used solely to identify the represented technology.",
            "", "## Preconditions and operation notes",
            "Shared dirty issue worktree was explicitly assigned. No git mutations, commits, document updates, global inventory writes, or pipeline/product edits are performed.",
            "The skill was read directly from the worktree because it is not registered in this runtime's skill catalog.",
            "An initial library-loading attempt decoded XML before JSON and failed; parsing the raw mxlibrary JSON first resolves it without modifying the shared library.",
            ""]
    return "\n".join(lines)


def correct_pass_two(tree,name):
    cells={c.get("id"):c for c in tree.getroot().iter("mxCell")}
    for ident,cell in cells.items():
        if ident.endswith("-icon"):
            cell.set("style",cell.get("style").replace("size=6;",""))
    for ident,gutter,fraction in (
        [("e1",290,0.44),("e2",276,0.24)] if name=="auth-security-fig4" else
        [("e2",276,0.24),("e3",290,0.44)] if name=="sandbox-browser-preview-fig1" else []):
        cell=cells[ident]
        cell.set("style",cell.get("style").replace("entryY=0.3;",f"entryY={fraction};"))
        points=cell.find("mxGeometry/Array").findall("mxPoint")
        points[1].set("x",str(gutter))
        points[2].set("x",str(gutter))
        points[2].set("y",str(344+146*fraction))
    if name=="canonical-aks-components":
        cell=cells["e3"]
        cell.set("style",cell.get("style").replace("exitX=0.65;exitY=0;entryX=0.65;entryY=0;",
                                                   "exitX=1;exitY=0.3;entryX=0.85;entryY=0;"))
        array=cell.find("mxGeometry/Array")
        array.clear()
        for x,y in [(282,387.8),(282,300),(754.4,300)]:
            ET.SubElement(array,"mxPoint",x=str(x),y=str(y))
    return tree


def main():
    p=argparse.ArgumentParser()
    p.add_argument("phase",choices=["pitch","upgrade","export","copy-pass"])
    p.add_argument("--name",action="append")
    p.add_argument("--pass-number",type=int)
    args=p.parse_args()
    for name in args.name or DATA:
        if name not in DATA:
            raise ValueError("outside authorized names")
        folder=REVIEWS/name
        folder.mkdir(parents=True,exist_ok=True)
        if args.phase=="pitch":
            (folder/"evidence.md").write_text(research_record(name),encoding="utf-8")
            file=folder/f"{name}-pitch.drawio"
            write_source(make_pitch(name),file)
            export(file)
        elif args.phase=="upgrade":
            file=folder/f"{name}-pass-01.drawio"
            write_source(make_upgrade(name),file)
            export(file)
        elif args.phase=="copy-pass":
            number=args.pass_number
            if not number or number<2:
                raise ValueError("correction passes start at two")
            before=folder/f"{name}-pass-{number-1:02}.drawio"
            file=folder/f"{name}-pass-{number:02}.drawio"
            if file.exists():
                raise ValueError("never overwrite a pass")
            tree=ET.parse(before)
            if number==2:
                tree=correct_pass_two(tree,name)
            if number==3:
                cells={c.get("id"):c for c in tree.getroot().iter("mxCell")}
                if name=="canonical-aks-components":
                    cells["e3"].find("mxGeometry/Array").set("as","points")
                if name=="auth-security-fig4":
                    cells["e1"].find("mxGeometry").set("x","-0.6")
            write_source(tree,file)
            export(file)
        elif args.phase=="export":
            suffix=f"pass-{args.pass_number:02}" if args.pass_number else "pitch"
            export(folder/f"{name}-{suffix}.drawio")


if __name__=="__main__":
    main()
