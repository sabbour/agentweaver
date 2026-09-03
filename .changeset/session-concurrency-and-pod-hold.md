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
run's turn token. Because `/configure` is genuinely one-shot, the current short-lived MCP broker
token is renewed over the authenticated API-to-pod control plane before every turn and immediately
before every MCP tool call, without forwarding a browser bearer into the pod. A turn landing on the
other replica simply falls back to the old cold-start path.

Held pods are given back by a new, deliberately short `Assistant:PodIdleTimeout` (default 5 min,
skipped while a tool-approval is armed), by conversation dormancy at `IdleTimeout`, and on turn
failure or cancellation — a compare-and-swap guarantees exactly one release, with
`AgentHostReaperService` as the existing backstop.

Holding the pod across turns also had to be reconciled with mid-conversation provider changes, which
it would otherwise have silently defeated. A pod resolves BYOK vs GitHub Copilot and builds its model
client EXACTLY ONCE, at its one-shot `/configure`; the per-turn refresh rebuilds only the tool set and
system message. So once a pod was held, repointing the run row at a newly-selected provider changed
the bookkeeping while the held pod kept serving every turn from the old one. Per-turn re-resolution
now compares the full provider IDENTITY (provider kind plus binding / configuration id) rather than
the two-value `ModelSource` enum — which cannot see a swapped BYOK configuration or a rebound Copilot
account at all — and releases the held pod whenever it really changed, so the next turn cold-starts
one configured for the provider now in effect. The cost is exactly one cold start on the turn after
an administrator changed the provider.

Releasing a held pod is now fenced. Claims are named deterministically from the run id while
everything that decides to release one is process-local, so an API replica whose conversation had
moved to the other replica could delete a claim that replica was actively serving a turn from. Each
conversation's owner stamps a holder token on the claim it creates and a release proceeds only while
that stamp still matches, making it a compare-and-swap rather than an unconditional delete. This is
deliberately not a distributed lease: the cross-replica `AgentHostReaperService` remains the backstop
for genuinely orphaned claims.

Finally, a durable `InProgress` assistant run is only ever parked by the owning API pod's in-memory
sweep, so a pod that restarted first stranded the row as `InProgress` forever and permanently burned
one of that user's concurrency slots. When (and only when) the bound is about to refuse a start, the
counted rows are re-examined against the run's last durable event and any silent past
`Assistant:StaleActiveRunThreshold` (default 90 min) is discounted and CAS-parked, so the repair is
shared cluster-wide without a new background job.
