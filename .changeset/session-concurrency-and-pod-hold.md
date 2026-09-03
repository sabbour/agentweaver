---
"agentweaver": patch
---

Fix false "too many active assistant conversations" rejections, and the 15-20s of silence before
every Session reply.

The per-user concurrency bound counted `AssistantRunService`'s IN-MEMORY `_runs` dictionary, which
conflates "resident in this process" with "actively running". `RehydrateRunAsync` inserts into that
same dictionary, so merely opening or replying to an existing conversation occupied a slot for the
next 30 minutes — and with two API replicas and no session affinity, the SAME conversation could
occupy a slot on BOTH, so the replicas disagreed about the count. Live logs showed five distinct
operator runs against a limit of three. `StartRunAsync` now counts the caller's `InProgress` operator
runs in the durable run store (`IRunStore.GetRunsBySubmittingUserAsync`, reusing the query the
duplicate-start guard already issued), so one conversation is one row however many replicas have it
resident, a parked or finished conversation frees its slot immediately, and rehydration cannot
consume a slot at all. In-flight starts, which have no durable row yet, are reserved under the
existing `_startLock` so concurrent starts on one replica still cannot slip past the bound. With
counting correct the bound is raised 3 → 5: the live report was five legitimately-open
conversations, and an open-but-quiet conversation now holds no pod (below), so an extra one costs
close to nothing.

`RemoteOperatorAssistantAgent` also claimed a warm AgentHost pod and RELEASED it after every single
turn, so each message paid claim binding, the A2A handshake, MCP connect, history replay, and a
one-shot `/configure` (~8s on its own — it runs `CopilotAIAgent.SetupAsync` and starts a Copilot/BYOK
client from scratch). The pod is now HELD for the conversation. `KubernetesSandboxExecutor` reuses an
existing operator claim instead of deleting and recreating it whenever this replica still holds the
run's turn token, and because `/configure` is genuinely one-shot the current caller bearer instead
rides the per-turn setup channel (new `AgentSetupParams.CallerBearerToken` →
`AgentHostRuntimeState.RefreshCallerBearerToken`), so a refreshed browser token still reaches the pod
every turn. A turn landing on the other replica simply falls back to the old cold-start path.

Held pods are given back by a new, deliberately short `Assistant:PodIdleTimeout` (default 5 min,
skipped while a tool-approval is armed), by conversation dormancy at `IdleTimeout`, and on turn
failure or cancellation — a compare-and-swap guarantees exactly one release, with
`AgentHostReaperService` as the existing backstop.
