# A2A Transport — Reference

::: warning Preview dependency on the hot path
The A2A transport is built on the `Microsoft.Agents.AI.A2A` and `Microsoft.Agents.AI.Hosting.A2A(.AspNetCore)` package line. **Every published version of that line is `-preview`** (for example `1.9.0-preview.260603.1` through `1.11.1-preview.260625.1`), whereas the workflow runtime it pairs with reached stable `1.9.0`. A2A sits on the agent-execution hot path as the **sole** wire transport, so this preview status is a first-class operational fact, not a footnote.

**Mitigations (all required):** pin one exact known-good build by **version + hash**; gate the whole path behind `Sandbox:AgentExecutionMode`; and treat the **`in-api` mode as the rollback path** — not a second wire protocol. Advance the pin only after validating against the next A2A release, tracking the line to GA.
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

Pin a single A2A build aligned with Agentweaver's existing `Microsoft.Agents.AI.*` line (for example the `…-preview.260603.1` stamp shared with the GitHub Copilot agent package), recorded by **exact version and content hash**. Do not float the version. Upgrading the pin is a deliberate, validated step gated by soak on the execution-mode flag.

## 2. Endpoints

`MapA2AHttpJson(builder, path)` publishes a fixed, narrow surface under the configured path (default `/a2a/agent`, on port `8088`):

| Endpoint | Method | Purpose |
|---|---|---|
| `…/v1/message:stream` | `POST` | Streaming agent turn over SSE. The only data-plane endpoint. **Requires `Authorization: Bearer {AgentHostOptions.TurnBearerToken}`.** |
| `…/v1/card` | `GET` | The agent card — capability + security-scheme discovery. **Authz-gated, not anonymous.** |

So at the default path the live routes are `POST /a2a/agent/v1/message:stream` and `GET /a2a/agent/v1/card`. The hosted agent is **not** `CopilotAIAgent` directly: it is `A2ATurnBridgeAgent` (a `DelegatingAIAgent` registered under the MAF name `agentweaver-pod`) wrapping the pod's singleton `CopilotAIAgent`. The A2A server is configured with `AgentRunMode.DisallowBackground` (turns are synchronous streams, never detached tasks). A startup readiness gate returns `503` for every route except `/healthz` until `AgentHostStartupService` has finished the pod's run-scoped `CopilotAIAgent.SetupAsync`.

`message:stream` is not protected by NetworkPolicy alone. `KubernetesSandboxExecutor` generates a 256-bit random turn token for each run, injects it into the pod as `AgentHost__TurnBearerToken`, and registers it in `IAgentHostTurnTokenRegistry`. `RemoteAgentProxy` reads the token and sets `Authorization: Bearer {per-run token}` on all turn calls. Because each pod has its own token, a token stolen from one run cannot be reused against another run's AgentHost.

Messages are keyed by `messageId` and `contextId` (the worker uses the run id as `contextId`). The server maintains a per-`contextId` conversation history, which Agentweaver treats as **ephemeral** (see §4). The surface is intentionally bounded: a fixed streaming endpoint plus a discovery endpoint, narrower than any ad-hoc executor surface.

## 3. The agent card (`/v1/card`)

The agent card advertises the agent's capabilities (streaming, push notifications) and its security schemes (bearer / OAuth2). In Agentweaver it is:

- **Authz-gated.** `GET /v1/card` requires authorization; there is **no anonymous discovery** of the in-pod agent. The gate is a middleware that rejects any request to `…/v1/card` whose `Authorization` header does not match `Bearer {AgentHostOptions.CardBearerToken}` (an empty token disables the gate for dev/test only).
- **Minimized.** The card exposes only what the worker needs to bind the transport — no broad capability advertising, no surplus metadata.

The card's bearer/OAuth2 scheme is an **app-layer** auth model. It is useful but, on its own, it does **not** satisfy the transport security requirement (see H1). Agent-card bearer/OAuth2 and the per-run `message:stream` bearer must be wrapped by transport-layer identity (mTLS/SPIFFE) and scoped ingress.

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

1. The **turn input** — the first A2A `message:stream` message carries an `AgentSetupParams` `DataPart` (media type `application/x-agentweaver-agent-setup+json`) followed by the task `TextPart`. The pod's bridge reads only the per-turn `IsRevision` flag from it; the run-scoped setup already ran at pod startup.
2. The agent's **streaming output** — assistant text deltas, accumulated on the worker into the turn result.
3. The **`RunEvent` side-channel**, encoded as A2A `DataContent` parts (media type `application/x-agentweaver-run-event+json`, via `RunEventDataPartCodec`) on `message:stream`, decoded back into `RunEvent`s on the worker. These are forwarded **in-band** on the same stream today; an external-bus fan-out is a future option, not what ships.

The `RunEvent` codec is the only Agentweaver-owned shim, and it is transport-independent (any transport would need it). Ordering within a turn is preserved because `message:stream` is a single ordered SSE stream, re-injected on the worker under a monotonic sequence allocator.

### What does **not** cross the wire

- MAF `WorkflowEvent`s (executor-invoked/completed, request-info) — emitted by the worker graph around the leaf.
- HITL `RequestPort` suspend/resume — a worker graph construct.
- Checkpoints / session blobs — persisted out-of-band to the DB-backed checkpoint store.
- Worktree commit and diff — performed on the worker against the shared workspace PVC.

## 5. Security model (H1–H7)

A2A requires an in-pod HTTP listener and an east-west ingress rule onto Kata-isolated pods. The transport ships **only when all of H1–H7 hold**. These are mandatory gates, not recommendations.

| Gate | Requirement |
|---|---|
| **H1 — Transport identity** | TLS **plus** a workload-identity-bound pod server certificate. **mTLS / SPIFFE preferred.** This is now the **production default**: `AgentHostOptions.RequireMtls` / `SandboxAgentOptions.RequireMtls` default to `true`, which selects the `https` scheme and drives the mounted `appsettings.k8s.json` Kestrel endpoint with the workload-bound server cert + `RequireCertificate`. A **plain-HTTP PoC fallback** remains (`RequireMtls=false` → no Kestrel endpoint config, listener binds plain HTTP on the A2A port) and **must not** be used in production. |
| **H2 — Scoped ingress** | A `NetworkPolicy` ingress rule scoped to **worker-pod → sandbox:port only** (expressed for both plain NetworkPolicy and Cilium). Sandbox pods are otherwise ingress deny-all. |
| **H3 — A2A app-layer authz** | `/v1/message:stream` requires the per-run `AgentHostOptions.TurnBearerToken`; `/v1/card` is authz-gated and minimized. No anonymous discovery. |
| **H4 — Bounded listener** | Explicit Kestrel timeout, request-body, and stream limits, **plus SSE heartbeats** so a request-timeout does not kill the long-lived turn stream. |
| **H5 — Idempotent resume** | Resume via Agentweaver's DB checkpoint with **idempotent, sequence-based re-injection**. On mid-turn drop, **re-drive the turn from the last checkpoint** (A2A `message:stream` has no Last-Event-ID replay). |
| **H6 — No egress broadening** | The sandbox egress allowlist is unchanged: model endpoint, the API broker endpoint, and the run's legitimate git remote(s). Everything else is default-deny. A2A adds **no** egress. |
| **H7 — Pinned preview** | The preview library is pinned by exact **version + hash**, gated behind the execution-mode flag, and tracked to GA. **The rollback is the `in-api` flag** (see §6), and H7 records the residual-risk mitigations rather than relying on a live second transport. |

```mermaid
flowchart LR
    Worker[Worker pod\nRemoteAgentProxy] -->|H2: NetworkPolicy\nworker→sandbox:port only| NP{{Scoped ingress}}
    NP -->|H1: TLS + mTLS/SPIFFE\nworkload-identity-bound cert| TLS{{Transport identity}}
    TLS --> TurnAuth{{H3: Authorization\nBearer {per-run token}}}
    TurnAuth --> Listener[AgentHost listener\nH4: Kestrel limits + SSE heartbeats]
    Listener --> MS[/v1/message:stream/]
    Listener --> Card[/v1/card\nH3: authz-gated + minimized/]
    MS --> Agent[CopilotAIAgent\nH6: egress allowlist only]
    Agent -. "H5: durable resume\n(checkpoint store, idempotent)" .-> CK[(DB checkpoint store)]
    Listener -. "H7: preview pinned by version+hash\nrollback = in-api flag" .-> Flag[/Sandbox:AgentExecutionMode/]
```

### Notes on the gates

- **H1 is the one most often misread.** The per-run turn bearer and the agent card's bearer/OAuth2 scheme are app-layer controls; they do not replace transport-layer workload-identity-bound mTLS. Both layers are required in production.
- **H4 exists because of streaming.** A long agent turn is a long SSE stream. Without explicit limits and heartbeats, a default request timeout will sever a healthy turn.
- **H5 is owned by Agentweaver, not A2A.** A2A provides no durable resume; the checkpoint store does. Re-injection must be idempotent so a re-driven turn does not duplicate timeline events.

## 6. The `-preview` caveat, the rollback flag, and degraded mode

This section is the operational contract for running a preview dependency on the hot path.

### The caveat (stated prominently)

Agentweaver runs an **A2A `-preview` library on the agent-execution hot path with no alternate wire transport.** This residual risk is **accepted**, conditioned on the H7 mitigations: exact pinning, the execution-mode flag, and GA tracking. Because of it, `in-api` mode remains the **default** until the pod-per-run path completes soak.

### Rollback is a flag, not a second wire

| `Sandbox:AgentExecutionMode` | Behavior |
|---|---|
| `in-api` *(default)* | Agent turns run **in-process** in the worker exactly as today. This is the instant, fully-tested rollback for any A2A defect or outage. |
| `pod-per-run` | Agent turns are remoted to a sandbox pod over the A2A transport. |

Switching back to `in-api` **requires no second wire transport and no redeploy of a different protocol.** It removes the A2A path entirely. There is no "A2A vs gRPC vs exec-stdio" wire choice on the agent-turn path — A2A is the **sole** transport, and the rollback is the *mode*, not a *protocol*.

### kube-exec-stdio is the degraded-mode fallback only

A `kube-exec-stdio` channel exists for its own per-command purposes and remains available as a **degraded-mode fallback only**. It is **not** a wire transport for agent turns and is **not** the rollback path. The rollback path is `in-api`. Do not configure exec-stdio as a live alternate agent-turn transport.

## 7. Quick configuration reference

| Setting | Values | Meaning |
|---|---|---|
| `Sandbox:AgentExecutionMode` | `in-api` *(default)* / `pod-per-run` | In-process execution vs A2A-remoted pod execution. The `in-api` value is the rollback. |
| `Sandbox:ReleasePodOnSuspend` | `true` *(default)* / `false` | Checkpoint-and-release the pod when the graph suspends on a `RequestPort` or coordinator idle. |
| `Sandbox:AgentHost:RequireMtls` | `true` *(default)* / `false` | `true` = mTLS/`https` (production); `false` = plain-HTTP PoC listener. Mirrored to the pod as `AgentHost:RequireMtls`. |
| `Sandbox:AgentHost:Port` | `8088` *(default)* | Pod A2A listener port. |
| `Sandbox:AgentHost:A2APath` | `/a2a/agent` *(default)* | Base A2A path; routes are `{path}/v1/message:stream` and `{path}/v1/card`. Must match the pod's `AgentHost:A2APath`. |
| `AgentHost:TurnBearerToken` | generated per run | 256-bit random bearer required on `POST …/v1/message:stream`; injected into the pod as `AgentHost__TurnBearerToken` and stored worker-side in `IAgentHostTurnTokenRegistry`. Empty is local/test only. |
| `AgentHost:CardBearerToken` | token / empty | Bearer required on `…/v1/card` (H3); empty disables the gate (dev/test only). |
| `AgentHost:KvTokenMountPath` | path / empty | When set (for example `/mnt/user-tokens`), pod reads the run owner's per-user GitHub OAuth token from the run-scoped Key Vault CSI projection. |
| `AgentHost:UseSharedTokenStore` | `true` / `false` *(default)* | Legacy/local compatibility only. Production AKS stores per-user GitHub tokens in Key Vault and does not mirror them to the shared workspace PVC. |

### Pod-per-run lifecycle

In `pod-per-run` mode the AgentHost pod is **lazily launched per run**. `KubernetesPodAgentEndpointResolver.TryResolveEndpointAsync(runId)` is the single chokepoint every turn passes through (via `RemoteAgentProxy.SetupAsync`); on the first resolve for a run with no registered pod it calls `IAgentHostPodLifecycle.LaunchAgentHostPodAsync(runId)`, which creates a `SandboxClaim`, waits for it to bind, reads the pod IP, registers the `scheme://podIP:port{path}` endpoint, and records that run's turn bearer token. Concurrent launches for the same run are deduped. On suspend, `RunWatchLoopService` calls `ReleaseAgentHostPodAsync(runId)` when `Sandbox:ReleasePodOnSuspend=true`, which also unregisters the token. Outside a cluster the resolver is a no-op and `pod-per-run` fails fast with a clear message (set `in-api`).

See the [A2A bridge deep dive](../deep-dive/a2a-bridge.md) for how these settings interact with checkpointing and resume, and the [distributed-agents experience doc](../experience/a2a-distributed-agents.md) for what they change operationally.
