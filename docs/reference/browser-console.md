# Browser console — Reference

> ⚠️ **Frontend removed (#346).** The web UI no longer calls these endpoints — the
> Sessions/Assistant page is the sole assistant entry point now. This backend contract
> is kept for reference in case any other caller is found later; it is not linked from
> the app anymore.

Terse reference for the conversational browser-console facade. For implementation details see the [deep dive](../deep-dive/browser-console.md); for operator flow see the [experience guide](../experience/browser-console.md).

## Routes

| Method & path | Body | Returns | Notes |
|---|---|---|---|
| `POST /api/console/turn` | `ConsoleTurnRequest` | `ConsoleTurnResponse` | Primary natural-language console endpoint. |
| `POST /api/console/messages` | `ConsoleTurnRequest` | `ConsoleTurnResponse` | Alias wired to the same handler. |

Source: `apps/Agentweaver.Api/Endpoints/ConsoleEndpoints.cs:63`.

## `ConsoleTurnRequest`

Defined in `apps/Agentweaver.Api/Contracts/Dtos.cs:903`.

| Field | Type | Meaning |
|---|---|---|
| `text` | string \| null | User text. Either `text` or `message` must be non-empty. |
| `message` | string \| null | Alias used by the browser submit path. |
| `context` | `ConsoleTurnContext` \| null | Route-derived scope, project, run, and route. |
| `project_id` | string \| null | Top-level project fallback when `context.project_id` is absent. |
| `run_id` | string \| null | Top-level run fallback when `context.run_id` is absent. |
| `route` | string \| null | Top-level route fallback. |
| `conversation_id` | string \| null | Reuses an existing console conversation; blank creates a new id. |
| `confirmation_token` | string \| null | Reserved field; the current backend does not use it for execution. |

`ConsoleTurnContext` has `scope`, `project_id`, `run_id`, and `route` (`Dtos.cs:915`).

## `ConsoleTurnResponse`

Defined in `apps/Agentweaver.Api/Contracts/Dtos.cs:923`.

| Field | Type | Meaning |
|---|---|---|
| `conversation_id` | string \| null | Conversation id to send on the next turn. |
| `role` | string | Defaults to `assistant`. |
| `status` | string \| null | `completed`, `needs_clarification`, `needs_confirmation`, or `blocked` from the response kind. |
| `kind` | string | `answer`, `clarification`, `gate_required`, or `error`-style kind. |
| `message` | string | Primary display text. |
| `action` | string \| null | Gate/action name when present. |
| `tools` | `ConsoleToolSummary[]` \| null | Tool labels, statuses, and details for display. |
| `tool_calls` | `ConsoleToolCall[]` \| null | Normalized tool-call summaries. |
| `links` | `ConsoleLink[]` \| null | Router links or external hrefs. |
| `gate` | `ConsoleGate` \| null | Explicit gate description; console does not bypass gates. |
| `project_id` / `run_id` | string \| null | Bound ids after the turn. |
| `message_chunks` | `ConsoleMessageChunk[]` \| null | Current response always includes one chunk with the message. |
| `events` | `ConsoleTurnEvent[]` \| null | Current response includes a message event. |
| `action_summaries` | `ConsoleActionSummary[]` \| null | Action names, statuses, target type/id, label, and detail. |
| `clarifications` | `ConsoleClarification[]` \| null | Required clarification prompts and options. |
| `errors` | `ConsoleError[]` \| null | Error code/message list. |
| `actionable_state` | `ConsoleActionableState` \| null | Returned project/run/route, pending gate, and suggested commands. |

## Routing behavior

| Input category | Response behavior | Source |
|---|---|---|
| Empty text/message | `400 text_required` | `ConsoleTurnService.cs:61` |
| Invalid project id or run id | `400 invalid_project_id` / `400 invalid_run_id` | `ConsoleTurnService.cs:158`, `:174` |
| Missing project/run | `404 project_not_found` / `404 run_not_found` | `ConsoleTurnService.cs:162`, `:178` |
| Caller does not own project/run | `403 forbidden` | `ConsoleTurnService.cs:165`, `:181` |
| Supplied run belongs to another project | `409 context_mismatch` | `ConsoleTurnService.cs:77` |
| Gate words (`confirm`, `approve`, `merge`, `review`) | `gate_required`, with gate kind `assembly_review`, `review_merge`, or `outcome_spec_confirmation` | `ConsoleTurnService.cs:315` |
| Destructive words (`delete`, `remove`, `archive`, `cancel`, `stop`) | `gate_required` with `destructive_action` | `ConsoleTurnService.cs:354` |
| Read-only status/list/show questions | MAF facade with safe read-only tools | `ConsoleTurnService.cs:105`; `ConsoleFacadeAgent.cs:269` |
| Prose while a run is bound | Coordinator steering `kind=send` | `ConsoleTurnService.cs:278` |

## Safe facade tools

The facade tool list is read-only/status-oriented: `project_list`, `project_get`, `project_list_runs`, `backlog_get_board`, `run_status`, `coordinator_work_plan_get`, `coordinator_children_get`, `orchestration_topology`, `list_blueprints`, `catalog_list_roles`, `workflows_list`, `decision_list`, `decision_inbox_list`, and `memory_list` (`packages/Agentweaver.AgentRuntime/ConsoleFacadeAgent.cs:269`).

## Provider error status codes

| Failure kind | HTTP status | Notes |
|---|---|---|
| `Authorization` | `401` | Missing/invalid Copilot-entitled user token. |
| `RateLimited` | `429` | Provider limit; retryable. |
| `Configuration` | `503` | Model/runtime/configuration unavailable. |
| `ProviderUnavailable` | `503` | Transient provider health or model-list failure. |

Source: `apps/Agentweaver.Api/Endpoints/ConsoleEndpoints.cs:70`; classifier: `packages/Agentweaver.AgentRuntime/Providers/AgentProviderException.cs:41`.

## See also

- [Browser console — Deep Dive](../deep-dive/browser-console.md)
- [Browser console — Experience](../experience/browser-console.md)
- [Coordinator reference](./coordinator.md)
