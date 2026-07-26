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

A preview is just three small Kubernetes objects the API creates at runtime, chaining the shared gateway to
the run's pod:

![End-to-end flow: Browser, Preview Gateway, API orchestrator, HTTPRoute, ClusterIP Service, Sandbox pod, TcpPortForwarder, Preview app](../diagrams/sandbox-browser-preview-fig1.png)

<!-- Rendered from ../diagrams/src/sandbox-browser-preview-fig1.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

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
Failed preview paths best-effort stop the supervised process and dispose the forwarder, so preview failures do
not leak listeners and never block human review ([`PreviewStep.cs:154`](#source), [`PreviewRunner.cs:776`](#source)).

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
  now + `IdleTimeoutMinutes` (**30 min** default). The frontend pings `keepalive` ~every 60 s, and
  `KeepAliveAsync` ([`SandboxPreviewService.cs:206`](#source)) bumps the annotation. Stop pinging and the
  preview lapses within the idle window.
- **Hard lifetime cap.** `preview-max-until` = now + `MaxLifetimeHours` (**8 h** default). A preview is
  always reaped after this, regardless of keepalive.
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
  preview ports. API pods are intentionally not in this data path; an API-side `podIP:{target_port}` probe
  would be denied by policy. Out-of-range ports are rejected by the endpoint, so we never provision a preview
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
the UI — via the `start_preview` agent tool. The tool is produced by `AgentweaverApiTools.Build` and is
**run-scoped**: it is only offered when a `runId` is captured in the tool closure
([`AgentweaverApiTools.cs:245`](#source)), and the model supplies **only** the port. Because the `runId` is
server-bound, the agent physically cannot target another run.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
sequenceDiagram
    participant Agent as Agent (in sandbox)
    participant API as API (start_preview endpoint)
    participant Gate as AgentPreviewGate
    participant Op as Operator / auto-approve
    participant Preview as SandboxPreviewService
    Agent->>API: POST /api/runs/{runId}/sandbox/preview { target_port }
    API->>API: IsOwnerOrServiceCaller (owner OR run's own agent)
    API->>Gate: RequestApprovalAsync(runId, port)
    alt auto-approve source on
        Gate-->>API: Approved (immediate)
    else human-gated
        Gate->>Op: emit tool.approval_required (request_id)
        Op-->>Gate: POST /tool-approvals (grant) — or timeout
        Gate-->>API: Approved / DeniedOrTimedOut
    end
    API->>Preview: StartPreviewAsync(runId, port)  %% same path as operator route
    Preview-->>API: preview_url
    API-->>Agent: { preview_url, … }
```

1. **The tool POSTs** `{ target_port }` to `POST /api/runs/{runId}/sandbox/preview`
   ([`SandboxEndpoints.cs:57`](#source)) and returns the response `preview_url` back to the agent.
2. **Authorization** accepts the run's owner **or** the run's own agent callback. The agent callback
   authenticates with the shared service key, which resolves to the hardcoded internal-service identity
   (`ProjectAuthorization.InternalServiceUser` = `"agentweaver-internal"`) or, if configured, the `Auth:User`
   identity — not the human owner — so the human-oriented `IsOwner` check would block it; `IsOwnerOrServiceCaller`
   ([`EndpointHelpers.cs:40`](#source)) delegates to `ProjectAuthorization.IsInternalServiceCaller` to admit that
   service identity **without** weakening security — the server-bound `runId` means a service caller can only
   ever act on the run its agent is executing. (Issue #529: this previously checked only the configured
   `Auth:User` value, which no deployment sets, so `start_preview` 403'd for every agent callback in production.)
3. **The HITL gate** `AgentPreviewGate.RequestApprovalAsync` ([`AgentPreviewGate.cs:85`](#source)) is the
   human-in-the-loop seam. It reuses the same `IToolApprovalGate` primitive as `web_fetch`: it emits a
   `tool.approval_required` card ([`AgentPreviewGate.cs:103`](#source)) and suspends until an operator grants
   via `POST /api/runs/{runId}/tool-approvals` or the 5-minute window times out.
4. **Auto-approve** short-circuits the wait when any of these is on
   ([`AgentPreviewGate.cs:75`](#source)): the global `Sandbox:Preview:AutoApprove` config / env
   `SANDBOX_PREVIEW_AUTO_APPROVE` ([`AgentPreviewGate.cs:125`](#source)), the per-run `AutoApproveTools`
   operator option, or an existing scoped allow policy. Production stays human-gated (default `false`); the
   flag exists so an automated demo can run unattended.
5. **On approval** the endpoint runs the **same** `StartPreviewForRunAsync` path
   ([`SandboxEndpoints.cs:217`](#source)) as the operator route — Gateway-direct preview when enabled,
   `kubectl` fallback otherwise — and returns `preview_url`.

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
real human identity and the owner check (`IsOwnerOrServiceCaller`) applies unchanged. The auto-approve flag still
governs unattended runs; production stays human-gated.

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
