# Assistant Runtime — Conceptual Deep Dive

## Purpose and scope

The Assistant (also surfaced in the UI as **Sessions**) is a distinct, lightweight execution path from a full project run. There is no worktree or review/merge workflow. The API owns the durable conversation, while the model/tool loop runs in an AgentHost pod with MCP tool access — held across the turns of an active conversation and released once it goes quiet. This page explains that path end to end: how a conversation is created, why it survives idle periods and pod restarts, how caller identity reaches MCP without being persisted, and why the SDK's own session persistence is deliberately turned off.

Primary scope:

- `apps/Agentweaver.Api/Assistant/AssistantRunService.cs` — run lifecycle, durable concurrency limit, idle/pod-idle sweeps, durable rehydration.
- `apps/Agentweaver.Api/Endpoints/AssistantEndpoints.cs` — the HTTP surface.
- `apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs` — AgentHost launch/hold/release and the A2A proxy.
- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs` — claim reuse for a pod held across turns.
- `apps/Agentweaver.AgentHost/OperatorPodTurnRunner.cs` — pod-side request reconstruction and approval projection.
- `packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs` — the per-turn SDK session and tool-access model.

For the tool catalog the assistant calls into, see [MCP Server — Deep Dive](./mcp-server.md) and [Reference — MCP tools](/reference/mcp-tools). For the general agent turn/tool-governance model used by full project runs, see [Agent Runtime & Tools — Deep Dive](./agent-runtime.md) — the Assistant intentionally does **not** go through that heavier path.

## Why a separate, lighter-weight path

A project run needs an isolated git worktree, a sandboxed execution environment, and a review/merge workflow because it changes files in a repository. A chat conversation about the state of the product doesn't need any of that — it needs to durably remember what was said and to call the same MCP tools other clients use. `AssistantRunService` models a session as a run record (`AgentName == "Operator"`) purely so it can reuse the existing run store, event stream, and `/api/runs/{id}` list/delete endpoints, without inheriting worktree or sandbox machinery it doesn't need.

## The life of a session

The API owns the durable conversation; the held AgentHost creates a fresh SDK session
for each turn and uses a separately issued MCP broker token, not the browser's Entra bearer.

![Assistant session boundaries: API-owned conversation, held AgentHost, fresh per-turn SDK session, broker-only MCP access, and durable history](../diagrams/assistant-runtime-fig1.png)

<!-- Editable source: docs/diagrams/src/assistant-runtime-fig1.drawio; grounded lifecycle and broker-token corrections retained. -->

1. **Start.** `POST /api/assistant/runs` creates a run record and, if an initial message was supplied, immediately runs the opening turn. The response returns the `runId` used for every subsequent message.
2. **Converse.** `POST /api/assistant/runs/{id}/messages` appends the caller's message, runs a turn, and returns the assistant's reply. Each turn is serialized per-run via a semaphore so two messages to the same session can't race.
3. **Persist.** Every turn appends `AgentMessage` events (role + content) to the same durable event log every other run type uses. This is the only source of truth for a conversation's history — the in-memory cache is purely an optimization.
4. **Go idle, or move pods.** Two independent timers, because a conversation and its pod have very different costs. The **pod-idle** sweep releases a conversation's held AgentHost pod after 5 minutes of quiet (`AssistantRunOptions.PodIdleTimeout`) — the conversation stays fully alive and resumable, the next message just pays one cold start again. The much later **conversation-idle** sweep parks the run after 30 minutes without activity (`AssistantRunOptions.IdleTimeout`), releasing any still-held pod and freeing its concurrency slot. Neither sweep touches a run that is blocked on an armed tool-approval. Separately, because there is no session affinity between the UI and API replicas, a later message for the same run can land on a pod that never held it in memory at all.
5. **Resume.** A cache miss can be rehydrated from durable state after authorization. History is bounded to the latest 24 messages (`MaxHistoryMessages`). An idle sweep parks the run as nonterminal `Idle`; a compare-and-swap wake returns it to `InProgress`. `Completed`, or a durable `run.completed`, is closed and rejects further messages with `409 operator_run_closed`. Rehydration is not permission to revive a completed conversation or a blanket exactly-once-turn guarantee.

## Caller identity across API, AgentHost, and MCP

The browser request authenticates to the API with its Entra identity. For each turn, `AssistantRunService` obtains a separate short-lived Agentweaver MCP broker token and a renewal callback. `RemoteOperatorAssistantAgent` requires both; it does not forward the raw browser bearer to MCP. The initial token travels in the one-shot internal `/configure` payload, and subsequent turns refresh the held pod's broker context through per-turn setup. Credentials are not conversation history or durable run-event content.

That separation matters in Entra mode:

- the **browser bearer** establishes the Entra caller at the API;
- the **MCP broker token** carries that caller's permitted MCP context, with `mcp:invoke` and the exact resource audience;
- the **model-provider credential/configuration** and any repository capability are separate execution inputs, not interchangeable bearer tokens.

MCP validates the broker token's signature, issuer, resource audience, lifetime, subject, and scope, then forwards that broker token to the API for independent authorization. Entra tenant validation happens at the API identity boundary, not as an MCP credential fallback. Source: `AssistantRunService.cs:789-813`, `RemoteOperatorAssistantAgent.cs:77-83`, and `McpBrokerAuthenticationHandler.cs:64-88`.

::: tip Only genuinely-active conversations count against the limit
A caller may have at most `MaxConcurrentRunsPerUser` (5) sessions *actively running* at once — enforced only when a brand-new run is created, and counted from **durable run status** (the caller's `InProgress` operator runs in the run store) rather than from any one API replica's in-memory cache.

That distinction is the whole point. The cache conflates "resident in this process" with "actively running": rehydration inserts into it too, so merely opening or replying to an old conversation used to occupy a slot for the next 30 minutes — and with two API replicas and no session affinity, the *same* conversation could occupy a slot on *both*, so the replicas disagreed about the count and a user with a handful of open conversations was falsely told they had too many active ones. One conversation is one row, whichever replicas have it resident, and a parked or finished conversation frees its slot immediately.

Resuming an existing session via rehydration still deliberately does **not** re-check the limit: the alternative would make a conversation unresumable purely because the caller has since started other conversations, with no "resume this one instead" escape hatch the way `StartRunAsync` has "start a different one instead."
:::

## Pod lifetime: held for the conversation, not the turn

The AgentHost pod is claimed on a conversation's first turn and then **held**. Releasing it after every turn cost 15-20s of silence on each message — claim binding, the A2A handshake, MCP connect, history replay, and the `/configure` call alone (which runs `CopilotAIAgent.SetupAsync` and starts a Copilot/BYOK client from scratch) taking ~8s of it.

`KubernetesSandboxExecutor.LaunchAgentHostPodAsync` decides whether a pod is reusable by asking whether *this replica* still holds the run's turn token (`PodNameRegistry`). The turn token is what authenticates the A2A call, so it is exactly the right predicate:

- **token held** → the existing claim is reused as-is: no delete, no recreate, no `/configure`. The per-turn setup channel refreshes the MCP broker context described above.
- **no token** (other replica, or a restart) → the claim is unreachable and un-reconfigurable from here, so it is deleted and recreated, which is the original cold-start path. Cross-replica turns therefore degrade to the old behaviour rather than breaking.

Held pods are given back by:

- the **pod-idle sweep** after `PodIdleTimeout` (5 min) of quiet, skipped while an approval is armed;
- **conversation dormancy** at `IdleTimeout` (30 min), when the run is parked;
- **turn failure** — `RemoteOperatorAssistantAgent` releases on both its exception and cancellation paths.

A `TryMarkAgentHostPodReleasing` compare-and-swap on the run state guarantees exactly one release is issued no matter how many of those fire. If every explicit path fails, `AgentHostReaperService` is the backstop: it reaps any `agent-*` claim whose run is no longer `InProgress`/`Pending`/`AwaitingReview`.

Holding pods is also what makes the higher concurrency bound cheap — a conversation that is open but quiet holds no pod at all, so the marginal cost of an extra open conversation is close to zero.

## Why the SDK's own session store is off

Each turn, `OperatorAssistantAgent` creates a **brand-new** Copilot SDK session (it never resumes one) and seeds it with the rebuilt history described above. The SDK also offers a native session *store* (`EnableSessionStore` / `InfiniteSessions`) that would persist SDK session state itself. That flag is deliberately `false`.

Before the AgentHost cutover it was briefly flipped on during a hotfix attempt (tracked as the v0.9.68 regression), on the theory that the SDK's "database is locked" failure mode only affected one-shot sandboxed workloads, not the then-in-process assistant. That theory was wrong: because a fresh session was created on *every* turn rather than resumed, every concurrent conversation wrote to the same pod-local SQLite session file. The contention reproduced live in staging within minutes (`Error: database is locked`) and the flag was reverted the same day.

Durable rehydration (the mechanism described above) is unaffected by this and remains the correct answer to cross-pod/idle/restart continuity — it works entirely from Agentweaver's own event log, independent of the AgentHost or SDK session. Using the SDK's native store would require deterministic `SessionId` resume instead of creating a new session per turn; holding the pod across turns shortens the cold start but does not change that — each turn still starts a fresh SDK session with no state to preserve.

## Tool access and sandboxing

The Assistant model/tool loop runs in an AgentHost pod, while the API retains the durable conversation and approval endpoints. The SDK session is also constrained at the tool-declaration layer:

- `AvailableTools` is set to *only* the MCP tool declarations — every SDK built-in native tool (shell, file read/write, `str_replace_editor`, `grep`, `web_fetch`, …) is excluded from the model's tool surface entirely, so it's simply not offered, regardless of what the model asks for.
- `OnPermissionRequest` is a defense-in-depth second layer: it rejects any native shell/read/write/URL permission request outright (in case a built-in somehow still reached the permission layer) and approves MCP/custom tool requests, whose consequential subset is human-gated separately by `ApprovalGatingAIFunction` (driven by `OperatorToolApprovalPolicy`) and enforced by the MCP server itself. That policy is **fail-closed**: only an explicit allow-list of read/low-consequence tools runs without a prompt — every consequential mutator *and any unrecognized or newly added MCP tool* requires an operator decision by default, so a new tool can never silently execute without consent.

Any file system, shell, or code-execution work the assistant needs to do must go through the same MCP run tools (`coordinator_start` / `run_submit` / `run_task`) an external client would use.

## See also

- [The Assistant and Sessions — Getting Started](/guide/assistant)
- [Sessions & the Assistant — User Guide](/experience/assistant-sessions)
- [API reference — Assistant endpoints](/reference/api#assistant-endpoints)
- [Agent Runtime & Tools — Deep Dive](./agent-runtime.md) — the heavier path used by full project runs
- [MCP Server — Deep Dive](./mcp-server.md)

<!-- diagram-context:assistant-runtime-fig1:start -->
<details id="diagram-context-assistant-runtime-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>A conversation survives its pod</td></tr>
<tr><td>takeaway</td><td>API-owned history; a held AgentHost runs a fresh, MCP-only SDK session each turn.</td></tr>
<tr><td>group-title0</td><td>CONVERSATION CONTROL</td></tr>
<tr><td>group-title1</td><td>DURABILITY / EXECUTION</td></tr>
<tr><td>Sessions UI</td><td>Sessions UI</td></tr>
<tr><td>Sessions UI</td><td>Entra-authenticated caller</td></tr>
<tr><td>Sessions UI</td><td>Start or append a message</td></tr>
<tr><td>Sessions UI</td><td>Approval replies stay at API</td></tr>
<tr><td>Sessions UI</td><td>/api/assistant/runs</td></tr>
<tr><td>Assistant API</td><td>Assistant API</td></tr>
<tr><td>Assistant API</td><td>Durable conversation owner</td></tr>
<tr><td>Assistant API</td><td>Serialize turns per run</td></tr>
<tr><td>Assistant API</td><td>Issue + renew MCP broker</td></tr>
<tr><td>Assistant API</td><td>broker lifetime: 5 min</td></tr>
<tr><td>Held AgentHost</td><td>Held AgentHost</td></tr>
<tr><td>Held AgentHost</td><td>Pod reused across turns</td></tr>
<tr><td>Held AgentHost</td><td>One-shot /configure</td></tr>
<tr><td>Held AgentHost</td><td>Per-turn broker refresh</td></tr>
<tr><td>Held AgentHost</td><td>A2A turn bearer</td></tr>
<tr><td>Run + event store</td><td>Run + event store</td></tr>
<tr><td>Run + event store</td><td>Authoritative conversation</td></tr>
<tr><td>Run + event store</td><td>Append AgentMessage events</td></tr>
<tr><td>Run + event store</td><td>Reload latest 24 messages</td></tr>
<tr><td>Run + event store</td><td>Idle → InProgress (CAS)</td></tr>
<tr><td>MCP server</td><td>MCP server</td></tr>
<tr><td>MCP server</td><td>Broker-only tool boundary</td></tr>
<tr><td>MCP server</td><td>Validate issuer + audience</td></tr>
<tr><td>MCP server</td><td>Consequential tools gated</td></tr>
<tr><td>MCP server</td><td>RS256 • mcp:invoke</td></tr>
<tr><td>Fresh SDK session</td><td>Fresh SDK session</td></tr>
<tr><td>Fresh SDK session</td><td>OperatorAssistantAgent</td></tr>
<tr><td>Fresh SDK session</td><td>Seed reconstructed history</td></tr>
<tr><td>Fresh SDK session</td><td>No native shell / files</td></tr>
<tr><td>Fresh SDK session</td><td>SDK session store: off</td></tr>
<tr><td>relation-0</td><td>1 message</td></tr>
<tr><td>relation-1</td><td>2 configure / turn</td></tr>
<tr><td>relation-2</td><td>3 run turn</td></tr>
<tr><td>relation-3</td><td>4 MCP tools</td></tr>
<tr><td>relation-4</td><td>5 append / reload</td></tr>
<tr><td>relation-5</td><td>6 API authorization</td></tr>
<tr><td>assurance</td><td>Pod quiet 5 min: release • Conversation quiet 30 min: Idle, resumable • Completed: sealed</td></tr>
<tr><td>assurance-0-label</td><td>API durable history</td></tr>
<tr><td>assurance-0-fact</td><td>History survives pod release.</td></tr>
<tr><td>assurance-0-source</td><td>AssistantRunService.cs</td></tr>
<tr><td>assurance-1-label</td><td>Broker lifetime</td></tr>
<tr><td>assurance-1-fact</td><td>Renew before MCP tool calls.</td></tr>
<tr><td>assurance-1-source</td><td>OperatorAssistantAgent.cs</td></tr>
<tr><td>assurance-2-label</td><td>Conversation lifecycle</td></tr>
<tr><td>assurance-2-fact</td><td>Idle can wake; Completed cannot.</td></tr>
<tr><td>n0</td><td>Start or append a message; Approval replies stay at API</td></tr>
<tr><td>n1</td><td>Serialize turns per run; Issue + renew MCP broker</td></tr>
<tr><td>n2</td><td>One-shot /configure; Per-turn broker refresh</td></tr>
<tr><td>n3</td><td>Append AgentMessage events; Reload latest 24 messages</td></tr>
<tr><td>n4</td><td>Validate issuer + audience; Consequential tools gated</td></tr>
<tr><td>n5</td><td>Seed reconstructed history; No native shell / files</td></tr>
<tr><td>footer</td><td>Pod quiet 5 min: release  •  Conversation quiet 30 min: Idle, resumable  •  Completed: sealed</td></tr>
<tr><td>groups</td><td>CONVERSATION CONTROL; DURABILITY / EXECUTION</td></tr>
</tbody></table>
</details>
<!-- diagram-context:assistant-runtime-fig1:end -->
