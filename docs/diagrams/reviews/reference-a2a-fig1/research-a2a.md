## Summary

The audit’s main redesign rationale is correct: **checkpoint persistence belongs to the worker/orchestration tier, not the in-pod agent; API and worker are permitted AgentHost callers; NetworkPolicy is a transport control, not a gateway; and card authorization is separate from the configured turn bearer**. Current implementation adds important qualifications absent from the audit: the production overlay enables mTLS on both ends, the base manifests disable it, card auth defaults to disabled unless `CardBearerToken` is populated, and another additive NetworkPolicy permits preview-gateway traffic over a range containing port 8088. The docs also miss current per-turn prompt propagation, provider/BYOK selection, pod-local writeback, and differing client/server preview-package versions. Sources below are implementation/config/tests—not diagrams.

**Repository/worktree:** `sabbour/agentweaver`, rooted at
`C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring`.

All repository-relative citation paths below resolve under that absolute root. No files or inventories were changed, no agents or external research services were used, and tests were **read, not executed**.

---

# 1. Source-backed model for `reference-a2a-fig1`

## Nodes and boundaries

| ID | Node / boundary | Exact meaning and evidence |
|---|---|---|
| B1 | **Platform orchestration tier** | Contains workflow graph, checkpoint manager, event persistence, endpoint resolution, and remote proxy. `SandboxAgentOptions` explicitly says checkpoint and run-event writes stay in the worker. `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/SandboxAgentOptions.cs:18-32` |
| W1 | **Worker pod** | Primary workflow caller, containing `RemoteAgentProxy` and platform persistence. Worker ingress selector is `app: agentweaver-worker`. `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:35-48` |
| A1 | **API pod / AgentHost control-plane caller** | A separate permitted caller, not a proxy hop through which all worker traffic passes. Selector is `app: agentweaver-api`. Operator Assistant also uses `RemoteAgentProxy` outside the workflow factory. `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:35-48`; `sabbour/agentweaver:docs/deep-dive/a2a-bridge.md:11-21` |
| W2 | **Endpoint resolver / KubernetesSandboxExecutor** | Claims warm pod, resolves IP, records token, probes listener, then configures the newly claimed pod. `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:635-707` |
| W3 | **Turn-token registry** | Stores the run-associated bearer used by the remote proxy; distinct from provider and card credentials. `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:639-647`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:174-180` |
| W4 | **Worker checkpoint manager** | Constructed as `CheckpointManager.CreateJson(store)`. Store factory selects production PostgreSQL versus local/file fallback. `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:181-188` |
| D1 | **Platform durable persistence** | PostgreSQL checkpoint store and durable run events. This is outside the sandbox boundary. `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:181-188,303-350` |
| B2 | **Per-run AgentHost sandbox pod** | Run-scoped mutable state, hosted bridge, provider-backed runtime, and optional local workspace. No direct DB/checkpoint-store edge. The host registers a pod sandbox-policy store with the explicit “no DB in pod” boundary. `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:59-69`; `sabbour/agentweaver:docs/deep-dive/a2a-bridge.md:37-41` |
| H1 | **Kestrel listener `:8088`** | Listener and HTTPS/client-certificate policy, not a separate gateway service. `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostKestrelConfigurator.cs:22-65` |
| H2 | **One-time `/configure` + startup readiness** | Configures run identity, turn bearer, capability/provider, purpose, workspace, approvals; returns conflict on repeat. `/healthz` reports standby/ready. `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:232-296,406-414` |
| H3 | **A2A card auth gate** | Tests the configured **options** value `CardBearerToken`; empty disables this gate. `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:372-384`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostOptions.cs:62-75` |
| H4 | **A2A turn auth gate** | Tests **runtime-state** `TurnBearerToken`, delivered through configure, on POST `…/v1/message:stream`. Empty disables this middleware gate. `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:386-401` |
| H5 | **`A2ATurnBridgeAgent` (`agentweaver-pod`)** | Actual hosted AIAgent, not direct `CopilotAIAgent` exposure. Registration uses singleton lifetime and `AgentRunMode.DisallowBackground`. `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:145-178` |
| H6 | **Purpose routing** | `RoutingPodTurnRunner`: Operator Assistant purpose selects operator runner; other purposes select Copilot runner. Choice occurs per call, after configure determines purpose. `sabbour/agentweaver:apps/Agentweaver.AgentHost/OperatorPodTurnRunner.cs:210-236` |
| H7 | **Provider-backed runtime** | Copilot capability or separate BYOK provider configuration. Do not depict “Copilot-only model provider.” `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:478-525`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs:83-101,165-179` |
| H8 | **Optional pod-local workspace/writeback** | `ImplementationTurn` requires `LocalWritable`; assembly Build/Test requires `LocalReadOnly`. Bridge returns a prepared-writeback DataPart for writable execution. `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:411-446`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:326-368` |

## Edges

1. **Worker / API → pod listener: control plane**
   Label: `healthz → one-time configure; API/worker ingress permitted`.
   Actual ordering is bind → IP/token registration → `/healthz` reachability → `/configure`, **not configure → initial health probe**. Configure’s body includes:

   ```csharp
   runId,
   userId,
   turnBearerToken,
   copilotCredential,
   byokProviderConfiguration,
   modelProviderKey,
   repositoryAccessToken,
   mcpBrokerToken = launchContext.McpBrokerToken,
   ...
   purpose = launchContext.Purpose.ToString(),
   workspaceMode = launchContext.WorkspaceMode.ToString(),
   ```

   `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:635-707,1270-1303`

2. **Worker proxy → turn auth → bridge: turn input**
   Label: `POST /a2a/agent/v1/message:stream · Bearer <run turn token> · HTTP+JSON`.

   ```csharp
   _httpClient.DefaultRequestHeaders.Authorization =
       new AuthenticationHeaderValue("Bearer", turnToken);
   var a2aClient = new A2AHttpJsonClient(podEndpointUri, _httpClient);
   ```

   `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:174-190`

3. **Optional card caller → card gate → card endpoint**
   Label: `GET …/v1/card · separate configured CardBearerToken`.
   Do not suggest the worker fetches a card on every turn; its setup directly constructs `A2AAgent` from the client. Do not conflate the turn bearer and card bearer. `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:372-401`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:187-204`

4. **Bridge → purpose router → selected runner**
   Label: `apply per-turn context; execute task + IsRevision`.
   The bridge applies prompt/skills/memory context, project/agent identity, API address and API credential—not only `IsRevision`:

   ```csharp
   var merged = MergeSystemPromptContext(
       _runtimeState?.PodBaseSystemPromptContext,
       setup.SystemPromptContext);
   _runner.ApplyPerTurnContext(
       merged, setup.ProjectId, setup.AgentName, setup.ApiBaseUrl, setup.ApiKey);
   ```

   `sabbour/agentweaver:apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:453-464`

5. **Bridge → worker proxy: one ordered response stream**
   Label: `assistant output + RunEvent DataParts [+ prepared writeback]`.
   Worker accumulates text, captures writeback envelope separately, decodes events and reassigns sequence through its event pipeline. `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:307-329`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:326-368`

6. **Worker checkpoint manager ↔ durable checkpoint store**
   Label: `checkpoint / resume · PostgreSQL in production; file fallback locally`.
   This is the corrected persistence edge. **No AgentHost → checkpoint-store edge.** `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:181-188`

7. **Worker event pipeline → durable RunEvents**
   Label: `ordered append; idempotent (RunId, Sequence) persistence`.
   Existing-sequence deduplication is not proof that arbitrary replayed model/tool effects are exactly-once:

   ```csharp
   // Durable write-through is idempotent on the unique (RunId, Sequence) index
   foreach (var e in events.OrderBy(e => e.Sequence))
       _ = await stream.AppendAsync(runId, e);
   ```

   `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:318-342`

## Transport-control annotations—not gateway nodes

### Deployment matrix

| Configuration | Execution mode | Transport |
|---|---|---|
| Code fallback | `in-api` | `RequireMtls=true` when remote execution is selected |
| Kubernetes base API + worker | `pod-per-run` | `RequireMtls=false` |
| Kubernetes base AgentHost ConfigMap | Standby/configured pod | `RequireMtls=false`; plain-HTTP fallback when no endpoint block |
| Production overlay | Inherits pod-per-run base | Enables host HTTPS/mTLS **and** sets API/worker `RequireMtls=true` |

Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/SandboxAgentOptions.cs:38-65`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:143-150`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:136-145`; `sabbour/agentweaver:k8s/base/configmap-agenthost.yaml:43-66`; `sabbour/agentweaver:k8s/overlays/production/patch-agenthost-mtls.yaml:30-61`; `sabbour/agentweaver:k8s/overlays/production/patch-agenthost-mtls-client.yaml:15-40`.

### Identity qualification

The implemented mTLS mechanism is **mounted certificate + pinned CA validation**. Host requires a client certificate; client deliberately ignores hostname/IP mismatch and validates the certificate chain against its pinned CA. This is not evidence of deployed SPIFFE or per-pod workload-identity-bound certificates.

```csharp
httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
```

`sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostKestrelConfigurator.cs:68-105`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/AgentHostMtlsClientHandler.cs:47-60,71-94`

### Ingress qualification missing from audit

The specific A2A NetworkPolicy allows API and worker selectors to TCP/8088. **However, do not label the aggregate policy “only API and worker can reach 8088.”** The separate preview policy allows preview-gateway-labelled pods to TCP **3000–9000**, which includes 8088. It is an additive exception alongside deny-ingress.

This is a source-level policy observation, not a live exploitability assessment. In the graphic, retain the API/worker execution arrows and add a concise control annotation: **“Other additive policies also apply; preview ingress range includes 8088.”** Do not invent a preview gateway in the A2A request path.
`sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:28-48`; `sabbour/agentweaver:k8s/base/networkpolicy-sandbox.yaml:3-15,43-60`

### Configure capability boundary

Configure is not protected by the turn bearer because it delivers that bearer. It validates live Copilot capability fields unless BYOK is supplied, validates workspace configuration, then takes a one-time atomic gate:

```csharp
if (body.ByokProviderConfiguration is null && (body.CopilotCredential is null ||
    string.IsNullOrWhiteSpace(body.CopilotCredential.SnapshotReference) ||
    string.IsNullOrWhiteSpace(body.CopilotCredential.AccessToken) ||
    body.CopilotCredential.ExpiresAt <= DateTimeOffset.UtcNow))
```

```csharp
if (Interlocked.CompareExchange(ref _configured, 1, 0) != 0)
    return false;
```

The credential provider subsequently returns that credential only for the configured run and while unexpired. No user/token-store/config fallback.
`sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:232-289`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs:159-179`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostGitHubCapabilityCredentialProvider.cs:5-22`

## Relevant test evidence

- mTLS disabled/no endpoint produces plain fallback; enabled/no endpoint produces mTLS fallback; foreign CA and missing certificates rejected. `sabbour/agentweaver:tests/Agentweaver.Tests/AgentHost/AgentHostKestrelConfiguratorTests.cs:13-65`
- Client accepts pinned-CA leaf despite name mismatch, rejects different CA/unexpected errors/missing cert. `sabbour/agentweaver:tests/Agentweaver.Tests/Sandbox/AgentHostMtlsClientHandlerTests.cs:23-69`
- Configure payload includes run bearer and snapshot reference/expiry; BYOK payload has **null Copilot credential**. `sabbour/agentweaver:tests/Agentweaver.Tests/KubernetesSandboxExecutorClaimTests.cs:369-389,419-430`
- Missing capability fails closed; credential provider rejects another run and expired capability. `sabbour/agentweaver:tests/Agentweaver.Tests/KubernetesSandboxExecutorClaimTests.cs:646-658,701-727`
- Real HTTP A2A roundtrip checks final text, six forwarded events, revision state, remote API URL and API credential. `sabbour/agentweaver:tests/Agentweaver.Tests/AgentHost/A2ARoundTripIntegrationTests.cs:139-164`
- Per-turn skill context is layered over pod manifest **before** execution. `sabbour/agentweaver:tests/Agentweaver.Tests/AgentHost/A2ATurnBridgePerTurnContextTests.cs:132-160`
- Shared PostgreSQL store test demonstrates another replica reading the checkpoint; concurrent two-writer test checks all 20 writes. `sabbour/agentweaver:tests/Agentweaver.Tests/PostgresIntegration/PostgresCheckpointStoreTests.cs:39-77`

---

# 2. Exact factual replacements: `docs/reference/a2a.md`

## A. Preview warning and pinning

**Existing, line 4:**

> **Every published version of that line is `-preview`** … whereas the workflow runtime it pairs with reached stable `1.9.0`.

**Replacement paragraph:**

> The checked-in A2A dependencies remain preview packages on the remote agent-turn execution path. The client package `Microsoft.Agents.AI.A2A` is pinned to `1.19.0-preview.260822.1`; the host packages `Microsoft.Agents.AI.Hosting.A2A` and `.AspNetCore` are pinned to `1.11.1-preview.260625.1`. The workflow and Copilot integration packages use stable `1.19.0`. These are the repository’s current pins, not a claim about every version published upstream.

**Existing, line 25:**

> Pin a single A2A build aligned with Agentweaver's existing `Microsoft.Agents.AI.*` line …

**Replacement:**

> Keep the declared package versions and their committed lock-file content hashes together. The current client and host packages use different version stamps; validate the complete client/host combination before changing either side. Do not describe this as one uniformly aligned preview build.

Sources: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Agentweaver.AgentRuntime.csproj:14-17`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/packages.lock.json:24-28`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/packages.lock.json:34-38,52-56`.

## B. Hosted runner and per-turn setup

**Existing excerpt, line 36:**

> wrapping the pod's singleton `CopilotAIAgent`

**Replace with:**

> wrapping the pod’s singleton `CopilotAIAgent` through a purpose-routing runner. Workflow purposes use the Copilot runner; `OperatorAssistant` uses the operator MCP chat runner. Provider configuration may select GitHub Copilot capability authentication or BYOK.

**Existing, line 65:**

> The pod's bridge reads only the per-turn `IsRevision` flag from it; the run-scoped setup already ran after `/configure`.

**Replace with:**

> The bridge reads revision state and applies the per-turn system-prompt context, project/agent identity, API address, and API credential before execution. Prompt context is layered over the pod’s startup environment context; it includes the worker-assembled charter, memory, and assigned skills. One-time run provisioning still occurs through `/configure`.

Sources: `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:154-170`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/OperatorPodTurnRunner.cs:214-234`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:453-464`.

## C. Card authorization

**Existing, lines 44–49:** capability/security-scheme advertising, “no anonymous discovery,” and mandatory bearer/OAuth2 wrapping.

**Replacement for the section body:**

> The card endpoint is `GET {A2APath}/v1/card`. AgentHost protects it with a separate configured `AgentHost:CardBearerToken`; a non-empty value requires `Authorization: Bearer <card token>`. An empty value disables this gate. The options default is empty, and `AgentHost:Security:GateCardEndpoint` in the ConfigMap is not the value tested by the middleware.
>
> Turn submission uses a different credential: the runtime `TurnBearerToken` configured for that pod. Neither bearer is a TLS identity. mTLS behavior depends on the host and caller configuration described below. The repository code does not establish an OAuth2 discovery flow or SPIFFE identity integration.

Sources: `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostOptions.cs:62-75`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:372-401`; `sabbour/agentweaver:k8s/base/configmap-agenthost.yaml:62-66`.

Also repair the corrupted literal examples in endpoint/auth prose to:

```text
Authorization: Bearer <run turn token>
Authorization: Bearer <card token>
```

## D. Writeback boundary

**Existing, line 76:**

> - Worktree commit and diff — performed on the worker against the shared workspace PVC.

**Replacement:**

> - Workflow checkpoint/session persistence remains in the platform tier; it is not performed by the pod.
>
> Pod-local implementation turns are a separate workspace path: the pod prepares Git writeback and returns a `PreparedWriteback` DataPart. Do not describe all commit/diff work as occurring on the worker’s shared PVC.

Add to **What crosses the wire**:

> 4. For writable pod-local implementation turns, a prepared-writeback descriptor after the assistant output. The worker captures this separately from `RunEvent` data.

Sources: `sabbour/agentweaver:apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:326-368`; `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:313-324`.

## E. H1–H7: replace claims of universal satisfaction

**Existing, line 80:**

> The transport ships **only when all of H1–H7 hold**. These are mandatory gates, not recommendations.

**Replacement:**

> H1–H7 describe the intended security and operational controls. Their implementation and configuration status differ. The table below distinguishes code defaults, Kubernetes base configuration, production-overlay configuration, and requirements that are not established by this repository-only review.

**Replacement rows:**

```markdown
| **H1 — Transport identity** | Code defaults enable mTLS. The Kubernetes base sets `RequireMtls=false` on AgentHost and its API/worker callers; the production overlay enables HTTPS and client certificates on both ends. Implemented validation uses mounted certificates and pinned CAs; the caller deliberately ignores pod-IP hostname mismatch. Do not equate this with verified SPIFFE or per-pod workload identity. |
| **H2 — Scoped ingress** | The A2A NetworkPolicy permits same-namespace API and worker pods to TCP/8088. NetworkPolicies are additive: the separate preview-gateway ingress policy permits TCP/3000–9000, which includes 8088. NetworkPolicy is not a gateway hop. |
| **H3 — A2A app-layer authz** | Turn submission checks the runtime per-run bearer. Card discovery checks the separate configured `CardBearerToken`. Either middleware gate is disabled when its corresponding value is empty; card auth is not unconditionally enabled by the checked-in defaults. |
| **H4 — Bounded streaming** | The manifests declare Kestrel connection/body/header/keepalive limits. AgentHost control calls use a finite timeout; A2A streaming uses an infinite HTTP transport timeout with worker-side total/read-idle deadlines. `HeartbeatIntervalSeconds` appears in the ConfigMap, but a general transport-heartbeat implementation is not established by the reviewed host code. |
| **H5 — Platform-owned persistence** | The workflow checkpoint manager and durable event store remain in the worker/orchestration tier. PostgreSQL checkpoints support cross-replica reads; event persistence deduplicates by `(RunId, Sequence)`. These controls are not A2A stream replay or a blanket exactly-once guarantee for re-executed model/tool effects. |
| **H6 — Separate egress controls** | The A2A ingress policy does not grant egress. Current sandbox egress includes DNS, public HTTPS excluding configured private/link-local ranges, and separate platform-service rules. Do not describe the manifests as allowing only a model endpoint, broker, and run-specific Git remotes. |
| **H7 — Pinned preview and rollback** | Client and host preview versions and content hashes are recorded separately. `in-api` is the code fallback and the remote-workflow rollback mode; Kubernetes base manifests select `pod-per-run`. Changing the configured startup wiring is not demonstrated hot reload or a second wire protocol. |
```

Supporting sources beyond those above:

- Streaming client distinction: `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:640-668`.
- Limits/heartbeat config: `sabbour/agentweaver:k8s/base/configmap-agenthost.yaml:45-66`.
- Actual public-HTTPS egress: `sabbour/agentweaver:k8s/base/networkpolicy-sandbox.yaml:80-137`.
- The Cilium file is **egress**, not a second A2A ingress implementation: `sabbour/agentweaver:k8s/base/cilium-network-policy-sandbox.yaml:1-26`.

Replace the existing three “Notes on the gates” bullets with:

> - Bearer authorization and mTLS are independent controls. Label the configured deployment mode rather than asserting that every pod uses mTLS.
> - Streaming deadlines and listener limits are distinct. The worker enforces total/read-idle deadlines even though the HTTP streaming client has no overall transport timeout.
> - Checkpoints and durable event sequencing are platform-owned. The pod returns turn data; it does not persist workflow checkpoints directly.

## F. Rollback/default wording

**Existing, line 111:**

> Because of it, `in-api` mode remains the **default** until the pod-per-run path completes soak.

**Replace with:**

> `in-api` remains the code fallback. The checked-in Kubernetes base API and worker deployments explicitly select `pod-per-run`; the production overlay additionally enables mTLS. These configuration files do not establish the state of a live deployment or the completion of soak.

**Existing, line 117:**

> This is the instant, fully-tested rollback for any A2A defect or outage.

**Replace with:**

> This is the in-process workflow execution mode and the configuration rollback from remote workflow turns.

**Existing, line 120:** entire “requires no … redeploy of a different protocol” paragraph.

**Replace with:**

> Rollback changes `Sandbox:AgentExecutionMode` to `in-api` and deploys/restarts the applicable process configuration. The execution-mode selection is read during startup and registered in dependency injection. No second agent-turn wire protocol is introduced; hot switching is not established by this implementation.

Source: `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:588-606`.

## G. Quick configuration table and lifecycle

Replace the three rows for **`KeyVaultUri`**, **`KvTokenMountPath`**, and **`UseSharedTokenStore`** at lines 137–139 with:

```markdown
| `/configure.copilotCredential` | snapshot reference, access token, expiry | Live run-bound Copilot capability redeemed by the platform and delivered once. AgentHost rejects another run or an expired credential; it has no ambient token-store/configuration fallback. |
| `/configure.byokProviderConfiguration` | provider configuration / null | Separate provider boundary. BYOK launch does not require or transmit a Copilot capability credential. |
| `/configure.repositoryAccessToken` / `mcpBrokerToken` | optional, purpose-scoped | Separate repository and Operator Assistant MCP credentials; neither is the card bearer or model-provider credential. |
```

Use `in-api (code fallback; Kubernetes base: pod-per-run)` in the execution-mode row. Use `true (code and production overlay); false (Kubernetes base)` in the mTLS row.

For line 143’s lifecycle paragraph, replace the ordering excerpt:

> waits for binding, reads the pod IP, calls `POST /configure` … waits for `/healthz` …

with:

> waits for binding, records the claimed pod and turn token, resolves the pod IP, and probes `/healthz` until the listener is reachable. A warm pod reports `standby` at this stage. The executor then posts the one-time `/configure` payload and awaits setup completion. A configured ready pod reports `ready`.

Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:635-707`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:286-350,406-414`.

---

# 3. `docs/reference/agent-communication.md`

## Exact corrections

### Tool identifiers

Replace **all** occurrences in tool tables, parameter prose, and the channel summary:

| Existing | Replacement |
|---|---|
| `inbox_submit` | `decision_inbox_submit` |
| `inbox_list` | `decision_inbox_list` |
| `inbox_merge` | `decision_inbox_merge` |
| `inbox_reject` | `decision_inbox_reject` |
| `memory_add` | `memory_record` |

Exact identifiers: `sabbour/agentweaver:apps/Agentweaver.Mcp/Tools/MemoryTools.cs:71-120,209-257`.

### Decision eligibility

**Existing, lines 68–71:**

> Active `architectural` and `scope` decisions are the highest-priority context injected into every agent …

**Replacement:**

> Active, approved `architectural` and `scope` decisions are eligible for team-wide context compilation. Their text is serialized as untrusted historical data, not executable prompt instructions. Coordinator children receive the approved-decisions context without the full memory/session selection.

Source: `sabbour/agentweaver:apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:57-64,156-171,209-214`.

### Cross-team memory

**Existing, lines 75–78:**

> `memory_search` reads **across all agents** on the project, and memory tagged `cross-team` surfaces in other agents' compiled context.

**Replacement:**

> `memory_search` queries across agents on the project. Search visibility is distinct from prompt eligibility: another agent’s learning or pattern must be high importance, approved, and tagged `cross-team` to enter context selection. Eligible records remain subject to the compiler’s item/token budget.

Source: `sabbour/agentweaver:apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:78-90,112-133`; approval regression: `sabbour/agentweaver:tests/Agentweaver.Tests/Memory/MemoryContextCompilerSecurityTests.cs:72-103`.

### Confirmation gate

**Existing, line 132:**

> No subagent work is dispatched until the OutcomeSpec is confirmed.

**Replacement:**

> Interactive `defineOutcome` runs wait at the OutcomeSpec confirmation gate. `direct` mode skips that definition gate while retaining the dispatch/review/merge pipeline; `defineOutcome` with autopilot can schedule unattended confirmation.

Source: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:189-208`.

### A2A paragraph

**Existing excerpt, lines 187–191:**

> … an `A2ATurnBridgeAgent` … wrapping its `CopilotAIAgent` …

**Replacement:**

> … an `A2ATurnBridgeAgent` whose purpose-routing runner selects workflow execution or the Operator Assistant MCP loop. The configured provider boundary may use a run-bound Copilot credential or BYOK. The orchestration graph and checkpoint persistence remain outside the pod.

Sources: bridge/router/provider sources in §1.

### Broken link and anchor

The six-page local-link check found one missing target:

```markdown
[Distributed execution spec §4](../../specs/018-distributed-agent-execution-scaling/spec.md)
```

Replace the complete bullet at lines 199–200 with:

```markdown
- [A2A bridge: turn and event transport](../deep-dive/a2a-bridge.md#turn-and-event-transport) —
  the current worker-to-AgentHost transport boundary.
```

The target heading exists at `sabbour/agentweaver:docs/deep-dive/a2a-bridge.md:23-27`. No other existing local file-link failures were found in the six pages; none of their existing local links contained fragments requiring repair.

---

# 4. `docs/reference/memory.md`

## Selection semantics

**Existing, line 10:**

> … four layers, applied in strict priority order:

**Replacement:**

> `MemoryContextCompiler.CompileAsync(projectId, agentName)` gathers approved decisions, eligible memory candidates, and the current session. Decisions and session are selected separately. Core memories and eligible learnings/patterns share one importance/recency-ranked item/token budget; the labels below are source categories, not an unconditional inclusion order.

**Replace lines 12–17’s text block with:**

```text
Decisions: active, approved architectural/scope records, ordered by creation time
Memory candidates: non-legacy own core context + eligible learnings/patterns
Memory selection: importance descending, then recency; bounded by items and approximate tokens
Session: most recent open session, selected separately from the memory budget
```

**Existing, line 44:**

> They are always included regardless of importance level.

**Replacement:**

> Core memories are eligible regardless of importance level, but are not guaranteed inclusion. They are pooled with eligible learnings/patterns, ordered by importance and recency, then bounded by the memory item/token limits.

Add after that paragraph:

> Defaults are 20 memory items and approximately 4,000 tokens at four characters per token. Positive call-site overrides take precedence over `MemoryContext:MaxItems` / `MemoryContext:MaxTokens`, then the legacy `Memory:ContextMaxItems` / `Memory:ContextMaxTokens` keys. Selection stops when the next ranked candidate would exceed the character budget; it does not skip that candidate and search for smaller later records. Decisions and session are outside this memory-item budget.

Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:15-17,35-53,105-145`.

## Internal versus external tool names

After the first `record_memory` explanation, insert:

> `record_memory`, `submit_inbox_entry`, `update_session`, and `export_memory` are runtime tool names in this discussion. The public MCP counterparts are `memory_record`, `decision_inbox_submit`, `session_update`, and `memory_export`.

Source: `sabbour/agentweaver:apps/Agentweaver.Mcp/Tools/MemoryTools.cs:71-90,209-226,302-332`.

## Scribe lifecycle

**Existing, line 139:**

> After every completed project run, the **Scribe** step runs automatically:

**Replacement:**

> Standalone workflow completion and coordinator finalization own their Scribe work. Coordinator children stop at `assemble-ready` and bypass their own human-review, merge, and Scribe stages; collective assembly performs the coordinator’s final Scribe pass. Where that pass runs, it:

Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:475-481`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1251-1251,1735-1753`.

Keep the provenance tables and JSON-envelope example as text. Tests demonstrate adversarial strings remain JSON data, cross-team approval is required, and legacy decisions remain excluded until approved. `sabbour/agentweaver:tests/Agentweaver.Tests/Memory/MemoryContextCompilerSecurityTests.cs:22-69,72-103,106-131`.

---

# 5. `docs/reference/agent-definition.md`

## Five targets

**Existing, line 13:**

> … produces three targets …

Replace `three` → `five`, and append these rows after the existing embedded-template row:

```markdown
| `docs/public/agents/agentweaver.agent.md` | A byte-identical generated copy for the documentation site's anonymous download. | The agent file above |
| `apps/Agentweaver.Web/wwwroot/agents/agentweaver.agent.md` | A byte-identical generated copy for each deployment's same-origin anonymous download. | The agent file above |
```

Update these exact excerpts:

- Line 40: `# rewrite all three generated targets` → `# rewrite all five generated targets`
- Line 50: `all three targets` → `all five targets`
- Append to the sample check output:

```text
OK: docs/public/agents/agentweaver.agent.md is in sync.
OK: apps/Agentweaver.Web/wwwroot/agents/agentweaver.agent.md is in sync.
```

Actual five-element return array: `sabbour/agentweaver:scripts/gen-docs.mjs:266-273`; destinations/purposes: `sabbour/agentweaver:scripts/gen-docs.mjs:4-16`.

## Stale literal source line references

- Existing `` `applyToolMapBlock`, `gen-docs.mjs:192` `` → `` `applyToolMapBlock` in `scripts/gen-docs.mjs` ``. Current function begins at 228.
- Existing `` `ProjectService.TryMaterializeAgentDefinition` (`ProjectService.cs:485`) `` → `` `ProjectService.TryMaterializeAgentDefinition` ``. Current method begins at 505.
- Prefer named symbols over literal line numbers in prose; keep research citations below independently.

Sources: `sabbour/agentweaver:scripts/gen-docs.mjs:226-239`; `sabbour/agentweaver:apps/Agentweaver.Api/Projects/ProjectService.cs:89-89,182-182,505-505`.

The non-clobbering materialization table/tree remains correct: destination, existing-file guard, directory creation, and caught write failures are implemented. Tests verify matching template bytes and preservation of user edits. `sabbour/agentweaver:apps/Agentweaver.Api/Projects/AgentDefinitionTemplate.cs:23-26,50-67`; `sabbour/agentweaver:tests/Agentweaver.Tests/Projects/AgentDefinitionTemplateTests.cs:33-42,69-86`.

---

# 6. `docs/reference/project-skills.md`

## Route scope and additions

**Existing, line 12:**

> All routes are project-scoped under `/api/projects/{id}/skills`.

**Replacement:**

> Catalog and assignment routes use `/api/projects/{id}/skills`. Bundled-default actions use `/api/projects/{id}/skill-defaults`; project marketplace actions use `/api/projects/{id}/skill-marketplaces`. Administrator-curated marketplace discovery is global at `/api/skill-marketplaces`.

Append:

```markdown
| `POST` | `/api/projects/{id}/skill-defaults/preview` | Preview bundled role-skill defaults for a confirmed team and predefined blueprint; returns a state-bound digest without catalog writes. |
| `POST` | `/api/projects/{id}/skill-defaults/apply` | Apply a matching preview atomically; stale digest returns `409`. |
| `GET` | `/api/projects/{id}/skill-marketplaces` | List administrator-curated definitions plus this project's added sources. |
| `POST` | `/api/projects/{id}/skill-marketplaces/sources` | Add a project marketplace source by GitHub repository identity/URL. |
| `DELETE` | `/api/projects/{id}/skill-marketplaces/sources/{name}` | Remove a project-added source; curated definitions are not removable here. |
```

Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SkillEndpoints.cs:25-85,234-297`.

## DTO provenance

**Existing, line 51:**

> `connected-repo-sync`, `repo-import`, `file-upload`, or `manual`.

**Replacement:**

> `built-in`, `connected-repo-sync`, `repo-import`, `file-upload`, `manual`, or `marketplace`.

Source: `sabbour/agentweaver:packages/Agentweaver.Domain/Skills/SkillProvenance.cs:7-37`.

## MCP additions and import wording

**Existing, lines 85–86:**

> GitHub/Git/raw SKILL.md sources

**Replace with:**

> `owner/repo`, HTTPS `github.com` repository/tree/blob URLs, or HTTPS `raw.githubusercontent.com` URLs pointing directly to `SKILL.md`

Keep the existing detailed import restrictions section; its description matches `SkillImportSource.Parse`. Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Skills/SkillCatalogService.cs:1492-1532`; rejection tests: `sabbour/agentweaver:tests/Agentweaver.Tests/Skills/SkillCatalogTests.cs:225-248`.

Append MCP rows:

```markdown
| `skill_marketplaces_list` | List enabled administrator-curated marketplaces. |
| `skill_marketplace_browse` | Browse/search a marketplace without importing catalog entries. |
| `skill_marketplace_import` | Import selected marketplace candidates. |
| `skill_marketplace_sources_list` | List curated and project-added marketplace sources available to the project. |
| `skill_marketplace_source_add` | Add a project marketplace source. |
| `skill_marketplace_source_remove` | Remove a project-added marketplace source. |
| `skill_defaults_preview` | Preview bundled defaults and return the digest required for apply. |
| `skill_defaults_apply` | Atomically apply a matching preview; reject stale state. |
```

Source: `sabbour/agentweaver:apps/Agentweaver.Mcp/Tools/SkillTools.cs:187-293`.

Append to **Curated marketplaces**:

> Projects can add their own marketplace sources alongside the administrator-curated definitions. Curated definitions win on name collisions and cannot be shadowed by project sources. Adding a source does not itself import or assign its skills.

Source: `sabbour/agentweaver:apps/Agentweaver.Api/Skills/MarketplaceSourceService.cs:35-36`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SkillEndpoints.cs:258-297`.

**Important audit nuance:** the strict HTTPS/two-host restriction is the **skill-import parser**. The project marketplace-source parser is separate and its regex accepts an optional `http` or `https` GitHub prefix. Do not generalize the import parser’s exact validation rules to every source-add operation. `sabbour/agentweaver:apps/Agentweaver.Api/Skills/MarketplaceSourceService.cs:47-51`.

## Tests and retained semantics

- Equal content hash + active state returns unchanged; missing status is handled separately. `sabbour/agentweaver:apps/Agentweaver.Api/Skills/SkillCatalogService.cs:1139-1155`
- Apply regenerates preview, compares digest with `FixedTimeEquals`, then delegates atomic store application. `sabbour/agentweaver:apps/Agentweaver.Api/Skills/SkillDefaultsService.cs:231-250`
- Endpoint tests cover successful eight-skill application, stale digest rejection, missing confirmed team, and cross-project digest replay rejection. `sabbour/agentweaver:tests/Agentweaver.Tests/Skills/SkillDefaultsEndpointsTests.cs:16-70,100-128`

---

# 7. `docs/reference/agent-executor.md`

The AX column was **not externally verified**, as requested. Preserve comparison tables only with explicit hypothesis status; do not use them as deployed-architecture evidence.

## Insert directly after the introductory paragraph

```markdown
> **Repository-grounded comparison; AX claims unverified here.** The Agentweaver descriptions below are grounded in the checked-in implementation and configuration. AX API, roadmap, isolation, oversubscription, and effort claims have not been verified against dated primary sources in this review. Treat them as hypotheses for a future adapter spike, not current deployment facts or confirmed blockers.
```

## Exact Agentweaver corrections

**Existing, line 36:**

> Agentweaver is currently welded to the GitHub Copilot A2A agent.

**Replacement:**

> Agentweaver uses A2A for remote AgentHost turns, but model authentication/configuration is not Copilot-only: the run boundary supports a snapshot-bound Copilot capability or BYOK provider configuration.

Source: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:478-525`; BYOK test: `sabbour/agentweaver:tests/Agentweaver.Tests/KubernetesSandboxExecutorClaimTests.cs:419-430`.

**Existing, line 38:**

> Agentweaver's AgentHost warm pool is fixed-size (×2 standby per cluster). AX … ~30× oversubscription …

**Replacement:**

> **Scale.** The checked-in AgentHost warm pool requests two standby replicas; this is not a total-run capacity ceiling. Worker autoscaling is a separate setting, currently two to three replicas in the base HPA. An AX oversubscription advantage remains an unverified hypothesis, not a repository-established comparison.

Sources: `sabbour/agentweaver:k8s/base/sandbox-warmpool-agenthost.yaml:29-34`; `sabbour/agentweaver:k8s/base/worker-hpa.yaml:57-76`.

**Existing, line 63:**

> The worktree still lives on the Azure Files RWX PVC — but AX actors must have that PVC mounted.

**Replacement:**

> Agentweaver retains ownership of branch, assembly, and merge semantics. Current implementation turns use verified pod-local writable checkouts and prepared Git writeback; assembly Build/Test uses a local read-only checkout. A hypothetical AX adapter must preserve source-commit/tree verification and the writeback contract. A shared writable PVC is not an established requirement for that adapter.

Sources: `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:233-257,411-455`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:326-368`; tests: `sabbour/agentweaver:tests/Agentweaver.Tests/AgentHost/PodLocalWorkspaceManagerTests.cs:107-121,210-240`.

**Existing, line 69, entire Authentication / Key Vault paragraph.**

**Replacement:**

> **h) Run capability and provider boundary.** AgentHost receives a one-time run configuration containing either a live snapshot-bound `copilotCredential` or BYOK provider configuration. Repository and MCP broker credentials are separate, purpose-scoped values. A hypothetical AX actor activation path must preserve those boundaries and must not restore ambient user-token lookup from Key Vault, CSI, shared storage, or host configuration.

Sources: `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:232-270`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostGitHubCapabilityCredentialProvider.cs:5-22`.

**Existing, line 67:** “gVisor is a weaker threat model” and assumed AX substrate.

**Replacement:**

> **g) Isolation model.** Any adapter must preserve Agentweaver’s required execution isolation and filesystem boundaries. AX/Substrate runtime defaults, gVisor assumptions, and compatibility with Kata require separate dated validation; this repository-only review does not establish an isolation regression or an integration effort estimate.

**Existing, lines 73–77:** effort bullets and categorical verdict.

**Replacement:**

> **Assessment — hypothesis only.** An AX spike would need to validate actor lifecycle, recovery semantics, provider-capability delivery, event persistence, workspace writeback, and isolation against Agentweaver’s existing contracts. Coordinator DAG dispatch, assembly, review, steering, and platform persistence remain explicit Agentweaver responsibilities unless an adapter design proves otherwise. The repository does not establish a fixed two-run ceiling, a lack of durable cross-replica checkpoints, a measured AX scaling advantage, or the claimed effort estimates.

Checkpoint counterevidence: `sabbour/agentweaver:tests/Agentweaver.Tests/PostgresIntegration/PostgresCheckpointStoreTests.cs:39-77`.

Update summary-table cells consistently:

- `Fixed ×2 standby per cluster` → `Two configured standby pods; separate worker HPA`
- `Unchanged; PVC mounted into AX actor` → `Adapter must preserve verified source and writeback contracts`
- `**Blocked** — AX HITL is roadmap-only` → `Agentweaver-native; AX support unverified`
- `gVisor default, or Kata runtime class` → `Isolation compatibility to validate`
- `API brokers per-user token...` → `One-time run capability or BYOK; separate repository/MCP credentials`
- Treat AX-specific values and effort column as **unverified estimates** throughout.

No AX deployment diagram should be added.

---

# 8. Stable image reuse and publication actions

All seven referenced PNG paths exist in the worktree; their contents were not inspected or used as factual evidence.

| Consumer | Exact action / text |
|---|---|
| `a2a.md`, existing image line 92 | Preserve `../diagrams/reference-a2a-fig1.png`. Replace alt text with: `A2A boundaries: API and worker callers, configured ingress and mTLS controls, separate card and turn authorization, one-time AgentHost configuration, purpose-routed turn execution, and worker-owned checkpoint persistence` |
| `a2a.md`, after message-mode introduction | Add `[Shared single-turn A2A sequence](../diagrams/canonical-agent-communication-a2a.png).` |
| `agent-communication.md`, shared-state section | Add `[Shared-state coordination model](../diagrams/canonical-agent-communication-shared.png).` Keep MCP/REST tables. |
| `agent-communication.md`, handoff section | Add `[Coordinator handoff model](../diagrams/canonical-agent-communication-handoff.png).` **Only after owner reconciles direct/defineOutcome/autopilot gates.** |
| `agent-communication.md`, A2A section | Add `[Shared single-turn A2A sequence](../diagrams/canonical-agent-communication-a2a.png).` No duplicate image. |
| `agent-definition.md`, generated-files section | Add `[Generation and materialization flow](../diagrams/agent-definition-fig1.png).` **Owner must show all five outputs.** Keep marker examples and filesystem tree. |
| `memory.md`, corrected selection text | Add `[Shared memory-context model](../diagrams/canonical-memory-context.png).` **Owner must reconcile pooled budget and approval/untrusted-data semantics.** |
| `project-skills.md`, introduction | Add `[Skill acquisition, catalog, and assignment flow](../diagrams/project-skills-fig1.png).` **Owner must include defaults preview/apply and project marketplace-source paths.** |
| `agent-executor.md` | Retain searchable comparison/hypothesis tables; add no AX architecture image. |

For `a2a.md` lines 94–97, **do not replace the old provenance with a claim that canonical draw.io authoring is complete yet**. Once the parent actually authors the canonical file, replace:

> Edit the JSON, then run `npm run docs:render-diagrams` …

with provenance naming the **actual authored `.drawio` source and actual export command**. This research did not create or verify that canonical source.

## Remaining bounded uncertainties

- No live Kubernetes deployment, TLS handshake, packet path, or aggregate CNI enforcement was tested.
- No blanket general SSE heartbeat implementation was established from `HeartbeatIntervalSeconds`; it is present in configuration, not in the reviewed `AgentHostOptions`/host wiring.
- No comprehensive A2A card/turn middleware integration test was located in the bounded test reads; the bearer behavior above is directly verified from middleware code.
- AX upstream status and performance remain unverified by design.
- The audit’s `CoordinatorGraphDescriptor` citation for Scribe ownership points at presentation metadata; the stronger evidence is the actual child terminal path and `CoordinatorAssemblyService` cited above.
