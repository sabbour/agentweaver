# Sandbox browser preview — Deep Dive

When an agent starts an HTTP server **inside its sandbox pod** — a dev server, a freshly built web app,
a debug endpoint — the **sandbox browser preview** exposes it to the user at a public HTTPS URL, scoped to
that one run. The preview is a **Gateway-direct reverse proxy**: a shared Gateway API gateway routes a
per-preview subdomain straight to the run's sandbox pod. It is **not** an API-loopback `kubectl
port-forward` (that earlier design was replica-unsafe and is retained only as a local-dev fallback).

This page explains how the proxy is wired and torn down. For the API surface see the
[reference](../reference/sandbox-browser-preview.md); for the user flow see the
[user guide](../experience/sandbox-browser-preview.md).

The feature is **enabled by default** in AKS deployments (`Sandbox__Preview__Enabled=true`, gateway
`agentweaver-preview-gateway`, zone `6a41f26c75d5cf00019ef7d7.westus2.staging.aksapp.io`). In local-dev
environments where `Sandbox:Preview:Enabled` is `false`, the Gateway path is a no-op and the
`kubectl port-forward` fallback is used instead ([`SandboxPreviewOptions.cs:21`](#source)).

## End-to-end flow

A preview creates two Kubernetes objects (Service and HTTPRoute) and patches the
existing pod. The API also validates the generated HTTPS URL; creation alone is
not readiness. The shared-owned diagram below is a reference, not authority for
older "creates objects only" labels:

When the user clicks **Preview** and picks a port, `StartPreviewAsync`
([`SandboxPreviewService.cs:100`](#source)) does the following:

1. **Resolve the bound pod from cluster state.** `ResolveBoundPodNameAsync`
   ([`SandboxPreviewService.cs:387`](#source)) derives the run's `SandboxClaim` name
   (`SandboxClaimConventions.DeriveAgentHostClaimName`), reads the claim via the in-cluster
   custom-objects client, and returns the bound pod from `status` — **not** from any in-process registry
   (see [Why cluster-state resolution](#why-cluster-state-pod-resolution-replica-safety)). A missing or
   not-yet-`Bound` claim is a deterministic "not ready" → `409`.
2. **Mint a capability token.** `PreviewToken.Generate` ([`PreviewToken.cs:68`](#source)) returns an
   unguessable label (three cosmetic words + a 128-bit base32 suffix). The host label is
   `{token}-preview` ([`PreviewToken.cs:92`](#source)) and the preview URL is
   `https://{token}-preview.{ZoneSuffix}`.
3. **Patch the pod** with the per-run selector label `agentweaver.dev/preview-run`
   ([`SandboxPreviewService.cs:122`](#source)) so a Service can target it.
4. **Create a ClusterIP Service** named `preview-{token}` whose selector is the per-run label and whose
   port `80` forwards to the requested `target_port` ([`SandboxPreviewService.cs:134`](#source)). For
   operator/manual previews that is the port the user entered; for the platform-owned live-preview path it
   is the AgentHost forwarder's public port, not necessarily the app's own port.
5. **Create an HTTPRoute** named `preview-{token}` that attaches to the shared preview Gateway, matches the
   `{token}-preview.{ZoneSuffix}` hostname, and backends the Service. Idle/max expiry and the run binding
   are stored in **annotations** ([`SandboxPreviewService.cs:168`](#source),
   [`:438`](#source)). If the HTTPRoute create fails for any reason other than `Conflict`, the
   just-created Service is best-effort deleted before rethrowing, so a retry can't leak ClusterIPs
   ([`SandboxPreviewService.cs:184`](#source)).
6. **Validate publication through the generated hostname.** App Routing owns the managed DNS zone and creates
   the per-preview record. The API probes immediately; a fresh name may be NXDOMAIN while that record
   converges, so it retries only name-resolution failures with bounded backoff until the configured
   `DnsConvergenceTimeoutSeconds` deadline (ten minutes by default). Existing records succeed on the
   initial probe. After DNS resolves, non-DNS Gateway and application failures use the shorter
   `PublicationTimeoutSeconds` readiness window. The API neither creates wildcard records nor otherwise
   mutates DNS.

The API returns `preview_url` and a relative `keepalive_url`; the browser opens the URL (in an iframe with
`referrerPolicy="no-referrer"`) and pings keepalive every 60 s. The API does **not** prove readiness by
connecting to `podIP:{target_port}`. `StartPreviewAsync` deliberately skips that TCP preflight because the
sandbox NetworkPolicy admits preview ports only from the preview Gateway, not from API pods
([`SandboxPreviewService.cs:134`](#source), [`k8s/base/networkpolicy-sandbox.yaml`](#source)). End-to-end data-path
reachability is therefore exercised at the Gateway hostname (`preview_url`), while registration readiness comes
from the in-pod AgentHost observation described next.

### Live-preview forwarder: pod-IP reachability guarantee

The Build & Test live-preview path adds one pod-local hop before step 4 above. `PreviewRunner` runs **inside
the same sandbox pod as the preview app**: it first discovers the app's real bound port using app log hints and
the pod's namespace-local kernel socket tables (`/proc/net/tcp` and `/proc/net/tcp6`), health-checks that app
port, then starts `TcpPortForwarder` in that pod. Reading `/proc/net/tcp6` is required for common Node defaults
such as `server.listen(port)`, which bind IPv6-any (`::`) and may not appear in `/proc/net/tcp`. The forwarder
listens on `0.0.0.0:{publicPort}` and pumps TCP to `127.0.0.1:{appPort}`
([`apps/Agentweaver.AgentHost/TcpPortForwarder.cs`](#source),
[`apps/Agentweaver.AgentHost/PreviewRunner.cs:315`](#source)). The public port is chosen by scanning the
allowed preview range `3000-9000`, matching both `SandboxPreviewOptions.AllowedPortMin/Max` and
`k8s/base/networkpolicy-sandbox.yaml`, and that public port is the value registered with the Gateway
([`PreviewRunner.cs:21`](#source), [`SandboxPreviewOptions.cs:56`](#source)).

This closes the loopback failure mode: previously an app that listened only on `127.0.0.1:3000` could pass the
AgentHost health check, but Gateway registration failed because Kubernetes routes to `podIP:port` and nothing
was listening there. The forwarder makes the registered port reachable on the pod IP regardless of whether the
app bound loopback-only or all interfaces. `ObserveBoundPortAsync` verifies reachability **inside the pod,
through the forwarder public port** before `PreviewStep` asks for approval or registers the Gateway route; if
the public port cannot be reached, the outcome is `sandbox.preview_failed` with reason `bound_unreachable`, and
if no port in `3000-9000` is free, the reason is `no_public_port_available`
([`PreviewRunner.cs:321`](#source), [`PreviewStep.cs:152`](#source)). Port-discovery failures are legible and
closed-set: `no_listening_port_discovered` when the observe timeout expires without a healthy listening port,
`process_exited:exit={code}` when the app exits before readiness, and `observe_error` for an unexpected
observe-endpoint error ([`PreviewRunner.cs:262`](#source), [`apps/Agentweaver.AgentHost/Program.cs:347`](#source)).
Denial and non-retryable failures best-effort stop the process and forwarder.
Approval expiry instead retains the healthy private process for fresh-approval
retry; it does not publish a URL.

### Single-label subdomain (no nested wildcards)

The host is `{token}-preview.{ZoneSuffix}` — a **single** new DNS label under the zone wildcard. AKS App
Routing's managed `DefaultDomainCertificate` only issues a `*.{zone}` wildcard and **does not support nested
wildcards** ([`gateway-preview.yaml:12`](#source)), so the token and the `-preview` marker share one
leftmost label (`{token}-preview`) rather than becoming two levels. `ZoneSuffix` is the managed
`aksapp.io` zone, supplied by the deploy script.

### Why cluster-state pod resolution (replica-safety)

The API runs at **replicas:2 with no session affinity**. The in-memory `PodNameRegistry` is populated
**only on the replica that launched the sandbox pod**, so a preview-start request landing on the *other*
replica would find nothing and fail — a split-brain `409`. `SandboxClaimConventions`
([`SandboxClaimConventions.cs`](#source)) reads the bound pod from the `SandboxClaim`'s `status`
(`Ready` condition `True` → `status.sandbox.name`), which **every** replica sees
identically. All other per-preview state lives in HTTPRoute annotations, never in process memory, so
keepalive and reaping are equally replica-safe.

## Lifecycle and cleanup

A preview outlives the run by default (`KeepAfterRun=true`, [`SandboxPreviewOptions.cs:46`](#source)) and
is torn down by a background reaper, an explicit stop, or pod disappearance:

- **Sliding idle TTL.** The HTTPRoute's `preview-expires-at` annotation is set to
  now + the project `lifetime_minutes` (**24 h** default; deployment fallback is
  `LifetimeMinutes`). `preview-max-until` initially uses the same lifetime.
  Keepalive moves idle expiry but never the original hard maximum.
- **Pod-gone.** If the backing pod no longer exists (run ended, claim released), the reaper reaps the
  preview as an orphan.
- **The reaper.** `SandboxPreviewReaperService` ([`SandboxPreviewReaperService.cs`](#source)) sweeps every
  ~60 s, listing preview HTTPRoutes and feeding each route's two timestamps plus a live pod-exists flag into
  the pure decision function `PreviewReaper.Decide` ([`PreviewReaper.cs:56`](#source)) →
  `Alive` / `ExpiredIdle` / `ExpiredMax` / `Orphan`. Non-alive previews are deleted (HTTPRoute then
  Service). `ListForRunAsync` uses the same isolation-safe liveness proxy — a control-plane pod lookup by
  `agentweaver.dev/preview-run` label — instead of a forbidden API-pod TCP probe
  ([`SandboxPreviewService.cs:399`](#source), [`:768`](#source)).
- **Orphan-Service sweep.** The same pass also deletes any `preview-*` Service that has **no** matching
  HTTPRoute (e.g. the process died between Service-create and HTTPRoute-create), after a 2-minute grace, so
  a retry loop can never accumulate leaked ClusterIPs ([`SandboxPreviewService.cs:303`](#source)).
- **Explicit stop.** `DELETE …/port-forward/{token}` calls `StopPreviewAsync`
  ([`SandboxPreviewService.cs:245`](#source)), which deletes the HTTPRoute then the Service (both
  idempotent / 404-tolerant).

Because every decision input is read from cluster state, **both** API replicas reconcile identically — there
is no leader and no in-memory expiry timer.

## Run ↔ token binding

Keepalive and stop never trust the token alone. `VerifyTokenForRunAsync`
([`SandboxPreviewService.cs:406`](#source)) reads the HTTPRoute named for the token and confirms its
`preview-run` annotation matches the `runId` in the URL (`PreviewReaper.RunMatches`,
[`PreviewReaper.cs:143`](#source)). A mismatch returns `404`, so one run cannot keep alive or delete
another run's preview by guessing a foreign token. The check reads cluster annotations, so it is
replica-safe.

## Security and containment notes

- **Capability URL.** The URL is unauthenticated — possession grants access. All security entropy is the
  128-bit CSPRNG suffix ([`PreviewToken.cs:35`](#source)); the cosmetic words add none. Reserved labels
  (`agentweaver`, `mcp`, `api`, `frontend`) are denied and regenerated ([`PreviewToken.cs:25`](#source)).
- **NetworkPolicy.** `sandbox-allow-preview-ingress` ([`networkpolicy-sandbox.yaml`](#source)) admits
  TCP `3000-9000` from a single `from` peer with a `podSelector` matching the preview gateway pods
  (`gateway.networking.k8s.io/gateway-name=agentweaver-preview-gateway`). With no `namespaceSelector`, the
  peer matches those pods in the policy's own namespace (`agentweaver`) — exactly where the
  approuting-istio preview gateway data-plane runs — so only the preview gateway can reach the sandbox
  preview ports under that rule. TCP 8088 is inside the range and also has
  API/worker control allows, so the union is not an exclusive Gateway-only rule
  for every port. API does not use a direct preview-port preflight. Out-of-range ports are rejected by the endpoint, so we never provision a preview
  the policy would black-hole.
- **Capability token in the URL.** The 128-bit token rides in the preview URL and therefore the Host header
  (and keepalive path). This is expected and inherent to an unguessable capability URL: app code only ever
  logs a non-reversible fingerprint (`SHA-256[0..4]+token`), never the raw token, and the URL is unguessable
  and short-lived (idle + hard-cap reaper) with `no-referrer` on preview pages.
- **RBAC.** The API ServiceAccount can read `sandboxclaims` and create/delete the per-preview `services` and
  `httproutes` ([`rbac-api.yaml`](#source)).

## Agent capability awareness

When the feature is enabled, `RunOrchestrator.ComposeCapabilities`
([`RunOrchestrator.cs:590`](#source)) appends a short **Browser Preview** note
([`RunOrchestrator.cs:64`](#source)) to worker/child system prompts, telling the agent to start and verify a
server, then call `start_preview(port=PORT)` with the actual port it observed. The prompt no longer tells the
model to pick, hardcode, force, or print a specific port; the same no-hardcoded-port guidance is present in
`AgentBasePrompt`, the project agent template, and `CharterCompiler` ([`AgentBasePrompt.cs:48`](#source),
[`apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md`](#source), [`CharterCompiler.cs:74`](#source)).

The note is additionally gated by `RunOrchestrator.RunSupportsPreview`: the orchestrating **Coordinator**
run (a run with no parent whose agent is `Coordinator`) never launches a server itself — it only dispatches
child worker runs — so it is not given the "you MUST launch, test, and preview a server" mandate. Child
worker runs and ordinary single-agent runs still receive it when the feature is enabled.

Build & Test gets a platform-owned preview step instead of asking the model to pick a port. The command
resolver deliberately avoids the old `PORT=3000` / `--port` injection: known stacks may receive host-binding
hints, but the app keeps its framework default or honors `process.env.PORT` if it already does so
([`PreviewCommandResolver.cs:25`](#source)). AgentHost then observes the actual app port, fronts it with the
pod-local `TcpPortForwarder`, and `PreviewStep` registers the forwarder's public port with the Gateway
([`PreviewRunner.cs:315`](#source), [`PreviewStep.cs:166`](#source)). During coordinator assembly in
`pod-per-run` mode, the step runs inside a dedicated AgentHost pod bound to the coordinator run id and
configured with the detached integration worktree as its working directory (`CollectiveAssemblyPipeline.cs:155`,
`KubernetesSandboxExecutor.cs:423`). The preview service therefore creates the HTTPRoute to that AgentHost pod,
so the review URL reaches the server running from the assembled tree. The preview service supports this by
resolving both run claim conventions: the AgentHost `agent-{runId}` claim and the retained command-sandbox
`run-{runId}` claim (`SandboxClaimConventions.cs:28`, `SandboxPreviewService.cs:432`).

## Agent-initiated preview (`start_preview`)

A running agent can also expose its server **autonomously**, mid-workflow, without a human picking a port in
the UI — via `PreviewRunnerToolProvider` / `PreviewPublishTool`. The model supplies
`port` and optional process `session_id`; the run ID is server-bound. The tool's
binding is not itself a cryptographic restriction on a shared service credential:
the endpoint still performs run access checks.

1. **The tool POSTs** `{ target_port }` to `POST /api/runs/{runId}/sandbox/preview`
   ([`SandboxEndpoints.cs:60`](#source)) and returns the response `preview_url` back to the agent.
2. **Authorization** requires contributor-level run access, with the explicit
   internal-service allowance. Port/run validation and authorization are separate
   from the later operator-approval gate (`SandboxEndpoints.cs:86-137`).
3. **The HITL gate** `AgentPreviewGate.RequestApprovalAsync` ([`AgentPreviewGate.cs:108`](#source)) is the
   human-in-the-loop seam. It reuses the same `IToolApprovalGate` primitive as `web_fetch`: it emits a
   `tool.approval_required` card ([`AgentPreviewGate.cs:131`](#source)) and suspends until an operator grants
   via `POST /api/runs/{runId}/tool-approvals` or the project-configured approval window times out
   (30 minutes by default; project owners choose 1–1440 minutes in Sandbox policy settings). The
   deployment config remains only a fallback for legacy/non-project runs.
4. **Explicit preview auto-approve** short-circuits the wait when any of these is on
   ([`AgentPreviewGate.cs:93`](#source)): the global `Sandbox:Preview:AutoApprove` config / env
   `SANDBOX_PREVIEW_AUTO_APPROVE` ([`AgentPreviewGate.cs:176`](#source)), the immutable run
   `auto_approve_tools` policy, or an existing scoped allow policy. A run-policy grant emits
   `tool.auto_approved` with the policy snapshot ID, `previewTarget=run_sandbox`, and target port;
   it contains no credential, token, secret URL, or request body and creates no approval card,
   notification, or waiter. Production remains human-gated when all sources are false. Auto-approval
   bypasses only the human wait: port validation, process liveness, sandbox ownership, and Gateway or
   port-forward publication still run and fail normally.
5. **On approval** the endpoint rechecks the active run and, when a process session
   is supplied, process health. Only then does it run the **same** `StartPreviewForRunAsync` path
   ([`SandboxEndpoints.cs:238`](#source)) as the operator route — Gateway-direct preview when enabled,
   `kubectl` fallback otherwise. Gateway publication returns a URL only after
   HTTPS validation. Denial returns 403 and expiry returns 408 without calling
   the publication path.
6. **On timeout** the deterministic preview step records the expired request and retains the healthy,
   still-private PreviewRunner process. An owner can call
   `POST /api/runs/{runId}/sandbox/preview-approvals/{requestId}/retry`; the API rejects non-expired,
   superseded, or terminal-run retries, emits a fresh request id, and registers the same process only
   after approval. Explicit denial still stops the retained process.

> **Design note.** The agent tool is a synchronous HTTP callback that must return a URL, so it uses the
> per-tool `IToolApprovalGate` (which persists context/decisions and returns a bool) rather than the MAF
> `RequestPort` workflow primitive — `RequestPort` suspends/checkpoints the whole workflow and resumes via a
> separately-posted decision, which cannot satisfy a synchronous tool callback.

### Second surface: the MCP `start_preview` tool

The same capability is also exposed as an **MCP tool** on the `agentweaver-mcp` server, so an external MCP
client (e.g. GitHub Copilot connected to the Agentweaver MCP server) can expose a run's preview without being
the in-sandbox agent. Because an external caller is not bound to a single run, the MCP tool takes the run id as
an explicit parameter: `start_preview(run_id: string, port: int)`
([`RunTools.cs`](#source), auto-discovered by `WithToolsFromAssembly`). It POSTs `{ target_port }` to the
**same** `POST /api/runs/{runId}/sandbox/preview` endpoint, so it reuses the **same** `AgentPreviewGate` and
`StartPreviewForRunAsync` path — no port-forward or approval logic is duplicated. Authorization is enforced by
the MCP server forwarding the caller's bearer token to the API (`AgentweaverApiClient`), so the backend sees the
real human identity and the owner check (`IsOwnerOrServiceCaller`) applies unchanged. The same immutable run
`auto_approve_tools` snapshot used by API- and AgentHost-initiated preview calls applies here, so MCP does not
introduce a separate approval path. Omitted/false policy remains human-gated.

> The MCP surface lives in the separate `agentweaver-mcp` deployable image, so changes to `start_preview` require
> rebuilding **both** `agentweaver-api` and `agentweaver-mcp`.

## Source

| Concern | File |
|---|---|
| Preview provisioning, reap, orphan sweep, run↔token binding | `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs` |
| Pod-local live-preview TCP forwarder | `apps/Agentweaver.AgentHost/TcpPortForwarder.cs` |
| Supervised live-preview process, `/proc/net/tcp{,6}` port discovery, and forwarder observation | `apps/Agentweaver.AgentHost/PreviewRunner.cs` |
| Deterministic live-preview step and failure reasons | `apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs` |
| Config defaults & port-range check | `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewOptions.cs` |
| Capability token (128-bit, reserved deny, DNS-1123) | `apps/Agentweaver.Api/Sandbox/Preview/PreviewToken.cs` |
| Reaper decision logic & label/Service-name helpers | `apps/Agentweaver.Api/Sandbox/Preview/PreviewReaper.cs` |
| Background ~60 s reaper sweep | `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewReaperService.cs` |
| SandboxClaim CRD coordinates + bound-pod parsing | `apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs` |
| HTTP endpoints (start / agent-start / keepalive / stop / list) | `apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs` |
| Agent-initiated approval gate (HITL + auto-approve) | `apps/Agentweaver.Api/Sandbox/Preview/AgentPreviewGate.cs` |
| `start_preview` agent tool (run-scoped HTTP callback) | `packages/Agentweaver.AgentRuntime/AgentweaverApiTools.cs` |
| `start_preview` MCP tool (run_id + port, same endpoint) | `apps/Agentweaver.Mcp/Tools/RunTools.cs` |
| Owner-or-agent-callback authorization helper | `apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs` |
| HITL approval primitive (shared with `web_fetch`) | `apps/Agentweaver.Api/Runs/DurableToolApprovalGate.cs` |
| Agent capability note injection | `apps/Agentweaver.Api/Runs/RunOrchestrator.cs` |
| Build & Test preview activation prompt | `packages/Agentweaver.AgentRuntime/Workflow/BuildTestTurnExecutor.cs` |
| Shared preview Gateway | `k8s/base/gateway-preview.yaml` |
| Sandbox NetworkPolicy (preview ingress range) | `k8s/base/networkpolicy-sandbox.yaml` |
| API RBAC (claims read, service/route write) | `k8s/base/rbac-api.yaml` |
| Preview button, iframe, keepalive ping | `apps/web/src/pages/CoordinatorRunPage.tsx` |
| API client (`startPortForward` / `pingKeepalive`) | `apps/web/src/api/client.ts` |
| `PortForwardSessionDto` (DTO fields) | `apps/web/src/api/types.ts` |

## See also

- [Sandbox browser preview — Reference](../reference/sandbox-browser-preview.md) — routes, DTO, config, status codes.
- [Sandbox browser preview — User Guide](../experience/sandbox-browser-preview.md) — the step-by-step user flow.
- [Live-preview provisioning](./live-preview-provisioning.md) — how Build & Test produces and enforces preview outcomes.
- [Sandbox](./sandbox.md) — the sandbox claim/pod model the preview targets.
- [Sandbox pod execution](./sandbox-pod-execution.md) — how the per-run pod is claimed and bound.
- [Sandbox pods reference](../reference/sandbox-pods.md) — pod naming and the wider sandbox API surface.

<details id="diagram-context-sandbox-browser-preview-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Preview readiness follows the public path</td></tr>
<tr><td>takeaway</td><td>Provision the route, then probe its exact HTTPS URL; object creation alone is not ready.</td></tr>
<tr><td>group-title0</td><td>CONTROL: PROVISION + PROBE</td></tr>
<tr><td>group-title1</td><td>GATEWAY DATA PATH</td></tr>
<tr><td>Preview API</td><td>Preview API</td></tr>
<tr><td>Preview API</td><td>Resolve bound SandboxClaim</td></tr>
<tr><td>Preview API</td><td>Patch run selector on pod</td></tr>
<tr><td>Preview API</td><td>Create Service + HTTPRoute</td></tr>
<tr><td>Preview API</td><td>State from cluster, not cache</td></tr>
<tr><td>Publication probe</td><td>Publication probe</td></tr>
<tr><td>Publication probe</td><td>Exact generated HTTPS URL</td></tr>
<tr><td>Publication probe</td><td>Wait for managed DNS</td></tr>
<tr><td>Publication probe</td><td>Check Gateway + application</td></tr>
<tr><td>Publication probe</td><td>Only then return ready</td></tr>
<tr><td>Browser preview</td><td>Browser preview</td></tr>
<tr><td>Browser preview</td><td>Open the returned URL</td></tr>
<tr><td>Browser preview</td><td>Run-scoped capability host</td></tr>
<tr><td>Browser preview</td><td>Keepalive via API</td></tr>
<tr><td>Browser preview</td><td>Iframe: no-referrer</td></tr>
<tr><td>Preview Gateway</td><td>Preview Gateway</td></tr>
<tr><td>Preview Gateway</td><td>Separate shared Gateway</td></tr>
<tr><td>Preview Gateway</td><td>HTTPS host match</td></tr>
<tr><td>Preview Gateway</td><td>HTTPRoute selects Service</td></tr>
<tr><td>Preview Gateway</td><td>Not API port-forward</td></tr>
<tr><td>ClusterIP Service</td><td>ClusterIP Service</td></tr>
<tr><td>ClusterIP Service</td><td>Per-preview target selector</td></tr>
<tr><td>ClusterIP Service</td><td>Service :80 → public port</td></tr>
<tr><td>ClusterIP Service</td><td>Routes to bound sandbox pod</td></tr>
<tr><td>ClusterIP Service</td><td>Allowed ports 3000–9000</td></tr>
<tr><td>Sandbox preview app</td><td>Sandbox preview app</td></tr>
<tr><td>Sandbox preview app</td><td>AgentHost pod-local path</td></tr>
<tr><td>Sandbox preview app</td><td>Live preview: TCP forwarder</td></tr>
<tr><td>Sandbox preview app</td><td>0.0.0.0 → loopback app</td></tr>
<tr><td>Sandbox preview app</td><td>Manual: chosen target port</td></tr>
<tr><td>relation-0</td><td>1 after create</td></tr>
<tr><td>relation-1</td><td>2 ready URL</td></tr>
<tr><td>relation-2</td><td>3 HTTPS probe</td></tr>
<tr><td>relation-3</td><td>4 HTTPS</td></tr>
<tr><td>relation-4</td><td>5 route</td></tr>
<tr><td>relation-5</td><td>6 public port</td></tr>
<tr><td>assurance</td><td>No API → pod TCP readiness probe. Publication failure rolls back; DNS convergence has a bounded retry window.</td></tr>
<tr><td>assurance-0-label</td><td>Public readiness</td></tr>
<tr><td>assurance-0-fact</td><td>Probe the exact generated HTTPS URL.</td></tr>
<tr><td>assurance-0-source</td><td>SandboxPreviewService.cs</td></tr>
<tr><td>assurance-1-label</td><td>Rollback on failure</td></tr>
<tr><td>assurance-1-fact</td><td>Unpublish failed preview resources.</td></tr>
<tr><td>assurance-1-source</td><td>SandboxPreviewPublicationTests.cs</td></tr>
<tr><td>assurance-2-label</td><td>Separate ingress</td></tr>
<tr><td>assurance-2-fact</td><td>DNS managed externally, not by API.</td></tr>
<tr><td>assurance-2-source</td><td>gateway-preview.yaml</td></tr>
<tr><td>n0</td><td>Patch run selector on pod; Create Service + HTTPRoute</td></tr>
<tr><td>n1</td><td>Wait for managed DNS; Check Gateway + application</td></tr>
<tr><td>n2</td><td>Run-scoped capability host; Keepalive via API</td></tr>
<tr><td>n3</td><td>HTTPS host match; HTTPRoute selects Service</td></tr>
<tr><td>n4</td><td>Service :80 → public port; Routes to bound sandbox pod</td></tr>
<tr><td>n5</td><td>Live preview: TCP forwarder; 0.0.0.0 → loopback app</td></tr>
<tr><td>groups</td><td>CONTROL: PROVISION + PROBE; GATEWAY DATA PATH</td></tr>
</tbody></table>
</details>

<details id="diagram-context-sandbox-browser-preview-fig2" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Only approved requests reach publication</td></tr>
<tr><td>takeaway</td><td>Run access, operator approval and HTTPS readiness are different gates.</td></tr>
<tr><td>Run-bound preview tool</td><td>Run-bound preview tool</td></tr>
<tr><td>Run-bound preview tool</td><td>port + optional session_id</td></tr>
<tr><td>Run-bound preview tool</td><td>PreviewPublishTool provider</td></tr>
<tr><td>API preview endpoint</td><td>API preview endpoint</td></tr>
<tr><td>API preview endpoint</td><td>Validate run and target port</td></tr>
<tr><td>API preview endpoint</td><td>Contributor / internal service</td></tr>
<tr><td>AgentPreviewGate</td><td>AgentPreviewGate</td></tr>
<tr><td>AgentPreviewGate</td><td>Explicit auto or human approval</td></tr>
<tr><td>AgentPreviewGate</td><td>No authority from tool closure</td></tr>
<tr><td>Denied / expired</td><td>Denied / expired</td></tr>
<tr><td>Denied / expired</td><td>403 / 408 respectively</td></tr>
<tr><td>Denied / expired</td><td>No StartPreview invocation</td></tr>
<tr><td>Approved only</td><td>Approved only</td></tr>
<tr><td>Approved only</td><td>Recheck active run</td></tr>
<tr><td>Approved only</td><td>Process health if session supplied</td></tr>
<tr><td>Shared start path</td><td>Shared start path</td></tr>
<tr><td>Shared start path</td><td>Gateway publication or local mode</td></tr>
<tr><td>Shared start path</td><td>Failure never yields ready URL</td></tr>
<tr><td>arrow-1</td><td>POST</td></tr>
<tr><td>arrow-2</td><td>request</td></tr>
<tr><td>arrow-3</td><td>reject</td></tr>
<tr><td>arrow-4</td><td>grant</td></tr>
<tr><td>arrow-5</td><td>start</td></tr>
<tr><td>note-0</td><td>Denial and expiry terminate before publication; expiry supports fresh approval.</td></tr>
<tr><td>note-1</td><td>Gateway URL only after HTTPS validation; local fallback is API-host loopback.</td></tr>
<tr><td>note-2</td><td>Process session_id is distinct from the Gateway capability token.</td></tr>
<tr><td>notes</td><td>Denial and expiry terminate before publication; expiry supports fresh approval.; Gateway URL only after HTTPS validation; local fallback is API-host loopback.; Process session_id is distinct from the Gateway capability token.</td></tr>
</tbody></table>
</details>
