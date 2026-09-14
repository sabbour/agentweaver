# Sandbox browser preview

Sometimes an agent starts a **server inside its sandbox pod** — a dev server, a freshly built web app, an
API it stood up, a debug endpoint — and you want to actually *open it in your browser* and look. Because
each run executes in its own isolated pod, that server isn't reachable by default. The **sandbox browser
preview** is the supported way to reach it: a live preview served at a unique HTTPS URL that routes straight
to the run's own pod, scoped to that one run.

This page walks through the user experience. For the API see the
[reference](../reference/sandbox-browser-preview.md); for how the proxy works under the hood see the
[deep dive](../deep-dive/sandbox-browser-preview.md).

![API provisions a Service and HTTPRoute; browser preview traffic flows through the Gateway to the run's pod](../diagrams/sandbox-browser-preview-fig1.png)

<!-- Shared diagram: sandbox-browser-preview-fig1; owned by deep-dive-execution.
     Consume the stable canonical PNG; do not edit a local duplicate. -->

## When the Preview button is available

A **Preview Sandbox** button appears in the coordinator run page when **both** are true:

- the run is using the **Kubernetes sandbox** (the run's `sandbox.selected` event reports backend
  `kubernetes-sandbox-claim`), **and**
- a preview lifecycle state has been recorded or an existing preview session is available.

Kubernetes placement alone does not make the header button appear. The current UI does not hide it
solely because the run is terminal: a retained preview can still be inspected. An operator API request
can also start a post-run preview if the required pod is still bound; agent/platform publication
remains run-bound.

## Step by step

1. **Click Preview.** The **Sandbox Preview** dialog opens.
2. **Pick a port.** Enter the port your app is listening on *inside* the sandbox pod. The **Target port
   (inside sandbox)** field defaults to `3000`. It must be within the allowed preview range
   (**3000–9000** by default); other values are rejected.
3. **Start.** Click **Start**. The app calls `POST /api/runs/{runId}/sandbox/port-forward` with that port.
   The API resolves the run's bound pod from cluster state and provisions a per-preview HTTPRoute and
   ClusterIP Service wiring the shared preview gateway to your pod.
4. **See it become active.** The dialog confirms **"Preview active for port {target_port} on pod
   {pod_name}"** and shows the session id.
   - When a `preview_url` is returned (the normal case in-cluster), the dialog embeds the live preview in an
     **iframe** (with `referrerPolicy="no-referrer"`) and offers an **Open preview** button that opens it in
     a new tab.
   - On a local/dev backend where the gateway path is off, the dialog notes that no proxied preview URL was
     returned.
5. **Keepalive is bounded.** While the run page has an active session with a keepalive URL, it sends
   keepalive about every 60 seconds. This is not gated on the dialog being open. Expiry is bounded by the
   original maximum lifetime; watching does not extend the hard cap.
6. **Stop when done.** Click **Stop** to tear it down (`DELETE` on that session). **Close** just dismisses
   the dialog.

## Agent-initiated preview (with your approval)

An agent can also open a preview **for you**, mid-run, when it has just started a server and wants to show
you the result — without you opening the dialog or typing a port. The agent calls a `start_preview(port)`
tool; instead of exposing the server silently, that request raises a **human-in-the-loop approval** on the
run timeline:

- A **"the agent wants to expose a preview server on port N"** approval card appears (the same kind of card
  used for the agent's URL-fetch requests). **Approve** it and the agent gets back the live `preview_url`;
  the preview behaves exactly like one you started yourself (same URL, same auto-expiry, same Stop).
- The approval remains visible in the notification badge, a persistent toast, and the run timeline.
  The timeline card shows when it expires.
- If you don't approve within the project's configured window (24 hours by default), the request
  lapses. Choose **Retry approval** on the expired card or preview status to create a fresh approval
  request. Agentweaver reuses the healthy server process instead of restarting the run or executing
  the preview command again.
- The agent can only ever request a preview for **its own run** — the run is bound server-side, so a
  `start_preview` call can't reach another run's pod.

Operators running automated demos can set `SANDBOX_PREVIEW_AUTO_APPROVE=true` (or the per-run
auto-approve-tools option) to grant these requests automatically. Project owners can set the manual
approval expiry and preview lifetime to 1–1440 minutes in **Project settings → Sandbox policy**;
both default to 24 hours. Preview lifetime is a single expiration and hard-cap setting. The deployment setting
`Sandbox:Preview:ApprovalTimeoutMinutes` (or `SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES`) is retained
as a 24-hour-default fallback for legacy runs that are not associated with a project. In normal use
the approval stays in your hands.

## Build & Test preview

Workflows that include the platform-owned **Build & Test** step can also produce a browser preview. After
an approved **or request-changes** Build & Test verdict, the platform attempts to start the app/service,
discover its actual port, and register a preview URL through the approval flow. Declined verdicts skip it;
preview failure does not block human review. It no longer injects `PORT=3000` or `--port`; apps use their framework default or honor
`process.env.PORT` if they already support it. Port discovery is dependency-free: AgentHost reads app log hints
and the sandbox pod's `/proc/net/tcp` plus `/proc/net/tcp6` socket tables, so it works even when the image lacks
`ss` and when Node binds IPv6-any (`::`). Before registration, AgentHost fronts the observed app port with a
pod-local TCP forwarder on an allowed public port (`3000-9000`), so even a loopback-only app is reachable from
the Gateway.

The preview backend resolves
either the run's AgentHost claim or a retained command-sandbox claim, so previews work whether the server was
started in the `agent-{runId}` pod-per-run sandbox or the `run-{runId}` Build & Test command sandbox
(`apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs:28`, `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:432`).

## What to expect

- **Kubernetes-only.** The preview routes into the run's own sandbox pod. On local/dev runs there is no
  claim pod, so the button doesn't appear — the same "this is a cluster feature" boundary as the pod pill.
- **A real, public HTTPS URL.** Unlike the older loopback design, the preview is reachable at
  `https://{token}-preview.{ZoneSuffix}`. The URL is an **unguessable capability link** (128-bit token):
  anyone with the URL can open it, so don't share it. It is short-lived and auto-expires.
- **Scoped to this run's pod.** A preview reaches only the run's own sandbox pod, never another run's.
  Keepalive and stop verify the token actually belongs to the run before acting.
- **Auto-expiry.** Project lifetime defaults to **1440 minutes (24 hours)** and can be set to
  1–1440 minutes. Initial expiry and maximum lifetime are both calculated from that same setting at
  creation; keepalive cannot extend the hard cap. Expiry, explicit **Stop**, or pod loss ends availability.
  The default retention policy permits a preview to outlive its run only while the required pod/process
  resources remain available. The route state is durable across API restarts.
- **Platform previews handle loopback binds.** The Build & Test live-preview path runs the app and forwarder
  inside the sandbox pod and registers the forwarder's `0.0.0.0` public port, so apps that only bind
  `127.0.0.1` can still be previewed. Manual previews still expose the port you enter directly, so prefer
  all-interface binds there.
- **Gateway is the reachability test.** The API does not probe the sandbox pod directly. Platform previews
  first check readiness in-pod; registration then waits for publication through the generated HTTPS
  Gateway URL. Opening that returned URL exercises the same path.
- **Failure is actionable, not blocking.** If the app exits, no listening port appears, observe hits an
  unexpected error, or the forwarder cannot make the app reachable, you see **Preview unavailable** with a
  reason such as `process_exited:exit={code}`, `no_listening_port_discovered`, `observe_error`,
  `bound_unreachable`, or `no_public_port_available`; review can continue.

## Related reading

- [Sandbox browser preview — Reference](../reference/sandbox-browser-preview.md) — routes, DTO fields, config, status codes.
- [Sandbox browser preview — Deep Dive](../deep-dive/sandbox-browser-preview.md) — the reverse proxy, lifecycle, and cleanup.
- [Live-preview provisioning](./live-preview-provisioning.md) — the Build & Test preview review flow.
- [Sandbox pod execution experience](./sandbox-pod-execution.md) — the pod pill and the pod-per-run model.
- [Runs, board & live inspection](./runs-board-watch.md) — where embedded run inspection lives.

<!-- diagram-context:sandbox-browser-preview-fig1:start -->
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
<!-- diagram-context:sandbox-browser-preview-fig1:end -->
