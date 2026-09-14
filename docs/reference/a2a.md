# A2A Transport — Reference

See A2A remotes one leaf turn, not team coordination for the shared visual model.

::: warning Preview dependency on the hot path
The checked-in A2A dependencies remain preview packages on the remote-turn path. The client `Microsoft.Agents.AI.A2A` is pinned to `1.19.0-preview.260822.1`; host packages `Microsoft.Agents.AI.Hosting.A2A` and `.AspNetCore` use `1.11.1-preview.260625.1`. Workflow and Copilot integration packages use stable `1.19.0`. These are repository pins, not a statement about every upstream release.

**Mitigations (all required):** pin the exact validated client/host combination by **version + hash**; gate the whole path behind `Sandbox:AgentExecutionMode`; and treat the **`in-api` mode as the rollback path** — not a second wire protocol. Advance the pin only after validating against the next A2A release, tracking the line to GA.
:::

This reference catalogues the A2A surface Agentweaver uses, the message-mode semantics, the agent card, and the H1–H7 security model. For the design reasoning behind these choices, read the [A2A bridge deep dive](../deep-dive/a2a-bridge.md). For the pod lifecycle, see [Sandbox pods reference](./sandbox-pods.md) and [Sandbox pod execution](../deep-dive/sandbox-pod-execution.md).

## 1. Package surface

| Side | Package | What it provides |
|---|---|---|
| **Host (in-pod)** | `Microsoft.Agents.AI.Hosting` + `Microsoft.Agents.AI.Hosting.A2A` | `builder.AddAIAgent(name, factory, lifetime).AddA2AServer(...)` to register the agent, then `app.MapA2AHttpJson(builder, path)` — exposes an existing `AIAgent` over the A2A **HTTP+JSON** transport as ASP.NET Core endpoints |
| **Client (worker)** | `Microsoft.Agents.AI.A2A` | `A2AAgent : AIAgent`, constructed from an `A2AHttpJsonClient` — wraps a remote A2A HTTP+JSON endpoint as a local `AIAgent` |
| **Underlying SDK** | `A2A` | `A2AHttpJsonClient` (HTTP+JSON) and `A2AClient` (JSON-RPC), both `IA2AClient` — the raw protocol clients the wrapper sits on |

The worker never drops to the raw `A2A` client. It consumes the remote endpoint as an `AIAgent` via `A2AAgent` (over `A2AHttpJsonClient`), which is why the worker's turn executor is untouched: both the local and remote leaf are the same `AIAgent` abstraction.

> **HTTP+JSON, not JSON-RPC.** A2A defines two wire transports. Agentweaver uses the **HTTP+JSON** profile on both ends — `MapA2AHttpJson` on the host, `A2AHttpJsonClient` on the worker. The JSON-RPC `A2AClient` is **not** interchangeable: it posts to the base path and 404s against the HTTP+JSON routes.

### Pinning

Keep declared package versions and committed lock-file content hashes together. Client and host currently have different version stamps; validate the complete client/host combination before changing either side.

## 2. Endpoints

`MapA2AHttpJson(builder, path)` publishes a fixed, narrow surface under the configured path (default `/a2a/agent`, on port `8088`):

| Endpoint | Method | Purpose |
|---|---|---|
| `…/v1/message:stream` | `POST` | Streaming agent turn over SSE. The only data-plane endpoint. **Requires `Authorization: Bearer <turn-token>`.** |
| `…/v1/card` | `GET` | The agent card — capability + security-scheme discovery. A non-empty `CardBearerToken` enables its separate bearer gate. |

So at the default path the live routes are `POST /a2a/agent/v1/message:stream` and `GET /a2a/agent/v1/card`. The hosted agent is **not** `CopilotAIAgent` directly: it is `A2ATurnBridgeAgent` (a `DelegatingAIAgent` registered under the MAF name `agentweaver-pod`) wrapping the pod's singleton `CopilotAIAgent` through a purpose-routing runner. Workflow purposes use the Copilot runner; Operator Assistant uses its MCP chat runner. Provider configuration selects a run-bound Copilot capability or BYOK. The A2A server is configured with `AgentRunMode.DisallowBackground` (turns are synchronous streams, never detached tasks). A startup readiness gate returns `503` for every route except `/healthz` and `/configure` until `AgentHostStartupService` has finished the pod's run-scoped `CopilotAIAgent.SetupAsync`. Warm-pool pods start in standby and run setup only after `/configure`.

`message:stream` is not protected by NetworkPolicy alone. `KubernetesSandboxExecutor` generates a 256-bit random turn token for each run, sends it to the claimed warm pod in `POST /configure`, and registers it in `IAgentHostTurnTokenRegistry`. `RemoteAgentProxy` reads the token and sets `Authorization: Bearer <turn-token>` on all turn calls. Because each pod has its own configured token, a token stolen from one run cannot be reused against another run's AgentHost. `/configure` itself is not bearer-protected because it delivers the token; scoped NetworkPolicy is the guard.

Messages are keyed by `messageId` and `contextId` (the worker uses the run id as `contextId`). The server maintains a per-`contextId` conversation history, which Agentweaver treats as **ephemeral** (see §4). The surface is intentionally bounded: a fixed streaming endpoint plus a discovery endpoint, narrower than any ad-hoc executor surface.

## 3. The agent card (`/v1/card`)

Card discovery and turn execution use distinct configured application-layer gates.

- The card endpoint is `GET {A2APath}/v1/card`. A non-empty `AgentHost:CardBearerToken` requires a matching bearer token; an empty value disables that gate. The options default is empty. `AgentHost:Security:GateCardEndpoint` is not the value the middleware checks. Turn submission instead checks the runtime `TurnBearerToken` delivered through configure. Neither bearer is a TLS identity, and the reviewed implementation does not establish OAuth2 discovery or SPIFFE identity.
- **Minimized.** The card exposes only what the worker needs to bind the transport — no broad capability advertising, no surplus metadata.

Bearer authorization and TLS identity are independent controls. The deployment matrix in H1 distinguishes plain base configuration from production-overlay mTLS; neither establishes a SPIFFE integration.

## 4. Message-mode semantics

Agentweaver uses A2A in **message/stream mode only**.

| Concern | A2A capability | Agentweaver's use |
|---|---|---|
| Per-turn streaming | `message:stream` (SSE) | **Used.** One ordered stream per turn carries updates, token deltas, and `RunEvent` `DataPart`s. |
| Task lifecycle | `submitted`/`working`/`input-required`/`completed` + continuation tokens | **Not used.** No A2A task is ever opened — no task-model tax. |
| HITL / `input-required` | task pauses awaiting input | **Not used over the wire.** HITL is a MAF `RequestPort` in the worker graph, not an agent turn. |
| Conversation history | server-side, keyed by `contextId` | **Bypassed.** Ephemeral; dies with the pod. Durable resume is Agentweaver's checkpoint store. |
| Stream replay | none (no Last-Event-ID) | **Not relied on.** A mid-turn drop re-drives the turn from the last checkpoint. |

### What crosses the wire

1. The bridge reads revision state and applies per-turn system-prompt context, project/agent identity, API address, and API credential before execution. Prompt context is layered over the pod's startup environment and includes the worker-assembled charter, memory, and assigned skills. One-time run provisioning still occurs through `/configure`.

Streaming returns assistant text and in-band `RunEvent` DataParts (`application/x-agentweaver-run-event+json`). Writable pod-local turns can also return a `PreparedWriteback` descriptor, captured separately from events.
2. The agent's **streaming output** — assistant text deltas, accumulated on the worker into the turn result.
3. The **`RunEvent` side-channel**, encoded as A2A `DataContent` parts (media type `application/x-agentweaver-run-event+json`, via `RunEventDataPartCodec`) on `message:stream`, decoded back into `RunEvent`s on the worker. These are forwarded **in-band** on the same stream today; an external-bus fan-out is a future option, not what ships.

The `RunEvent` codec is the only Agentweaver-owned shim, and it is transport-independent (any transport would need it). Ordering within a turn is preserved because `message:stream` is a single ordered SSE stream, re-injected on the worker under a monotonic sequence allocator.

### What does **not** cross the wire

- MAF `WorkflowEvent`s (executor-invoked/completed, request-info) — emitted by the worker graph around the leaf.
- HITL `RequestPort` suspend/resume — a worker graph construct.
- Checkpoints / session blobs — persisted out-of-band to the DB-backed checkpoint store.
- Pod-local writable implementation turns prepare Git writeback and return a `PreparedWriteback` DataPart. The worker validates and applies that receipt; not all commit/diff work runs on the shared PVC.

## 5. Security model (H1–H7)

H1-H7 describe intended security and operational controls. Their implementation and configuration status differ; distinguish code defaults, Kubernetes base configuration, production-overlay configuration, and requirements not established by a repository-only review.

| Gate | Requirement |
|---|---|
| **H1 — Transport identity** | Code defaults enable mTLS. Kubernetes base disables it on AgentHost and API/worker callers; the production overlay enables HTTPS and client certificates on both ends. Mounted certificates and pinned CAs are not a claim of SPIFFE identity; the client ignores pod-IP hostname mismatch. |
| **H2 — Scoped ingress** | The A2A NetworkPolicy permits same-namespace API and worker pods to TCP/8088. Policies are additive: preview-gateway ingress permits TCP/3000-9000, including 8088. NetworkPolicy is not a gateway hop. |
| **H3 — A2A app-layer authz** | Turn submission checks the configured run bearer; card discovery checks the separate CardBearerToken option. Either middleware gate is disabled when its corresponding value is empty. |
| **H4 — Bounded listener** | Kestrel has declared connection/body/header/keepalive limits. Control calls use a finite timeout; streaming uses an infinite HTTP timeout with worker total/read-idle deadlines. A general transport heartbeat is not established by the configuration field alone. |
| **H5 — Idempotent resume** | Checkpoint management and durable run events remain in the worker/orchestration tier. PostgreSQL checkpoints support cross-replica reads; event persistence deduplicates by run and sequence. This is not A2A replay or a blanket exactly-once guarantee for model/tool effects. |
| **H6 — No egress broadening** | Ingress does not grant egress. Current sandbox egress includes DNS, explicit platform-service rules, and public HTTPS excluding configured private/link-local ranges; it is not a per-run Git-host-only allowlist. |
| **H7 — Pinned preview** | Client/host preview versions and lock hashes are pinned separately. in-api is the code fallback; Kubernetes base selects pod-per-run. Startup configuration rollback is not hot reload or a second wire protocol. |

Notes on the gates:

- Bearer tokens and mTLS are independent controls; label the configured deployment rather than asserting universal mTLS.
- Listener limits and worker total/read-idle streaming deadlines are distinct.
- Checkpoints and durable event sequencing are platform-owned, not pod persistence.

## 6. The `-preview` caveat, the rollback flag, and degraded mode

This section is the operational contract for running a preview dependency on the hot path.

### The caveat (stated prominently)

`in-api` is the code fallback. Kubernetes base API and worker deployments explicitly select `pod-per-run`; production additionally enables mTLS. Configuration files do not establish live deployment or soak status.

### Rollback is a flag, not a second wire

| `Sandbox:AgentExecutionMode` | Behavior |
|---|---|
| `in-api` *(code fallback)* | Agent turns run **in-process** in the worker exactly as today. This is the in-process workflow execution mode and configuration rollback from remote workflow turns. |
| `pod-per-run` | Agent turns are remoted to a sandbox pod over the A2A transport. |

Rollback changes `Sandbox:AgentExecutionMode` to `in-api` and deploys/restarts the applicable process configuration. Execution-mode selection is registered during startup; hot switching is not established and no second turn wire protocol is introduced.

### kube-exec-stdio is the degraded-mode fallback only

A `kube-exec-stdio` channel exists for its own per-command purposes and remains available as a **degraded-mode fallback only**. It is **not** a wire transport for agent turns and is **not** the rollback path. The rollback path is `in-api`. Do not configure exec-stdio as a live alternate agent-turn transport.

## 7. Quick configuration reference

| Setting | Values | Meaning |
|---|---|---|
| `Sandbox:AgentExecutionMode` | in-api code fallback; pod-per-run in Kubernetes base | Startup selection, changed through deployment configuration. |
| `Sandbox:ReleasePodOnSuspend` | `true` *(default)* / `false` | Checkpoint-and-release the pod when the graph suspends on a `RequestPort` or coordinator idle. |
| `Sandbox:AgentHost:RequireMtls` | true in code / production overlay; false in base | Configure host and API/worker client transport consistently. |
| `Sandbox:AgentHost:Port` | `8088` *(default)* | Pod A2A listener port. |
| `Sandbox:AgentHost:A2APath` | `/a2a/agent` *(default)* | Base A2A path; routes are `{path}/v1/message:stream` and `{path}/v1/card`. Must match the pod's `AgentHost:A2APath`. |
| Runtime turn token | generated per run | 256-bit random bearer required on `POST …/v1/message:stream`; delivered to the claimed warm pod by `POST /configure` and stored worker-side in `IAgentHostTurnTokenRegistry`. Empty is local/test only. |
| `AgentHost:CardBearerToken` | token / empty (code default) | Non-empty requires a matching bearer on `v1/card`; empty disables the card gate. |
| `/configure.copilotCredential` | snapshot reference, access token, expiry | Live run-bound Copilot capability, delivered once. Another run or expired capability fails closed; no ambient token-store fallback. |
| `/configure.byokProviderConfiguration` | provider configuration / null | Separate BYOK boundary; BYOK launch does not transmit a Copilot capability. |
| `/configure.repositoryAccessToken` / `mcpBrokerToken` | optional, purpose-scoped | Separate repository/MCP authorities, not the card bearer or model credential. |

### Pod-per-run lifecycle

The executor waits for binding, records the claimed pod and turn token, resolves its IP, and probes `/healthz` until the listener is reachable. A warm pod reports `standby`. It then posts one-time `/configure` and awaits setup completion; a ready configured pod reports `ready`.

See the [A2A bridge deep dive](../deep-dive/a2a-bridge.md) for how these settings interact with checkpointing and resume, and the [distributed-agents experience doc](../experience/a2a-distributed-agents.md) for what they change operationally.

<details id="diagram-context-reference-a2a-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>A2A: a remote turn, not a remote workflow</td></tr>
<tr><td>subtitle</td><td>Transport and capabilities stop at the pod boundary. Checkpoints stay with orchestration.</td></tr>
<tr><td>platform-title</td><td>PLATFORM / AKS</td></tr>
<tr><td>platform-subtitle</td><td>API and worker are permitted AgentHost callers</td></tr>
<tr><td>pod-title</td><td>PER-RUN AGENTHOST POD</td></tr>
<tr><td>pod-subtitle</td><td>Kata boundary / listener :8088 / no workflow DB</td></tr>
<tr><td>API caller</td><td>API caller</td></tr>
<tr><td>API caller</td><td>Sandbox lifecycle and control</td></tr>
<tr><td>Worker / remote proxy</td><td>Worker / remote proxy</td></tr>
<tr><td>Worker / remote proxy</td><td>Owns the orchestration graph</td></tr>
<tr><td>Worker / remote proxy</td><td>RemoteAgentProxy</td></tr>
<tr><td>Checkpoint manager</td><td>Checkpoint manager</td></tr>
<tr><td>Checkpoint manager</td><td>JSON workflow state / resume</td></tr>
<tr><td>Durable platform store</td><td>Durable platform store</td></tr>
<tr><td>Durable platform store</td><td>Checkpoints + run events</td></tr>
<tr><td>Configure once</td><td>Configure once</td></tr>
<tr><td>Configure once</td><td>Live Copilot capability OR BYOK</td></tr>
<tr><td>Configure once</td><td>/healthz, then /configure</td></tr>
<tr><td>Turn gate</td><td>Turn gate</td></tr>
<tr><td>Turn gate</td><td>Run-scoped bearer</td></tr>
<tr><td>Card gate</td><td>Card gate</td></tr>
<tr><td>Card gate</td><td>Separate option</td></tr>
<tr><td>A2ATurnBridgeAgent</td><td>A2ATurnBridgeAgent</td></tr>
<tr><td>A2ATurnBridgeAgent</td><td>Per-turn prompt / skills / API context</td></tr>
<tr><td>A2ATurnBridgeAgent</td><td>purpose-routing runner</td></tr>
<tr><td>Card gate</td><td>Empty CardBearerToken: card gate disabled.</td></tr>
<tr><td>transport-title</td><td>TRANSPORT CONTROLS</td></tr>
<tr><td>tls</td><td>Mounted certificates; pinned CA validation. Base: mTLS off. Production overlay: on.</td></tr>
<tr><td>policy</td><td>NetworkPolicy is not a gateway hop. API + worker: TCP/8088.</td></tr>
<tr><td>additive</td><td>Policies are additive: preview ingress range also includes 8088.</td></tr>
<tr><td>credential-note</td><td>Separate authorities</td></tr>
<tr><td>credential-detail</td><td>TLS identity, card token, turn token and model capability are not interchangeable. Pod returns text / RunEvent DataParts; writable turns can return a writeback receipt.</td></tr>
<tr><td>Configure once</td><td>health / configure</td></tr>
<tr><td>Turn gate</td><td>turn / ordered stream</td></tr>
<tr><td>Checkpoint manager</td><td>save / resume</td></tr>
</tbody></table>
</details>

<details id="diagram-context-canonical-agent-communication-a2a" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>A2A remotes a leaf turn, not the graph</td></tr>
<tr><td>takeaway</td><td>Setup and task cross to AgentHost; assistant output and structured events return.</td></tr>
<tr><td>Workflow graph</td><td>Workflow graph</td></tr>
<tr><td>Workflow graph</td><td>Host owns gates/checkpoints</td></tr>
<tr><td>Workflow graph</td><td>Five factory-created leaf types</td></tr>
<tr><td>RemoteAgentProxy</td><td>RemoteAgentProxy</td></tr>
<tr><td>RemoteAgentProxy</td><td>Build setup DataContent</td></tr>
<tr><td>RemoteAgentProxy</td><td>Task TextContent in same message</td></tr>
<tr><td>AgentHost bridge</td><td>AgentHost bridge</td></tr>
<tr><td>AgentHost bridge</td><td>message:stream over HTTP+JSON</td></tr>
<tr><td>AgentHost bridge</td><td>Apply per-turn context</td></tr>
<tr><td>Caller event pipeline</td><td>Caller event pipeline</td></tr>
<tr><td>Caller event pipeline</td><td>Decoded structured events</td></tr>
<tr><td>Caller event pipeline</td><td>Durable state outside pod</td></tr>
<tr><td>Proxy stream decoder</td><td>Proxy stream decoder</td></tr>
<tr><td>Proxy stream decoder</td><td>Output + RunEventDataPart</td></tr>
<tr><td>Proxy stream decoder</td><td>Check definitive turn end</td></tr>
<tr><td>Leaf runtime</td><td>Leaf runtime</td></tr>
<tr><td>Leaf runtime</td><td>Execute provider/tool loop</td></tr>
<tr><td>Leaf runtime</td><td>Stream updates and events</td></tr>
<tr><td>arrow-1</td><td>invoke</td></tr>
<tr><td>arrow-2</td><td>send</td></tr>
<tr><td>arrow-3</td><td>run</td></tr>
<tr><td>arrow-4</td><td>stream</td></tr>
<tr><td>arrow-5</td><td>append</td></tr>
<tr><td>note-0</td><td>Claim/configure is a separate lifecycle, completed before this exchange.</td></tr>
<tr><td>note-1</td><td>EOF alone is not successful completion; structured failures remain failures.</td></tr>
<tr><td>notes</td><td>Claim/configure is a separate lifecycle, completed before this exchange.; EOF alone is not successful completion; structured failures remain failures.</td></tr>
</tbody></table>
</details>
