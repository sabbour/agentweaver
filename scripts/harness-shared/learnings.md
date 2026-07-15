# Harness learnings

Append-only log of durable facts a harness run should already know before it starts:
discovered bugs/gotchas (including ones since fixed — keep the record so the same
mistake isn't reintroduced), environment facts, and "this is intentional, not a bug"
notes about scenario/adapter design.

Do not hand-edit this file. Append new entries with:

```powershell
node scripts/harness-shared/record-learning.mjs `
  --title "<short title>" `
  --category bug|environment-fact|scenario-design-note `
  --surface api|ui|mcp|all `
  --body "<detail>" `
  --status open|fixed  # optional, defaults to open
```

The script validates required fields and dedupes by title (case-insensitive) against
existing entries before appending — it will refuse to add an exact-title duplicate.

Each entry below follows this shape:

```
## <title>
- date: YYYY-MM-DD
- category: bug | environment-fact | scenario-design-note
- surface: api | ui | mcp | all
- status: open | fixed

<body>
```

---

## MCP stdio transport must never go through target-guard's URL validation

- date: 2026-07-14
- category: bug
- surface: mcp
- status: fixed

`client.mjs` used the literal string `"stdio"` as a transport-selector sentinel
(`options.target === 'stdio'` picks the stdio transport), but forwarded the same
`options` object into `createStdioTransport`, which called `assertTargetAllowed()` —
a URL-parsing guard meant only for the HTTP transport's network host allowlist. Since
`"stdio"` isn't a URL, this threw `target "stdio" is not a valid URL` on every stdio
connect attempt, breaking the harness's own documented smoke example. Fixed in commit
`80bf0121` by removing the guard call from `transport-stdio.mjs` entirely — stdio
spawns a local subprocess and has no network target to validate. Kept here so nobody
reintroduces the same conflation of "which transport to use" with "what network host
to validate" if the transport-selection code is ever refactored.

---

## Agentweaver MCP server endpoint path and auth requirement

- date: 2026-07-14
- category: environment-fact
- surface: mcp
- status: open

The Agentweaver MCP server's live HTTP endpoint is at the `/mcp`-suffixed path
(`https://<host>/mcp`), not the bare origin — connecting to the origin alone will not
reach the MCP endpoint. The server also requires OAuth: connecting over http
transport needs a valid, authenticated bearer token (`--token`/`AGENTWEAVER_TOKEN`),
not an arbitrary string. Obtain one via the app's own OAuth sign-in flow, or
`gh auth token` where that identity is what the server trusts. Stdio transport has
neither requirement since it never leaves the local subprocess.

---

## Staging Agentweaver environment can be undeployed between sessions

- date: 2026-07-14
- category: environment-fact
- surface: all

The staging Agentweaver environment (the `agentweaver` namespace on the
`agentweaver-aks-2` cluster) is periodically undeployed/deleted, not a permanently
available fixture. Before treating "can't reach staging" as a Harness or application
bug, run `kubectl get pods,svc,ingress -n agentweaver` first. If nothing is there,
this is an environment-availability gap to raise/redeploy, not a defect to chase in
harness or product code.

---

## `priya.api` adapter intentionally never confirms past the outcome-spec gate

- date: 2026-07-14
- category: scenario-design-note
- surface: api

`scripts/persona-briefs/surfaces/priya.api.md`'s Intent mapping ends with "Stop at
the outcome-spec confirmation gate without confirming execution." This is by design:
the scenario tests the pushback/revise-spec loop (Priya's mandatory two grounded
objections), not full-run completion. A run that stops there is working as intended,
not stuck or broken. If a caller wants a scenario that drives to full completion,
they need a different adapter/scenario, not this one.

This pattern generalizes across the catalog: every persona core in
`scripts/persona-briefs/personas/*.md` currently has a "Where to stop (safe
checkpoint)" section that stops before or at a confirmation/review gate, and every
`*.api.md` surface adapter explicitly says "Stop at the outcome-spec confirmation
gate without confirming execution." Any adapter whose Intent mapping says "stop at
X" should be treated as intentionally non-terminal, not a stuck/broken run, when
triaging a "the run didn't finish" report. See `scripts/persona-briefs/catalog.json`
for the `runsToCompletion` flag recorded per persona/surface pair.

---

## Default judge: use the Judge subagent, not an external AGENTWEAVER_JUDGE_CMD

- date: 2026-07-14
- category: environment-fact
- surface: all
- status: open

AGENTWEAVER_JUDGE_CMD (consumed by scripts/harness-judge/core.mjs's makeDefaultJudge()/makeCommandJudge()) has never been configured anywhere in this repo or environment, so every harness run to date (including live smoke runs) only ever produced the safe CANNOT_DETERMINE fallback verdict -- the judge's core evaluative step never actually ran. The fix is agent-native, not a subprocess wrapper: a new custom agent .github/agents/judge.agent.md ('Judge', tools: []) is a pure text-in/text-out reasoner with no file/shell/network access and no ability to act on anything in the (possibly untrusted/adversarial) evidence it judges -- sandboxing comes from the platform's own custom-agent tool scoping, not from manually verified CLI lockdown flags on a nested process. When running as the Harness agent: (1) build the prompt with 'node scripts/harness-judge/core.mjs <evidence.json> --prompt-out <prompt.txt>' (this works standalone, no judge command required); (2) dispatch it synchronously via the task tool with agent_type: 'Judge'; (3) parse/validate/persist the response with the new scripts/harness-judge/save-verdict.mjs (parse+validate+write only, no subprocess judge). Verified end-to-end via 'copilot --agent judge' with a real evidence fixture: produced a real, schema-valid PASS/PASS verdict (not CANNOT_DETERMINE), confirmed against verdict-schema.mjs's validateVerdict. AGENTWEAVER_JUDGE_CMD remains a secondary path only for headless/CI contexts with no agent session to dispatch a task call from.

---

## priya-ticket-triage api deterministic driver never calls revise-spec, so P1 pushback criterion predictably FAILs

- date: 2026-07-14
- category: scenario-design-note
- surface: api
- status: open

The built-in priya-ticket-triage API scenario's deterministic driver (run-persona.mjs) only submits the goal and polls run/outcome-spec/events/metrics; it has no logic to call revise-spec or raise objections. The priya persona core mandates >=2 grounded pushbacks. A real judge (dispatched via the Judge subagent) will correctly mark P1 as FAIL for missing mandatory pushback on every run of this deterministic scenario, even though P0 platform mechanics pass cleanly. This is expected given the current driver's scope (it exercises submit->draft->settle, not the pushback/revise loop) -- not a platform regression. If pushback coverage is needed, drive the scenario via the exploratory agent-driver tools (revise-spec) instead of the deterministic CLI scenario.

---

## API harness: persona scenarios are driven dynamically, not fixed scripts or curated subcommands

- date: 2026-07-14
- category: scenario-design-note
- surface: api
- status: open

scripts/api-harness/scenarios/*.mjs (fixed per-persona step sequences) and agent-driver/tools.mjs (curated named subcommands like submit-goal/revise-spec/get-spec) were both retired. Do NOT re-add either pattern. Every persona scenario (Priya, Jordan, ...) is now driven via scripts/api-harness/drive.mjs: init (session/auth), spec (fetch live OpenAPI doc, /openapi/v1.json), call --method/--path/--body/--thought (the one generic action primitive), check-approvals/resolve-approval (kept as named commands ONLY because they encode a safety invariant -- never blind-approve -- not because approvals are curated business logic), finish. This exists because a fixed script structurally cannot issue grounded pushback/objections or poll-then-adapt behavior -- exactly what personas like Priya require, and what caused a P1 FAIL when Ahmed ran the old fixed priya-ticket-triage scenario. run-persona.mjs now ONLY drives generated-artifacts-seam (a deterministic structural generator-conformance check with no persona/pushback dimension -- intentionally still fixed). See decision Morpheus-harness-dropped-fixed-per-scenario-scripts-entirely for full rationale.

---

## Persona driving is now dispatched to a fresh PersonaActor sub-agent, not driven inline by Harness

- date: 2026-07-14
- category: scenario-design-note
- surface: api
- status: open

Harness no longer reasons inline as if it were the persona while driving drive.mjs. It resolves the persona brief + target/token, then dispatches a fresh, isolated PersonaActor sub-agent (.github/agents/persona-actor.agent.md, tools: [execute]) via task (mode: sync) with the persona brief/adapter text, target/token, session path, and OpenAPI spec injected into the per-invocation prompt -- mirroring how judge.agent.md bakes in shared methodology while evidence comes from the prompt. PersonaActor decides one action at a time from the persona brief + the REAL previous API response (never pre-writing both sides of the exchange), calls it for real via drive.mjs, grounds every pushback in real response content, stops at its brief's gate, then finishes and returns the transcript to Harness for judging. drive.mjs itself is unchanged -- this is purely about WHO calls it. Known caveat: unlike Judge (tools: [], structurally isolated), PersonaActor holds a real execute tool -- its isolation from the rest of the repo is a documented prompt restriction, not a structural sandbox. Do not re-fold persona driving back into Harness reasoning inline, and do not assume PersonaActor's isolation is tool-enforced the way Judge's is.

---

## drive.mjs deleted -- PersonaActor curls the API/spec directly, no HTTP-calling script

- date: 2026-07-15
- category: scenario-design-note
- surface: api
- status: open

drive.mjs (init/spec/call/check-approvals/resolve-approval/finish) was deleted entirely -- do NOT re-add a scripted HTTP-calling/operationId-resolution/spec-caching layer between PersonaActor and the target API. PersonaActor is a real Copilot CLI agent with shell/execute access: it curls /openapi/v1.yaml directly, resolves operations from the live spec each turn via its own reasoning, issues its own curl calls with the bearer token, and appends transcript turns itself via shell redirection -- see .github/agents/persona-actor.agent.md. Approval/steer actions are just more discoverable endpoints now; the previous code-enforced default-defer approval judge (lib/approvals.mjs/lib/approval-judge.mjs) is preserved in the repo (still tested) but is unused by any production path -- the safety invariant is now a PROMPTED instruction in persona-actor.agent.md, not a structural default. Harness's Target resolution section documents the production/staging safety policy explicitly (scripts/harness-shared/target-guard.mjs remains the authoritative shared implementation, still used by ui-harness/mcp-harness) since it is no longer implicitly invoked by a drive.mjs init call. See decision Morpheus-deleted-drive-mjs-entirely-personaactor-now-curls-.

---

## Fix #272 (chat-confirm) FAIL in v0.9.56-864e2c51: steer kind=send does not transition from awaiting_confirmation

- date: 2026-07-15
- category: bug
- surface: api
- status: open

Verified against live staging (wave1-verify-20260715T004112). A run was started in defineOutcome mode and reached coordinator_status=awaiting_confirmation. A steer message with kind=send and instruction='yes, looks good, please proceed' was accepted (HTTP 201, status=applied, relayedAt set), but coordinator_status remained awaiting_confirmation across 3 polls over 45s. Event count stayed static at 216 (last event coordinator.outcome_spec/awaiting_confirmation). The steer kind=confirm verb is not even supported (returns 400 with 'Unknown steering verb confirm; supported: stop, send, redirect, amend'). Fix #272 is NOT working in this build — reopening recommended.

---

## Memory write endpoint: POST /api/projects/{id}/agents/{name}/memory

- date: 2026-07-15
- category: environment-fact
- surface: api
- status: open

The live API exposes a direct memory-write endpoint at POST /api/projects/{id}/agents/{name}/memory accepting RecordMemoryRequest body (fields: session_id, type, importance, content, tags). Returns HTTP 201 with the created item including id and created_at. Confirmed that writes persist and are immediately visible via GET /api/projects/{id}/memory (total_count increments). Verified in batch memory-write-verify-20260715T010957 against v0.9.56-864e2c51.

---

## Team member history not updated by direct memory writes

- date: 2026-07-15
- category: scenario-design-note
- surface: api
- status: open

GET /api/projects/{id}/team/members/{name}/history does NOT update when you call POST /api/projects/{id}/agents/{name}/memory directly. The history endpoint reflects only orchestration-run-driven activity. A direct memory write (HTTP 201) leaves history unchanged (confirmed: identical 430-byte response before and after write in batch memory-write-verify-20260715T010957). Do not use direct memory writes as a proxy for testing history updates; trigger a real orchestration run instead.

---

## Memory MCP tools (memory_record/memory_list/memory_search) not injected into agent function schema during live orchestration runs

- date: 2026-07-15
- category: bug
- surface: api
- status: open

Scenario memory-agent-roundtrip (batch 20260715T014105, v0.9.56/864e2c51) confirmed: the MCP tools defined in MemoryTools.cs (memory_record, memory_list, memory_get, memory_search) are NOT exposed in the agent's callable function schema when a squad agent executes a subtask in a live orchestration. The agent.tools event for the child run lists only 8 platform tools (report_intent, report_outcome, ask_question, preview/health tools); no memory tools appear. The agent (Stark, claude-sonnet-5) explicitly self-diagnosed: 'no memory_record, memory_list, or memory_search MCP tools are exposed in my available toolset.' Memory count was 13 before and 13 after — delta=0. Secondary issue: system prompt says 'Persist durable project facts with record_memory' but the registered tool name in MemoryTools.cs is 'memory_record' (name mismatch). Both issues must be fixed for the agent-driven memory round-trip to work. The raw REST memory API plumbing (POST /api/projects/{id}/agents/{name}/memory) was separately confirmed working by the coordinator calling it directly. Verdict: FAIL — agent-driven memory round-trip is broken. Squad defect for triage.

---

## Skill instructions NOT injected into agent context during live orchestration runs (v0.9.56)

- date: 2026-07-15
- category: bug
- surface: api
- status: open

Scenario skill-usage-verify (batch 20260715T015920, v0.9.56/864e2c51) confirmed: skill instructions assigned to agent Rogers via PUT /api/projects/{id}/skills/{skillId}/assignments/{agentName} (HTTP 204, persisted in API) are NOT injected into the agent's system prompt or callable tool list during live orchestration execution. agent.system_prompt event for Rogers child run 3320174d: 4608-char prompt contains zero skill-related content. agent.tools event: 8 standard platform tools only, no skill functions. Unique verification token SKILL-ACTIVE-SKL-9X3M7-HARNESS-VERIFY absent from all 23 child run events. Rogers produced a correct 14424-char research document with no trace of skill instructions. This is the same symptom as #335 (memory-tool-injection bug): both skill instructions and memory tools are wired in the API layer but the runtime context assembly in v0.9.56 ignores both when starting agent subtasks. Skill CRUD and assignment API itself works correctly (stages 1-2 PASS). New issue recommended for squad triage.

---

## transport-http.mjs URL capture bug: assertTargetAllowed returns void, not the URL

- date: 2026-07-15
- category: bug
- surface: mcp
- status: fixed

transport-http.mjs had 'const url = assertTargetAllowed(target, ...)' but assertTargetAllowed() is a void assertion (returns undefined), so url was always undefined and StreamableHTTPClientTransport threw 'Failed to parse URL from undefined' on every HTTP smoke attempt. Fixed this session by removing the const url = capture and passing new URL(target) directly to StreamableHTTPClientTransport. Unit tests still pass (all 10). Root cause: assertTargetAllowed's signature implies an assertion, not a transformer — its return value is meaningless. If anyone modifies transport-http.mjs again, assertTargetAllowed must be called for side-effect only.

---

## MCP outputSchema null for all run-workflow tools: surface regression in v0.9.56

- date: 2026-07-15
- category: bug
- surface: mcp
- status: open

All 5 run-workflow MCP tools (run_submit, run_status, run_show_artifacts, run_task, run_archive) return outputSchema: null in tools/list. The required-capabilities.json contract requires declared output schemas for run_submit (run_id+status), run_status (status), run_show_artifacts (artifacts), and run_task (run_id+status+artifacts). This causes the capabilities contract check to FAIL on 4 of 8 required capabilities (submit-run, poll-run, list-artifacts, one-call-run). Note: runtime behavior IS correct -- run_submit returns run_id+status, run_status returns status, etc. -- but the schema declarations are absent. The fix is to add [return: Description] or schema annotations to these tools in MemoryTools.cs/RunTools.cs so the MCP SDK exposes outputSchema in tools/list. Verified in mcp-stress-run-20260715T032543 against v0.9.56-864e2c51.

---

## MCP run_submit and run_task mark optional params as required in inputSchema

- date: 2026-07-15
- category: bug
- surface: mcp
- status: open

run_submit marks agent_name, base_branch, model_source as required in its inputSchema even though they are functionally optional (empty string works). Calling without them throws System.ArgumentException from .NET reflection-based parameter binding. Similarly run_task marks workflow_id, model_id, start_mode, timeout_seconds, poll_interval_seconds as required. The current capabilities-contract.mjs does not catch this regression because it only checks that ITS known required fields (project_id, task) are still required -- it does not flag NEW required fields being added. Workaround: always pass these optional fields as empty strings. Fix: mark them as non-required in the C# method signature (use nullable or default params). Verified in mcp-stress-run-20260715T032543 against v0.9.56-864e2c51.

---

## MCP tool exceptions use generic error message, actual error detail is lost to client

- date: 2026-07-15
- category: bug
- surface: mcp
- status: open

When MCP tools throw exceptions, the server returns isError:true with a generic 'An error occurred invoking <tool>.' message. The actual business logic error (e.g. 'Run artifacts are not ready yet.', 'Invalid project id.', 'Invalid run id.', 'This project has no team.') is only visible in server-side pod logs, never surfaced to the MCP client. This prevents agents using these tools from understanding and recovering from specific error conditions. The server logs do include a 'hint' field (e.g. 'Call run_status to confirm the run is in a review-ready or terminal state, then retry.') but it is also not surfaced. Fix: expose the McpApiException error/hint fields in the MCP error content rather than swallowing them in a generic message. Verified in mcp-stress-run-20260715T032543 against v0.9.56-864e2c51.

---

## mcp-cli-smoke.mjs --list flag documented in SKILL.md but not implemented

- date: 2026-07-15
- category: bug
- surface: mcp
- status: open

SKILL.md documents 'node scripts/mcp-harness/smoke/mcp-cli-smoke.mjs --list' to list built-in persona-driven MCP scenarios, but the flag has no handler in the source file. Running it without --target/--server-command triggers stdio transport which throws 'A stdio server command is required'. The --list codepath was either never implemented or was removed. Until fixed, the only way to discover MCP scenarios is to read scripts/persona-briefs/catalog.json directly.

---

## run_task outputSchema has 'run:true' boolean property causing MCP SDK Zod validation failure

- date: 2026-07-15
- category: bug
- surface: mcp
- status: open

run_task's outputSchema declares 'run' property as boolean true (meaning 'any value' in JSON Schema) rather than as a proper type schema. This causes the MCP SDK's Zod validator to throw ZodError 'Invalid input' at tools[N].outputSchema.properties.run when calling tools/list via the McpHarnessClient. Verified in v0.9.58 (ab0aeff7d459). The runtime behavior IS correct (run_task returns a full run object), but the declared schema uses boolean schema syntax that the @modelcontextprotocol/sdk client rejects. Fix: change 'run: true' to 'run: { type: object }' or add a proper object schema with the RunSummary shape. Impact: any MCP client using the SDK's strict schema validation fails to enumerate tools at all. Workaround: use raw JSON-RPC calls that skip Zod validation.

---

## run_task outputSchema boolean true fixed in v0.9.59

- date: 2026-07-15
- category: environment-fact
- surface: mcp
- status: fixed

The #341 bug (run_task outputSchema had 'run: true' boolean JSON Schema property causing MCP SDK Zod validation failure on tools/list) is CONFIRMED FIXED in v0.9.59. Verified 2026-07-15 via real @modelcontextprotocol/sdk TypeScript client: tools/list succeeded, returned 91 tools, and run_task.outputSchema.properties.run is now {'type':['object','null'],'properties':{'run_id':...}} — a proper typed schema, not boolean true. The fix replaced RunTaskResult.Run (was JsonElement?) with a typed RunEmbedded record in apps/Agentweaver.Mcp/Tools/RunTools.cs.

---

## MCP run-workflow outputSchemas null fixed in v0.9.59

- date: 2026-07-15
- category: environment-fact
- surface: mcp
- status: fixed

The v0.9.56 bug (all run-workflow MCP tools returned outputSchema: null) is CONFIRMED FIXED in v0.9.59. Verified 2026-07-15: run_submit.outputSchema.properties = ['run_id','status','start_mode'], run_status.outputSchema.properties = ['status'], run_show_artifacts.outputSchema.properties = ['artifacts'], run_task.outputSchema.properties = ['run_id','status','artifacts','run','error','hint','review_prompt']. Required-capabilities.json contract checks for these fields will now pass.

---

## MCP run_submit and run_task optional params marked required fixed in v0.9.59

- date: 2026-07-15
- category: environment-fact
- surface: mcp
- status: fixed

The v0.9.56 bug (run_submit marking agent_name/base_branch/model_source as required, run_task marking workflow_id/model_id/start_mode/timeout_seconds/poll_interval_seconds as required) is CONFIRMED FIXED in v0.9.59. Verified 2026-07-15: run_submit.inputSchema.required = ['project_id','task'] only, run_task.inputSchema.required = ['project_id','task'] only. Callers no longer need to pass empty-string workarounds for optional parameters.

---

## MCP smoke test times out: run stays in awaiting_confirmation, never reaches terminal status

- date: 2026-07-15
- category: bug
- surface: mcp
- status: open

In v0.9.59, mcp-cli-smoke.mjs timed out after 240 seconds (2026-07-15). The smoke test submits a task via run_submit with a minimal goal and polls run_status every 2s, but the run never reached a terminal status (completed/failed/cancelled/archived) — it got stuck in an awaiting_confirmation-like state consistent with the #272 'steer kind=send does not transition from awaiting_confirmation' bug. The smoke test requires terminal status to proceed to artifact/cleanup steps. Workaround: use a project that already has a team configured for a workflow that will naturally complete, or extend the smoke test to detect awaiting_confirmation and steer/confirm if the run reaches that state. The smoke test's goal was 'Create a minimal smoke-test task and stop at the reviewable result' — but the coordinator reached a confirmation gate and had no one to confirm it.

---

## MCP team_get and team_cast return 'Project not found' for projects without initialized workspace

- date: 2026-07-15
- category: bug
- surface: mcp
- status: open

team_get and team_cast MCP tools return isError:true with 'Project X not found' even when project_get and project_list confirm the project exists (state:active). Reproduced 2026-07-15 on v0.9.59 for both a freshly-created blank project (5fb39a60-...) and an existing blank project (2ca06f67-... 'MCP Harness Smoke Test'). Root cause: team operations look up the team from the project's git workspace (which must have .squad/agents/ charaters) — a blank project never has its workspace initialized. The error message 'Project not found' is misleading: the project IS found in the database, but the workspace files don't exist. The Oracle Demo project (a80d1db5-...) works correctly because it has an initialized workspace with cast team members. Fix: return a more accurate error like 'No team configured for this project' or 'Project workspace not initialized' instead of 'Project not found'. This misleads callers into thinking the project ID is wrong.

---

## MCP persona driver (general-purpose agent) writes PowerShell DateTime ts format instead of ISO 8601

- date: 2026-07-15
- category: bug
- surface: mcp
- status: open

When the general-purpose agent acts as the MCP persona driver and writes transcript JSONL turns, it uses PowerShell's default DateTime.ToString() format ('7/15/2026 2:02:10 PM') instead of the ISO 8601 format required by the AGENT.md spec ('2026-07-15T14:02:10Z'). This makes timing derivation unreliable (tools parsing the ts field may fail or produce wrong results) and does not match the transcript schema. Observed in the jordan-live-2026-07-15T13-56-19-444Z.jsonl and priya-live-2026-07-15T13-56-27-230Z.jsonl transcripts from batch mcp-v0959-stress. The dispatch prompt should explicitly instruct agents to use new Date().toISOString() or equivalent for the ts field, or the AGENT.md should be updated to emphasize the ISO 8601 requirement more prominently.

---

## MCP team_cast inputSchema marks mutually exclusive params as required

- date: 2026-07-15
- category: bug
- surface: mcp
- status: open

team_cast.inputSchema.required = ['project_id','goal','confirm_proposal_id','confirm'] even though goal and confirm_proposal_id are described as mutually exclusive (goal is required unless confirm_proposal_id is set). Passing empty string for confirm_proposal_id when using goal mode results in a 'Project not found' error rather than a meaningful validation error. This is a variant of the same optional-params-marked-required pattern that was fixed for run_submit/run_task in v0.9.59, but team_cast was not fixed in the same release. Verified 2026-07-15 on v0.9.59.

---

## MCP smoke test must confirm awaiting_confirmation gate (fix #345)

- date: 2026-07-15
- category: bug
- surface: mcp
- status: fixed

The smoke script (scripts/mcp-harness/smoke/mcp-cli-smoke.mjs) had no logic to handle the coordinator_status=awaiting_confirmation state. Any coordinator goal that produces a reviewable outcome spec suspends at this gate; with no confirm call, the smoke test polled for 240s then timed out as a false failure. Fix: detect coordinator_status=awaiting_confirmation in the poll loop and call coordinator_outcome_spec_confirm (the correct real tool, confirmed in CoordinatorTools.cs) once before continuing to poll. Point-3 verification: MCP coordinator_outcome_spec_confirm calls POST /api/runs/{id}/outcome-spec/confirm -> CoordinatorRunService.ConfirmOutcomeSpecAsync -> SubmitDecisionAsync -- the exact same backend resume seam used by the API surface (confirmed working in #272). No MCP-specific blocker exists; the confirm mechanism genuinely works over MCP. The issue was purely that the smoke path never invoked it.
