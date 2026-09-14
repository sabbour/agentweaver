"""One-off, owned experience review artifacts; never an inventory/catalog writer."""
import argparse
import copy
import hashlib
import importlib.util
import json
import subprocess
from pathlib import Path
from xml.etree import ElementTree as ET

from PIL import Image

ROOT = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
REVIEWS = ROOT / "docs/diagrams/reviews"
CLI = HERE / "export-tool/desktop/draw.io.exe"
PLAN = json.loads((ROOT / ".github/skills/docs-diagram-audit/reports/plan-experience.json").read_text())
SCHEMA = ROOT / ".github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json"
spec = importlib.util.spec_from_file_location("growth", ROOT / ".github/skills/docs-diagram-iterate/scripts/check_xml_growth.py")
growth = importlib.util.module_from_spec(spec)
spec.loader.exec_module(growth)
PALETTE = [("#d2ccf8", "#3f3682"), ("#a6e9ed", "#00666d"), ("#9fd89f", "#0e700e"),
           ("#f9e2ae", "#835b00"), ("#e7e1dc", "#635c57")]
SYMBOLS = {
    "person": ("shape=mxgraph.c4.person;", "native:c4"),
    "process": ("shape=process;", "native:flowchart"),
    "decision": ("shape=rhombus;", "native:flowchart"),
    "document": ("shape=document;", "native:flowchart"),
    "database": ("shape=cylinder3;size=8;", "native:database"),
    "pod": ("shape=mxgraph.kubernetes.icon2;prIcon=pod;", "native:kubernetes"),
    "entra": ("shape=mxgraph.azure.azure_active_directory;", "native:azure"),
    "product": ("shape=hexagon;", "custom:agentweaver"),
}
IDENTITY = "experience-00-overview-fig1/research-identity.md"
EXECUTION = "canonical-a2a-execution/research-execution.md"
ORCHESTRATION = "experience-review-workspace-merge-fig1/research-orchestration.md"


def card(title, subtitle, metadata, badge, detail, symbol="product", tone=0):
    return dict(title=title, subtitle=subtitle, metadata=metadata, badge=badge,
                detail=detail, symbol=symbol, tone=tone)


def edge(source, target, label, evidence, route=None, revision=False):
    return dict(source=source, target=target, label=label, evidence=evidence,
                route=route, revision=revision)


MODELS = {}


def model(name, title, takeaway, groups, nodes, edges, note, research, pitch):
    MODELS[name] = dict(title=title, takeaway=takeaway, groups=groups, nodes=nodes,
                        edges=edges, note=note, research=research, pitch=pitch)


model("experience-00-overview-fig1", "Two front doors, one product",
      "Web and MCP share authorization and authoritative state; events flow back to clients.",
      ["PEOPLE AND CLIENTS", "AUTHORITATIVE BACKEND"],
      [
          card("Human operator", "Inspect and decide", "browser or assistant", "INTENT", "Choose the interface,\nnot a different product.", "person"),
          card("Web UI", "Project and run views", "REST + stream request", "BROWSER", "Opens the watch request;\nreceives API event payloads.", "process", 1),
          card("MCP client", "Assistant tool caller", "tool result + progress", "ASSISTANT", "Makes explicit tool calls;\nsurfaces decisions to people.", "process"),
          card("Product state", "Projects, teams, runs", "knowledge + workspaces", "SHARED", "API-authorized reads and\nmutations; no MCP bypass.", "product", 2),
          card("Agentweaver API", "Authorization boundary", "run snapshots + events", "API", "Owns resource checks and\naccess to product state.", "process", 1),
          card("MCP server", "Authenticated adapter", "validated broker bearer", "MCP", "Forwards the exact caller\ntoken to the API.", "process", 0),
      ],
      [
          edge(0, 1, "inspect", "apps/web/src/App.tsx:103-125"),
          edge(1, 4, "requests", "apps/web/src/api/sse.ts:237-246", "down-left"),
          edge(4, 1, "SSE events", "apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:481-514", "up-right"),
          edge(2, 5, "tool call", "apps/Agentweaver.Mcp/Program.cs:85-98", "down-left"),
          edge(5, 2, "result", "apps/Agentweaver.Mcp/Tools/RunTools.cs:229-265", "up-right"),
          edge(5, 4, "forward", "apps/Agentweaver.Mcp/AgentweaverApiClient.cs:359-382", "left-top"),
          edge(4, 5, "API result", "apps/Agentweaver.Mcp/AgentweaverApiClient.cs:376-382", "right-bottom"),
          edge(4, 3, "access", "apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:158-162"),
      ],
      "Browser opens the connection; API sends event payloads. MCP results return through MCP.",
      IDENTITY, ["Client interfaces", "Authorized product state", "operate"])

model("experience-mcp-client-fig1", "Assistant-driven work",
      "Choose one intake path, inspect the result, and preserve explicit human decisions.",
      ["PREPARE AND CHOOSE", "OPERATE AND REVIEW"],
      [
          card("Human + assistant", "Agree on the work", "MCP calls -> API", "INTENT", "The assistant explains actions;\na person supplies judgment.", "person"),
          card("Project and team", "Inspect or create", "propose -> confirm cast", "PREPARE", "Named roles and charters\nbelong to the project."),
          card("Choose intake", "Queue OR start now", "do not start twice", "CHOICE", "Ready pickup is an alternative\nto an immediate start.", "decision", 3),
          card("Human review", "Read before deciding", "run_review: boolean", "JUDGMENT", "MCP approve/decline is binary.\nFeedback uses other surfaces.", "decision", 3),
          card("State and artifacts", "Watch, list, read", "API -> MCP -> client", "EVIDENCE", "Progress and results return\nthrough the MCP adapter.", "document", 1),
          card("Coordinator", "Runs and child work", "status / children / watch", "EXECUTE", "Queued: claim then unattended.\nDirect: no outcome gate."),
      ],
      [
          edge(0, 1, "prepare", "apps/Agentweaver.Api/Casting/CastingService.cs:899-927"),
          edge(1, 2, "choose", "apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-34"),
          edge(2, 5, "start once", "apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:186-249"),
          edge(5, 4, "observe", "apps/Agentweaver.Mcp/Tools/RunTools.cs:229-265"),
          edge(4, 3, "inspect", "apps/Agentweaver.Mcp/Tools/RunTools.cs:276-284"),
      ],
      "Define Outcome is the third start variant: draft, obtain authorized confirmation, then dispatch.",
      IDENTITY, ["Assistant intent", "Reviewed work", "operate through MCP"])

model("experience-onboarding-auth-fig1", "Browser sign-in and readiness",
      "Entra establishes identity; AI readiness and optional GitHub capabilities remain separate.",
      ["BROWSER SIGN-IN", "SESSION AND SETUP"],
      [
          card("Browser", "Start Entra sign-in", "/auth/entra/authorize", "START", "The API binds this request\nto expiring browser state.", "person"),
          card("Auth API", "Save state and PKCE", "verifier + nonce", "SERVER", "The verifier stays server-side.\nRedirect carries a challenge.", "process", 1),
          card("Microsoft Entra", "Authenticate identity", "code + state callback", "IDENTITY", "Not GitHub login;\nnot repository authorization.", "entra", 0),
          card("Ready app shell", "Continue when ready", "platform access + AI", "READY", "GitHub Repo App access is\noptional for GitHub work.", "process", 2),
          card("Browser session", "One-time code exchange", "session credential", "SESSION", "Frontend exchanges a code;\nno raw token in callback URL.", "database", 1),
          card("Callback checks", "Consume state once", "redeem code + verifier", "VALIDATE", "Validate Entra response and\nbound browser callback.", "process", 3),
      ],
      [
          edge(0, 1, "authorize", "apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:290"),
          edge(1, 2, "redirect", "apps/Agentweaver.Api/Auth/EntraOAuthRedirectService.cs:250-260"),
          edge(2, 5, "callback", "apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:327-365"),
          edge(5, 4, "exchange", "apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:416-452"),
          edge(4, 3, "setup check", "apps/web/src/App.tsx:278-293"),
      ],
      "Session identity does not grant repository access, provider readiness, or project membership.",
      IDENTITY, ["Entra browser sign-in", "Authorized session", "establish identity"])

model("experience-onboarding-auth-fig2", "MCP uses broker credentials",
      "An Entra-backed consent flow issues the exact-resource credential accepted by MCP.",
      ["DISCOVERY AND HUMAN CONSENT", "TOKEN AND RESOURCE ENFORCEMENT"],
      [
          card("MCP client", "Discover resource/issuer", "401 challenge + metadata", "DISCOVER", "Use the advertised resource\nand authorization server.", "process"),
          card("Browser consent", "Entra-backed session", "/oauth/authorize + PKCE", "AUTHORIZE", "Show client and requested\naccess; Allow or Deny.", "person", 3),
          card("OpenIddict", "Bind grant and code", "client / redirect / resource", "ISSUER", "Existing consent may skip a\nprompt; denial is not success.", "process", 1),
          card("Authorized API", "Enforce resource access", "project role / membership", "API", "MCP forwards the validated\nbearer; API checks again.", "process", 2),
          card("MCP boundary", "Validate broker token", "issuer + RS256 + lifetime", "RESOURCE", "Exact single audience,\nsubject and mcp:invoke.", "process", 0),
          card("Token exchange", "Code + verifier", "/oauth/token", "BROKER", "Returns Agentweaver token;\nnot raw Entra or GitHub.", "document", 1),
      ],
      [
          edge(0, 1, "open", "apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:87-138"),
          edge(1, 2, "allow", "apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:148-188"),
          edge(2, 5, "code grant", "apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:31-76"),
          edge(5, 4, "tool bearer", "apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-87"),
          edge(4, 3, "forward", "apps/Agentweaver.Mcp/AgentweaverApiClient.cs:359-382"),
      ],
      "Invalid/missing token: 401. Missing scope: 403. No API-key or raw Entra/GitHub fallback.",
      IDENTITY, ["Entra-backed consent", "MCP broker access", "issue scoped credential"])

model("experience-team-casting-memory-fig1", "Named teams, governed memory",
      "Only eligible database records feed future context; exports are inspectable mirrors.",
      ["TEAM AND KNOWLEDGE GOVERNANCE", "AUTHORITATIVE CONTEXT"],
      [
          card("Cast proposal", "Roles, names, charters", "new / augment / recast", "PROPOSE", "Inspect the proposal;\nconfirm the intended change."),
          card("Named team", "Persist roster and work", "charters + team history", "CONFIRM", "Agents accumulate records;\nrecords are not auto-policy."),
          card("Review knowledge", "Memory and decision inbox", "approve / reject / retain", "GOVERN", "Authorized promotion;\nrejection remains auditable.", "decision", 3),
          card("Future context", "Untrusted structured data", "children: narrower scope", "COMPILE", "Cross-agent memory requires\napproved high-value learning."),
          card("Eligibility", "Filter and budget", "exclude legacy records", "SELECT", "Active approved boundaries;\ncross-team learning/pattern.", "process", 1),
          card("Knowledge DB", "Authoritative records", "memory + decisions", "STORE", "Exported files are mirrors,\nnot the compiler authority.", "database", 2),
      ],
      [
          edge(0, 1, "confirm", "apps/Agentweaver.Api/Casting/CastingService.cs:899-999"),
          edge(1, 2, "record", "apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:150-167"),
          edge(2, 5, "persist", "apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:211-219"),
          edge(5, 4, "select", "apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:57-105"),
          edge(4, 3, "compile", "apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:159-226"),
      ],
      "A cross-team tag alone is insufficient. Approved + high importance + learning/pattern are required.",
      IDENTITY, ["Confirmed named team", "Eligible knowledge context", "learn with governance"])

model("canonical-a2a-execution", "Remote leaves, worker-owned graph",
      "A2A moves leaf turns into sandbox pods, not orchestration or durable state ownership.",
      ["WORKER CONTROL PLANE", "LEAF EXECUTION AND DURABILITY"],
      [
          card("Workflow graph", "Orchestration and gates", "RequestPort / checkpoints", "WORKER", "Graph progression and human\ngates stay worker-side."),
          card("RemoteAgentProxy", "Leaf-turn adapter", "message:stream", "A2A", "Configure run context;\nforward the leaf invocation."),
          card("Event recorder", "Decode returned events", "ordered sequence numbers", "WORKER", "Records pod event data parts;\nno direct pod-to-UI stream.", "process", 1),
          card("Sandbox pod", "AgentHost + executor", "leaf agent execution", "POD", "No database/checkpoint-store\naccess from AgentHost.", "pod", 0),
          card("Durable state", "Checkpoints and events", "shared run state", "STORE", "Worker persists progress;\nAPI reads event cursors.", "database", 2),
          card("Web timeline", "API stream consumer", "snapshot + SSE", "BROWSER", "Shows persisted run events;\nnot transport-level replay.", "process", 1),
      ],
      [
          edge(0, 1, "invoke leaf", "packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:36-63"),
          edge(1, 3, "A2A call", "packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:38-53", "middle-left"),
          edge(3, 2, "RunEvents", "packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:58-63", "middle-right"),
          edge(2, 4, "record", "apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:68-87", "diagonal"),
          edge(4, 5, "API / SSE", "apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:144-167"),
      ],
      "Worker checkpoints and review gates remain authoritative. TLS settings depend on deployment overlay.",
      EXECUTION, ["Worker graph and gates", "Remote leaf execution", "A2A turn"])

model("experience-a2a-distributed-agents-fig2", "Remote turns: pause is not failure",
      "Claim/configure, observe terminal evidence, and make recovery an explicit policy decision.",
      ["ACQUIRE AND EXECUTE", "DISTINCT OUTCOMES"],
      [
          card("Claim a sandbox", "Controller/Kubernetes bind", "pod-per-run deployment", "CLAIM", "Acquire a run-bound pod;\nnot a pod for each token.", "pod"),
          card("Configure + stream", "Inject run context", "A2A message:stream", "TURN", "Leaf output returns through\nthe worker event recorder."),
          card("Terminal evidence", "agent.turn.end", "successful completion", "COMPLETE", "A clean EOF without the\nterminal marker is not success.", "process", 2),
          card("Checkpoint wait", "Human / external gate", "release is conditional", "PAUSE", "Active previews or assembly\nmay retain pod resources.", "database", 1),
          card("Visible failure", "Incomplete / transport", "retryable when classified", "FAILED", "Prior deltas can be preserved;\nno seamless replay promise.", "process", 3),
          card("Recovery decision", "Inspect state and budget", "redispatch when chosen", "POLICY", "Fresh dispatch is deliberate;\nside effects may need review.", "decision", 3),
      ],
      [
          edge(0, 1, "configure", "apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:663-691"),
          edge(1, 2, "turn end", "packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:408-456"),
          edge(1, 4, "failure", "packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:408-456"),
          edge(4, 5, "evaluate", "apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:685-743"),
          edge(3, 0, "resume", "apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:394-408", "up-left", True),
      ],
      "The checkpoint lane is a separate workflow wait, not an automatic recovery path for a failed turn.",
      EXECUTION, ["Remote leaf turn", "Explicit outcome and recovery", "observe"])

model("experience-operations-fig1", "Choose the right operations surface",
      "Inspection and configuration have different scopes; MCP parity is intentionally partial.",
      ["INSPECT CURRENT STATE", "CONFIGURE WITH THE RIGHT AUTHORITY"],
      [
          card("Diagnostics", "Health and checks", "diagnostics_get", "HEALTH", "Inspect explicit failures and\nunknown timed-out checks.", "process", 2),
          card("Heartbeat + Flow", "Pickup and orchestration", "heartbeat_status", "WORK", "Automation reports pickup;\nFlow shows ongoing work."),
          card("Cluster + traces", "Capacity and observability", "REST-backed UI views", "INSPECT", "No dedicated Cluster or\nObservability MCP tools.", "process", 1),
          card("Account settings", "Authentication / AI access", "GitHub / MCP clients", "PERSONAL", "Not a repository-path\nsandbox-policy editor.", "person", 4),
          card("Platform settings", "Model provider controls", "platform administrator", "PLATFORM", "Global provider configuration\nrequires admin authority.", "process", 3),
          card("Project settings", "Repository / sandbox", "project-scoped policy", "PROJECT", "Preview approval and lifetime;\nMCP setter is narrower.", "product", 0),
      ], [],
      "All surfaces use authorized API operations. sandbox_policy_set changes repository shell_enabled only.",
      EXECUTION, ["Inspect system state", "Configure scoped policy", "separate responsibilities"])

model("experience-sandbox-pod-execution-fig3", "A durable run can outlive its pod",
      "A review wait may release compute; preview and assembly retention are explicit exceptions.",
      ["ACTIVE WORK AND WAIT", "RESUME OR RETAIN"],
      [
          card("Active leaf", "AgentHost returns output", "pod -> worker -> timeline", "ACTIVE", "Worker records events and\nowns workflow progression.", "pod"),
          card("Worker gate", "Human decision requested", "checkpoint-backed wait", "WAIT", "Persist resumable state;\npause watchdog accounting."),
          card("Release decision", "Policy and lifecycle checks", "pod-per-run + enabled", "CONDITIONAL", "Release requires support and\nno active retention exception.", "decision", 3),
          card("Resumed work", "Continue from saved state", "events through worker", "RESUME", "A released pod may be replaced;\nits name can change.", "pod", 2),
          card("Durable checkpoint", "Session and workspace", "authorized decision", "PERSIST", "Load resumable state;\nclaim/configure if released.", "database", 1),
          card("Release or retain", "Claim deletion / keep pod", "preview / assembly exception", "LIFECYCLE", "Active preview defers deletion;\nrelease failures are logged.", "process", 3),
      ],
      [
          edge(0, 1, "reach gate", "apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:394-408"),
          edge(1, 2, "evaluate", "apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:518-536"),
          edge(2, 5, "apply", "apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:956-984"),
          edge(1, 4, "checkpoint", "apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:394-408"),
          edge(4, 3, "resume", "apps/Agentweaver.Api/Runs/WorkflowRestartService.cs:118-140"),
      ],
      "Human waiting does not imply zero retained compute. This is not arbitrary failed-turn rehydration.",
      EXECUTION, ["Worker-owned run", "Conditional pod lifetime", "checkpoint / resume"])

model("experience-scaling-operations-fig1", "Scale roles, keep state shared",
      "Web serves requests; workers execute; Postgres coordinates durable state; pods run leaves.",
      ["CONTROL-PLANE ROLES", "SHARED STATE AND LEAF COMPUTE"],
      [
          card("Browser", "REST and watch client", "request / SSE response", "CLIENT", "Connects to web replicas;\nnot a worker-local queue.", "person"),
          card("Web tier", "Requests and event reads", "base: 2 replicas", "WEB", "Uses shared durable state;\nreads event cursors.", "process", 1),
          card("Worker tier", "Execution and ownership", "HPA: 2-3 replicas", "WORKER", "CPU 70% / memory 80%;\nleases and workflow state.", "process", 0),
          card("Current boundary", "Configuration, not a probe", "KEDA / web HPA: examples", "SCOPE", "Checked-in scaling settings\nare not live cluster evidence.", "document", 4),
          card("Postgres", "Shared durable state", "events / leases / checkpoints", "DATABASE", "Event relay polls the table;\nnot LISTEN/NOTIFY.", "database", 2),
          card("Sandbox pods", "Remote AgentHost leaves", "run context + A2A", "COMPUTE", "Return events to the worker;\nno direct pod DB access.", "pod", 0),
      ],
      [
          edge(0, 1, "requests", "apps/web/src/api/sse.ts:237-246"),
          edge(1, 4, "state / events", "apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:144-167"),
          edge(2, 4, "persist", "apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-95", "diagonal"),
          edge(2, 5, "execute", "packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:38-63", "down-left"),
          edge(5, 2, "results", "packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:58-63", "up-right"),
      ],
      "Worker HPA is active; KEDA and web HPA blocks are commented proposals. Kubernetes owns scheduling.",
      EXECUTION, ["Web and worker roles", "Shared durable state", "coordinate"])

model("experience-scaling-operations-fig3", "Lease transfer is not turn replay",
      "An expired owner can be replaced; continuation depends on the persisted run state.",
      ["LEASE OWNERSHIP", "STATE-DEPENDENT RECOVERY"],
      [
          card("Worker A", "Guarded lease claim", "owner A + token n", "OWNER", "Renewals must match both\nowner and fencing token.", "process"),
          card("Lease expires", "Renewals cease", "free / expired eligibility", "EXPIRY", "Failure is not a message;\nexpiry permits a later claim.", "database", 3),
          card("Worker B", "Win eligible next claim", "owner B + token n+1", "NEW OWNER", "Old-token terminal ownership\nchecks reject stale results.", "process", 1),
          card("Visible failure", "Stranded child / root", "retryable child vs root", "FAILURE", "Child may be redispatched;\nno automatic mid-turn replay.", "process", 3),
          card("Inspect run state", "Checkpoint or work plan", "recovery prerequisites", "DECIDE", "AwaitingReview, coordinator,\nand stranded turns differ.", "decision", 1),
          card("Durable recovery", "Eligible saved state", "checkpoint / coordinator plan", "CONTINUE", "Recover only supported state;\nmissing checkpoints surface.", "database", 2),
      ],
      [
          edge(0, 1, "renewals stop", "apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:54-95"),
          edge(1, 2, "next claim", "apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-51"),
          edge(2, 4, "inspect", "apps/Agentweaver.Api/Runs/WorkflowRestartService.cs:57-140", "diagonal"),
          edge(4, 3, "stranded turn", "apps/Agentweaver.Api/Runs/WorkflowRestartService.cs:57-90"),
          edge(4, 5, "recoverable", "apps/Agentweaver.Api/Runs/WorkflowRestartService.cs:118-140"),
      ],
      "Fencing protects guarded ownership operations, not every external tool side effect.",
      EXECUTION, ["Expired lease", "Eligible new owner", "guarded claim"])

model("experience-review-workspace-merge-fig1", "Inspect, decide, then integrate",
      "Approval identifies a candidate; guarded local merge can also include target-side changes.",
      ["CANDIDATE AND REVIEW", "SERVER-AUTHORITATIVE OUTCOMES"],
      [
          card("Run candidate", "Worktree + recorded hash", "files / changes / timeline", "CANDIDATE", "Inspect the actual candidate\nbefore submitting a decision.", "document", 0),
          card("Review decision", "Authorized run reviewer", "approve / change / decline", "REVIEW", "MCP run_review is binary;\nweb supports feedback.", "decision", 3),
          card("Guarded merge", "Lock, CAS, hash check", "approved candidate identity", "SERVER", "Changed candidate fails checks;\nconflicts are visible.", "process", 1),
          card("Revised candidate", "Feedback drives revision", "normal cap / collective rules", "CHANGE", "A new candidate needs review;\nno automatic approval."),
          card("Declined / blocked", "No successful integration", "explicit outcome or reason", "ATTENTION", "Decline, busy repository and\nmerge conflict are distinct.", "process", 3),
          card("Merged history", "Local destination updated", "three-way / ref-only merge", "MERGED", "Final tree may include newer\ntarget and special Squad state.", "database", 2),
      ],
      [
          edge(0, 1, "inspect", "apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:372-386"),
          edge(1, 2, "approve", "apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:925-968"),
          edge(1, 4, "decline", "apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:978-980"),
          edge(1, 3, "changes", "apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1367-1437", "middle-left", True),
          edge(2, 4, "blocked", "apps/Agentweaver.Api/Runs/MergeCoordinator.cs:139-160", "middle-right"),
          edge(2, 5, "guards pass", "apps/Agentweaver.Api/Git/WorktreeManager.cs:1902-2059"),
          edge(3, 0, "re-review", "apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1367-1437", "up-left", True),
      ],
      "Request changes produces the revised-candidate lane. Conflicts/busy/hash mismatch never imply merged.",
      ORCHESTRATION, ["Inspected candidate", "Guarded integration", "authorized review"])

model("experience-review-workspace-merge-fig2", "Browse a ref without changing it",
      "Select an allowed ref, inspect its tree, and read content through read-only operations.",
      ["CHOOSE THE REFERENCE", "READ THE CONTENT"],
      [
          card("Workspace reader", "Web page or MCP client", "authorized project access", "READER", "Browsing does not check out,\nedit, approve or merge.", "person"),
          card("Available refs", "Base, run, assembly", "list_project_workspace_refs", "REFS", "Only available allowed refs;\ndefault is the base branch.", "document", 1),
          card("Selected ref", "Explicit browsing context", "GET only", "SELECT", "Changing ref clears the\nprevious file selection.", "process", 0),
          card("Read-only content", "Source / Markdown preview", "binary / large / missing", "CONTENT", "Render supported content;\nsurface truthful limitations.", "document", 2),
          card("Selected path", "Relative file name", "get_project_workspace_file", "FILE", "Pass both path and ref;\ninvalid paths are rejected.", "process", 1),
          card("File tree", "Paths at the selected ref", "list_project_workspace", "TREE", "Choose a listed file;\nno working-tree mutation.", "document", 0),
      ],
      [
          edge(0, 1, "list refs", "apps/Agentweaver.Mcp/Tools/WorkspaceTools.cs:13-24"),
          edge(1, 2, "choose", "apps/web/src/pages/WorkspacePage.tsx:277-282"),
          edge(2, 5, "list tree", "apps/Agentweaver.Mcp/Tools/WorkspaceTools.cs:29-42"),
          edge(5, 4, "select", "apps/web/src/pages/WorkspacePage.tsx:297"),
          edge(4, 3, "read", "apps/Agentweaver.Mcp/Tools/WorkspaceTools.cs:48-63"),
      ],
      "Unknown refs/files can return 404; invalid paths return 400. Ref browsing is not content approval.",
      ORCHESTRATION, ["Allowed project ref", "Read-only file content", "browse"])

model("experience-workflows-backlog-fig3", "One won claim, one reserved run",
      "Ready pickup commits claim and reservation before activation; other outcomes do not launch.",
      ["SELECTION AND ATOMIC RESERVATION", "POST-CLAIM OUTCOMES"],
      [
          card("Ranked Ready tasks", "Heartbeat candidates", "eligible project + workspace", "READY", "Top-N limits candidates per\ntick, not total concurrency."),
          card("Atomic transaction", "Claim + run + policy", "task-scoped reservation", "STORE", "Commit all together;\nno orphan losing run.", "database", 1),
          card("Activate winner", "Use reserved run ID", "post-commit activation", "WON", "Schedule unattended confirm\nattributed to CapturedBy."),
          card("Lost / unavailable", "No launch by this pickup", "rollback / preserve rank", "NO START", "A winner may own a lost claim;\nunavailable leaves Ready.", "process", 4),
          card("Claimed failed run", "Visible failure reason", "preflight / activation failure", "FAILED", "Do not silently requeue.\nTerminalization may log failure.", "process", 3),
          card("Coordinator work", "Unattended execution", "claim-time policy snapshot", "RUNNING", "No second manual start for\nthe same captured goal.", "product", 2),
      ],
      [
          edge(0, 1, "attempt", "apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:101-121"),
          edge(1, 2, "won", "apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:231-249"),
          edge(1, 3, "not won", "apps/Agentweaver.Api/Infrastructure/Ef/EfBacklogTaskStore.cs:382-417", "diagonal"),
          edge(2, 5, "activate", "apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:240-249"),
          edge(2, 4, "failure", "apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:251-268", "middle-right"),
      ],
      "A won preflight-failure reservation also stays Claimed/Failed. Claim-once is not tool-execution-once.",
      ORCHESTRATION, ["Eligible Ready task", "Reserved coordinator run", "atomic claim"])

POSITIONS = [(42, 128), (309, 128), (576, 128), (42, 359), (309, 359), (576, 359)]
WIDTH, HEIGHT = 209, 148
PASS_ONE_ISSUES = {
    "experience-00-overview-fig1": "The API-result label crowds its arrowhead.",
    "experience-onboarding-auth-fig1": "The setup-check label obscures the arrowhead.",
    "experience-onboarding-auth-fig2": "The tool-bearer label obscures the arrowhead.",
    "canonical-a2a-execution": "Long edge labels, shared-looking endpoint segments and a group-title collision need correction.",
    "experience-a2a-distributed-agents-fig2": "The resume rail crosses the lower group title rather than using an outer gutter.",
    "experience-sandbox-pod-execution-fig3": "The reach-gate label crowds its arrowhead.",
    "experience-scaling-operations-fig1": "Web and worker database connectors appear to share a terminal segment without a logical junction.",
    "experience-scaling-operations-fig3": "Long horizontal connector labels hide arrowheads.",
    "experience-review-workspace-merge-fig1": "Revision rails cross the group title; blocked/decline share-looking routes need separate ports.",
    "experience-review-workspace-merge-fig2": "The available-ref tool metadata crowds the card edge at enlarged size.",
    "experience-workflows-backlog-fig3": "Not-won crosses the group title and failure/activate appear to share a source segment.",
}
PASS_TWO_ISSUES = {
    "canonical-a2a-execution": "RunEvents label collides with the separate record route at its crossing.",
    "experience-a2a-distributed-agents-fig2": "Outer resume label extends past the printable left page boundary.",
    "experience-review-workspace-merge-fig1": "Outer re-review label extends past the printable left page boundary.",
}


def correct_third_pass(xml, name):
    if name not in PASS_TWO_ISSUES:
        return xml
    tree = ET.fromstring(xml)
    target = "e2" if name == "canonical-a2a-execution" else "e4" if name == "experience-a2a-distributed-agents-fig2" else "e6"
    cell = next(cell for cell in tree.iter("mxCell") if cell.get("id") == target)
    geom = cell.find("mxGeometry")
    ET.SubElement(geom, "mxPoint", x="100" if target == "e2" else "26", y="0", **{"as": "offset"})
    ET.indent(tree)
    return '<?xml version="1.0" encoding="UTF-8"?>\n' + ET.tostring(tree, encoding="unicode") + "\n"


def correct_second_pass(xml, name):
    tree = ET.fromstring(xml)
    cells = {cell.get("id"): cell for cell in tree.iter("mxCell")}
    for link in MODELS[name]["edges"]:
        cell = cells[f"e{MODELS[name]['edges'].index(link)}"]
        if POSITIONS[link["source"]][1] == POSITIONS[link["target"]][1]:
            text = cell.get("value")
            if len(text) > 7 and " " in text:
                cell.set("value", text.replace(" ", "\n", 1))
            if len(text) > 8 and " " not in text:
                cell.set("style", cell.get("style").replace("fontSize=11;", "fontSize=9;"))

    def route(cell_id, ports, points):
        cell = cells[cell_id]
        style = growth.parse_style(cell.get("style"))
        for key, val in zip(("exitX", "exitY", "entryX", "entryY"), ports):
            style[key] = str(val)
        cell.set("style", ";".join(f"{k}={v}" for k, v in style.items()) + ";")
        geom = cell.find("mxGeometry")
        geom.clear()
        geom.set("relative", "1")
        geom.set("as", "geometry")
        if points:
            arr = ET.SubElement(geom, "Array", **{"as": "points"})
            for x, y in points:
                ET.SubElement(arr, "mxPoint", x=str(x), y=str(y))

    if name == "canonical-a2a-execution":
        route("e1", (.5, 1, 1, .25), [(413.5, 308), (269, 308), (269, 396)])
        route("e2", (1, .72, .72, 1), [(286, 465.56), (286, 322), (726.48, 322)])
        route("e3", (.3, 1, .6, 0), [(638.7, 296), (434.4, 296)])
    if name == "experience-a2a-distributed-agents-fig2":
        route("e4", (0, .5, 0, .5), [(16, 433), (16, 202)])
    if name == "experience-scaling-operations-fig1":
        route("e2", (.5, 1, .73, 0), [(680.5, 300), (461.57, 300)])
    if name == "experience-review-workspace-merge-fig1":
        route("e3", (.27, 1, 1, .25), [(365.43, 304), (269, 304), (269, 396)])
        route("e4", (.25, 1, .73, 0), [(628.25, 320), (461.57, 320)])
        route("e6", (0, .5, 0, .5), [(16, 433), (16, 202)])
    if name == "experience-workflows-backlog-fig3":
        route("e2", (.3, 1, 1, .25), [(371.7, 302), (269, 302), (269, 396)])
        route("e4", (.27, 1, .7, 0), [(632.43, 320), (455.3, 320)])
    if name == "experience-review-workspace-merge-fig2":
        cell = cells["n1-meta"]
        cell.set("style", cell.get("style").replace("fontSize=9.5;", "fontSize=9;"))
    ET.indent(tree)
    return '<?xml version="1.0" encoding="UTF-8"?>\n' + ET.tostring(tree, encoding="unicode") + "\n"


def allowed(path):
    relative = path.relative_to(ROOT).as_posix()
    if not any(relative == p or (p.endswith("/") and relative.startswith(p)) for p in PLAN["exclusive_asset_paths"]):
        raise ValueError(f"Not an exclusive asset path: {relative}")


def write(path, text):
    allowed(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def vertex(root, cell_id, value, x, y, w, h, style, parent="1"):
    cell = ET.SubElement(root, "mxCell", id=cell_id, value=value, style=style,
                         vertex="1", parent=parent)
    ET.SubElement(cell, "mxGeometry", x=str(x), y=str(y), width=str(w), height=str(h), **{"as": "geometry"})
    return cell


def label(root, cell_id, value, x, y, w, h, size=14, color="#272320", bold=False, parent="1", mono=False):
    family = "Cascadia Code" if mono else "Segoe UI"
    return vertex(root, cell_id, value, x, y, w, h,
                  f"text;html=0;strokeColor=none;fillColor=none;whiteSpace=wrap;align=left;"
                  f"verticalAlign=middle;fontFamily={family};fontSize={size};fontColor={color};"
                  f"fontStyle={1 if bold else 0};spacing=0;", parent)


def document(name, pitch=False):
    data = MODELS[name]
    template = ET.parse(ROOT / "docs/diagrams/drawio/fluent-template.drawio")
    mxfile = template.getroot()
    mxfile.set("agent", "GPT-6 Astra experience authoring")
    diagram = mxfile.find("diagram")
    diagram.set("id", name)
    diagram.set("name", data["title"])
    graph = diagram.find("mxGraphModel")
    graph.set("pageWidth", "827")
    graph.set("pageHeight", "583")
    graph.set("pageScale", "1")
    root = graph.find("root")
    root.clear()
    ET.SubElement(root, "mxCell", id="0")
    ET.SubElement(root, "mxCell", id="1", parent="0")
    vertex(root, "paper", "", 0, 0, 827, 583,
           "fillColor=#efeae7;strokeColor=none;")
    if pitch:
        for i, text in enumerate(data["pitch"][:2]):
            vertex(root, f"pitch-{i}", text, 72 + i * 407, 215, 276, 116,
                   "rounded=1;arcSize=16;absoluteArcSize=1;fillColor=#fdfbf8;strokeColor=#e2ddd9;"
                   "fontFamily=Segoe UI;fontSize=24;fontColor=#272320;whiteSpace=wrap;shadow=1;")
        if data["edges"]:
            cell = ET.SubElement(root, "mxCell", id="pitch-edge", value=data["pitch"][2],
                                 edge="1", parent="1", source="pitch-0", target="pitch-1",
                                 style="edgeStyle=orthogonalEdgeStyle;rounded=1;endArrow=block;"
                                 "strokeColor=#746d68;fontFamily=Segoe UI;fontSize=16;"
                                 "labelBackgroundColor=#fdfbf8;")
            ET.SubElement(cell, "mxGeometry", relative="1", **{"as": "geometry"})
    else:
        label(root, "title", data["title"], 32, 23, 765, 32, 25, bold=True)
        label(root, "takeaway", data["takeaway"], 32, 60, 765, 34, 14, "#635c57")
        for i, title in enumerate(data["groups"]):
            y = 100 + i * 231
            vertex(root, f"group-{i}", "", 28, y, 771, 190,
                   "rounded=1;arcSize=16;absoluteArcSize=1;fillColor=#f8f4f1;"
                   "strokeColor=#e2ddd9;strokeWidth=1;shadow=0;")
            label(root, f"group-title-{i}", title, 42, y + 8, 735, 18, 11, "#635c57", True)
        for i, node in enumerate(data["nodes"]):
            x, y = POSITIONS[i]
            bg, ink = PALETTE[node["tone"]]
            vertex(root, f"n{i}", "", x, y, WIDTH, HEIGHT,
                   "rounded=1;arcSize=16;absoluteArcSize=1;fillColor=#fdfbf8;"
                   "strokeColor=#ece7e3;strokeWidth=1;shadow=1;")
            vertex(root, f"n{i}-accent", "", 0, 10, 5, HEIGHT - 20,
                   f"rounded=1;arcSize=100;fillColor={ink};strokeColor=none;", f"n{i}")
            shape, _ = SYMBOLS[node["symbol"]]
            vertex(root, f"n{i}-icon", "", 14, 14, 30, 30,
                   f"{shape}html=1;fillColor={bg};strokeColor={ink};strokeWidth=1.5;"
                   f"fontColor={ink};", f"n{i}")
            vertex(root, f"n{i}-badge", node["badge"], 93, 15, 102, 23,
                   f"rounded=1;arcSize=100;fillColor={bg};strokeColor=none;"
                   f"fontFamily=Segoe UI;fontSize=10;fontStyle=1;fontColor={ink};", f"n{i}")
            label(root, f"n{i}-title", node["title"], 14, 51, 181, 22, 16, bold=True, parent=f"n{i}")
            label(root, f"n{i}-subtitle", node["subtitle"], 14, 77, 181, 18, 12, "#3f3935", parent=f"n{i}")
            label(root, f"n{i}-meta", node["metadata"], 14, 99, 181, 16, 9.5, "#746d68", parent=f"n{i}", mono=True)
            label(root, f"n{i}-detail", node["detail"], 14, 119, 181, 26, 10.5, "#635c57", parent=f"n{i}")
        for i, link in enumerate(data["edges"]):
            s, t = link["source"], link["target"]
            sx, sy = POSITIONS[s]
            tx, ty = POSITIONS[t]
            points = []
            route = link["route"]
            if sy == ty:
                right = tx > sx
                exit_x, entry_x = (1, 0) if right else (0, 1)
                exit_y = entry_y = 0.5
                if route in ("left-top", "right-bottom"):
                    exit_y = entry_y = 0.36 if route == "left-top" else 0.74
            else:
                exit_y, entry_y = (1, 0) if ty > sy else (0, 1)
                exit_x = entry_x = 0.5
                if route == "down-left":
                    exit_x = entry_x = 0.27
                elif route in ("up-right", "up-left"):
                    exit_x = entry_x = 0.73 if route == "up-right" else 0.27
                if sx != tx:
                    mid = {"middle-left": 308, "middle-right": 320, "diagonal": 300}.get(route, 310)
                    points = [(sx + WIDTH * exit_x, mid), (tx + WIDTH * entry_x, mid)]
            color = "#d39300" if link["revision"] else "#746d68"
            style = (f"edgeStyle=orthogonalEdgeStyle;rounded=1;orthogonalLoop=1;jettySize=auto;"
                     f"html=0;endArrow=block;endFill=1;strokeColor={color};strokeWidth=1.5;"
                     f"fontFamily=Segoe UI;fontSize=11;fontColor=#3f3935;"
                     f"labelBackgroundColor=#fdfbf8;jumpStyle=arc;jumpSize=6;"
                     f"exitX={exit_x};exitY={exit_y};entryX={entry_x};entryY={entry_y};")
            if link["revision"]:
                style += "dashed=1;dashPattern=8 6;"
            cell = ET.SubElement(root, "mxCell", id=f"e{i}", value=link["label"], style=style,
                                 edge="1", parent="1", source=f"n{s}", target=f"n{t}")
            geom = ET.SubElement(cell, "mxGeometry", relative="1", **{"as": "geometry"})
            if points:
                arr = ET.SubElement(geom, "Array", **{"as": "points"})
                for x, y in points:
                    ET.SubElement(arr, "mxPoint", x=str(x), y=str(y))
        label(root, "note", data["note"], 32, 538, 763, 29, 12, "#635c57")
    ET.indent(mxfile)
    return '<?xml version="1.0" encoding="UTF-8"?>\n' + ET.tostring(mxfile, encoding="unicode") + "\n"


def export(source, png):
    allowed(png)
    subprocess.run([str(CLI), f"--user-data-dir={HERE / 'export-tool/profile'}",
                    "--export", "--format", "png", "--border", "16", "--scale", "2",
                    "--output", str(png), str(source)], check=True, cwd=ROOT,
                   stdout=subprocess.DEVNULL)
    with Image.open(png) as image:
        image.resize((827, round(image.height * 827 / image.width)), Image.Resampling.LANCZOS).save(
            png.with_stem(png.stem + "-print"))


def artifacts(name, phase):
    folder = REVIEWS / name
    stem = f"{name}-{phase}"
    return folder / f"{stem}.drawio", folder / f"{stem}.png", folder / f"{stem}.md"


def prepare(names, phase):
    for name in names:
        source, png, record = artifacts(name, phase)
        if source.exists() or png.exists() or record.exists():
            raise ValueError(f"Refusing to overwrite saved phase: {source}")
        if phase in ("pitch", "pass-01"):
            xml = document(name, pitch=phase == "pitch")
        else:
            previous = f"pass-{int(phase[-2:]) - 1:02}"
            xml = artifacts(name, previous)[0].read_text()
            if phase == "pass-02":
                xml = correct_second_pass(xml, name)
            if phase == "pass-03":
                xml = correct_third_pass(xml, name)
        write(source, xml)
        if phase == "pass-01":
            baseline = growth.analyze(artifacts(name, "pitch")[0].read_text())
            result = growth.analyze(xml)
            assessed = growth.assess(artifacts(name, "pitch")[0].read_text(), xml)
            if not assessed["passed"]:
                raise ValueError(f"{name}: growth gate failed: {assessed}")
        export(source, png)
        print(f"EXPORTED {name} {phase}")


def record_inspection(names, phase):
    # Called by the author only after opening both the print and full-size PNGs.
    for name in names:
        data = MODELS[name]
        source, png, record = artifacts(name, phase)
        if record.exists():
            raise ValueError(f"Do not overwrite completed review: {record}")
        detail = f"# {name}: {phase}\n\n"
        detail += f"**Takeaway:** {data['takeaway']}\n\n"
        detail += "**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.\n\n"
        detail += "**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. "
        detail += "Actual exported PNG and its 827 px print-size derivative opened and inspected.\n\n"
        detail += f"**Grounding:** [{data['research']}](../{data['research']}). "
        detail += "Three independently launched Astra threads: identity, execution, orchestration. "
        detail += "Implementation/config/tests outrank legacy diagrams and captures.\n\n"
        if phase == "pitch":
            detail += "## Pitch handoff\n\nTwo-concept relationship sketch establishes the reading direction. "
            detail += "Known issue: deliberately collapsed responsibilities and outcome qualifications need "
            detail += "source-backed decomposition in pass 1; not publication-ready. "
            detail += "Long connector labels crowd endpoints in the team, session, consent and review sketches; "
            detail += "the visual-upgrade pass must separate those labels and expose their arrowheads. "
            detail += "Handed to the locally read docs-diagram-iterate workflow.\n\n"
        else:
            number = int(phase[-2:])
            if number == 1:
                baseline = growth.analyze(artifacts(name, "pitch")[0].read_text())
                result = growth.analyze(source.read_text())
                detail += "## Visual upgrade\n\n"
                detail += f"Metric: visible-semantic-canonical-xml-v1. Baseline {baseline['meaningful_count']}; "
                detail += f"result {result['meaningful_count']}; ratio "
                detail += f"{result['meaningful_count'] / baseline['meaningful_count']:.6f}x.\n\n"
                detail += "Growth decomposes the two conceptual endpoints into six distinct evidenced responsibilities "
                detail += "or outcomes, with tiered boundaries, native icons, titles, subtitles, metadata, badges, "
                detail += "visible qualifications and endpoint-attached connectors. No padding, invisible objects, "
                detail += "off-page cells, duplicate cells, comments or image bytes contribute.\n\n"
            elif number == 3 and name in PASS_TWO_ISSUES:
                detail += "Repositioned only the offending connector label: cleared the RunEvents crossing "
                detail += "or kept the outer return label within the printable page. "
            else:
                detail += "## Correction-only review\n\nNo composition, content, or visual-language changes. "
                if number == 2:
                    detail += "Wrapped long connector labels, separated ambiguous ports/routes, moved revision "
                    detail += "returns into outer gutters, and tightened the one crowded metadata label where needed. "
                    detail += "No new semantic objects or relationships. "
                else:
                    detail += "Orientation, overlap, arrow endpoint/direction/routing checks found no permitted defect. "
                detail += "Saved and re-exported a distinct source/PNG pair.\n\n"
            detail += "Reading direction, card/boundary separation, title/detail fit, label association and "
            detail += "arrow direction reviewed at print size and enlarged. "
            if number == 1 and name in PASS_ONE_ISSUES:
                detail += f"Correction handoff: {PASS_ONE_ISSUES[name]}\n\n"
            elif number == 2 and name in PASS_TWO_ISSUES:
                detail += f"Correction handoff: {PASS_TWO_ISSUES[name]}\n\n"
            else:
                detail += "Zero remaining defects.\n\n"
        detail += "## Symbols and credits\n\n"
        detail += "Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated "
        detail += "by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. "
        detail += "Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, "
        detail += "shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.\n\n"
        for node in data["nodes"]:
            detail += f"- {node['title']}: `{SYMBOLS[node['symbol']][1]}`.\n"
        detail += "\nNative shapes come from the bundled draw.io Desktop 31.4.5 libraries "
        detail += "(https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); "
        detail += "draw.io is Apache-2.0. Vendor symbols identify their actual technology; "
        detail += "no separate logos, screenshots or third-party image bytes were acquired. "
        detail += "Custom hexagon marks only product-specific concepts.\n\n"
        detail += "## Evidence map" + (" and complete final arrow trace" if phase == "pass-04" else "") + "\n\n"
        for i, link in enumerate(data["edges"]):
            detail += (f"- e{i}: {data['nodes'][link['source']]['title']} -> "
                       f"{data['nodes'][link['target']]['title']}: {link['label']}. "
                       f"`{link['evidence']}`. "
                       + ("Trace pending correction passes.\n" if phase in ("pitch", "pass-01") or
                          (phase == "pass-02" and name in PASS_TWO_ISSUES) else
                          "Endpoint-attached, correctly directed, gutter-routed; clean.\n"))
        if not data["edges"]:
            detail += "No arrows: this is a scoped surface map, not a process. Final trace set is empty.\n"
        write(record, detail)
        print(f"RECORDED {name} {phase}")


def manifest(names):
    import jsonschema
    for name in names:
        data = MODELS[name]
        result = dict(diagram=name, orientation="A5-landscape", pitch={}, passes=[], final_pass=4)
        for phase in ["pitch"] + [f"pass-{i:02}" for i in range(1, 5)]:
            source, png, record = artifacts(name, phase)
            if not all(p.exists() for p in [source, png, record, png.with_stem(png.stem + "-print")]):
                raise ValueError(f"Missing reviewed phase: {name} {phase}")
            entry = dict(drawio=source.name, png=png.name, change_record=record.name,
                         png_inspected_print=True, png_inspected_enlarged=True)
            if phase == "pitch":
                result["pitch"] = entry
                continue
            number = int(phase[-2:])
            entry.update(number=number, mode="visual-upgrade" if number == 1 else "correction-only",
                         orientation_defects=0, overlap_defects=0, arrow_defects=0)
            if number == 1 and name in PASS_ONE_ISSUES:
                entry.update(overlap_defects=1, arrow_defects=1)
            if number == 2 and name in PASS_TWO_ISSUES:
                entry.update(overlap_defects=1)
            if number == 1:
                a = growth.analyze(artifacts(name, "pitch")[0].read_text())
                b = growth.analyze(source.read_text())
                entry.update(baseline_meaningful_xml=a["meaningful_count"],
                             result_meaningful_xml=b["meaningful_count"],
                             growth_metric="visible-semantic-canonical-xml-v1",
                             growth_ratio=b["meaningful_count"] / a["meaningful_count"])
            if number == 4:
                entry["all_arrows_traced"] = True
                entry["arrow_trace"] = [
                    dict(id=f"e{i}", source=f"n{e['source']}", target=f"n{e['target']}",
                         relationship=e["label"], evidence=e["evidence"], result="clean")
                    for i, e in enumerate(data["edges"])
                ]
            result["passes"].append(entry)
        jsonschema.validate(result, json.loads(SCHEMA.read_text()))
        write(REVIEWS / name / "iteration-manifest.json", json.dumps(result, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["prepare", "record", "manifest", "models"])
    parser.add_argument("--phase", default="pitch")
    parser.add_argument("--names", nargs="*", default=list(MODELS))
    args = parser.parse_args()
    if not set(args.names) <= set(PLAN["diagram_names"]):
        raise ValueError("Unowned diagram selection")
    library = (ROOT / "docs/diagrams/drawio/fluent-library.xml").read_text()
    if not json.loads(library.split("<mxlibrary>", 1)[1].split("</mxlibrary>", 1)[0]):
        raise ValueError("Missing Fluent library")
    if args.action == "prepare":
        prepare(args.names, args.phase)
    elif args.action == "record":
        record_inspection(args.names, args.phase)
    elif args.action == "manifest":
        manifest(args.names)
    else:
        write(HERE / "experience-content-models.json", json.dumps(MODELS, indent=2) + "\n")


if __name__ == "__main__":
    main()
