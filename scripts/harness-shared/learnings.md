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
`agentweaver-aks` cluster) is periodically undeployed/deleted, not a permanently
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
- status: resolved

AGENTWEAVER_JUDGE_CMD (consumed by scripts/harness-judge/core.mjs's makeDefaultJudge()/makeCommandJudge()) has never been configured anywhere in this repo or environment, so every harness run to date (including live smoke runs) only ever produced the safe CANNOT_DETERMINE fallback verdict -- the judge's core evaluative step never actually ran. The fix is agent-native, not a subprocess wrapper: the custom agent .github/agents/judge.agent.md ('Judge', tools: []) is a pure text-in/text-out reasoner with no file/shell/network access and no ability to act on anything in the (possibly untrusted/adversarial) evidence it judges -- sandboxing comes from the platform's own custom-agent tool scoping, not from manually verified CLI lockdown flags on a nested process. `scripts/mcp-harness/run-persona.mjs finalize` now directly supports `--dump-evidence <evidence.json> --prompt-out <prompt.txt>`: it performs the MCP-specific transcript adaptation and P0 calculation, then writes normalized evidence and the shared prompt without judging. When running as the Harness agent: (1) use that finalize export mode; (2) dispatch the prompt synchronously via the task tool with agent_type: 'Judge'; (3) parse/validate/persist the response with scripts/harness-judge/save-verdict.mjs (parse+validate+write only, no subprocess judge). AGENTWEAVER_JUDGE_CMD remains a secondary path only for headless/CI contexts with no agent session to dispatch a task call from.

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

---

## generation-seam scenarios must not send null adapterVersion/personaCoreVersion

- date: 2026-07-16
- category: bug
- surface: api
- status: fixed

generated-artifacts-seam (kind: 'generation-seam') is a structural, non-persona scenario -- loadPersona() correctly returns null for it. run-persona.mjs previously set metadata.adapterVersion/personaCoreVersion to null in that case, which failed scripts/harness-judge/core.mjs validateEvidenceShape (REQUIRED_JOIN_KEY_FIELDS requires non-empty strings for every surface), crashing with 'invalid normalized evidence: metadata.adapterVersion/personaCoreVersion must be a non-empty string' instead of producing a verdict. Fixed by falling back to the same 'unknown' sentinel already used for this exact no-persona case in scripts/mcp-harness/run-persona.mjs (adapterVersion/personaCoreVersion) and scripts/ui-harness/agent-driver-ui/tools.mjs (targetRevision), via a new NO_PERSONA_VERSION_SENTINEL constant in scripts/api-harness/run-persona.mjs. Real persona scenarios are unaffected -- they still report their genuine content-hash-derived versions; the shared verdict-schema.mjs validation itself was left unchanged.

---

## Staging Operator Assistant binds AgentHost but fails on A2A None stream event before any tool call

- date: 2026-07-21
- category: bug
- surface: api
- status: fixed (PR #376)

Verified live against staging on 2026-07-21 immediately after the McpEndpoint/network-policy hotfix (PR #375). POST /api/assistant/runs with an initial message, and POST /api/assistant/runs/{id}/messages on an empty run, both now create/bind real Operator runs and warm AgentHost pods (events include sandbox.execution_pod.bound; AgentHost log shows /configure for purpose=OperatorAssistant) with NO observed 'AgentHost:McpEndpoint must be configured' errors in recent AgentHost logs. However the first turn still fails before any tool.call/tool.result event with run.error agenthost_unavailable: RemoteAgentProxy rejects an unsupported A2A stream event 'None' ('Only message, task, task update events are supported from A2A agents. Received: None'). Example run ids: 5ce61ce2-7590-4583-b4d7-e11c5d390751, ca8906f1-d423-483c-9b48-3d1dd24e3411, aa36d689-3b14-4b65-bd3a-8d5d900a17de.

Root cause: `A2ATurnBridgeAgent` (DelegatingAIAgent wrapping the singleton `CopilotAIAgent`) never overrode session creation, so MAF's A2A session store called `CopilotAIAgent.CreateSessionCoreAsync` on every new message regardless of `AgentHostPurpose` -- and `AgentHostStartupService` deliberately skips `CopilotAIAgent.SetupAsync` for `OperatorAssistant` purpose, so it threw `SetupAsync must be called before CreateSessionAsync` before every turn, which the A2A proxy then surfaced as the bare, unclassified `Received: None` stream event. Fixed in PR #376 (`apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs`): `CreateSessionCoreAsync` now bypasses the inner agent for `OperatorAssistant` purpose.

**Re-verified live on 2026-07-21 (post redeploy of commit `4f57729b861f`)** via a dedicated Harness/PersonaActor E2E run (Oracle persona) cross-checked directly against AgentHost pod logs (not just API responses): confirmed pod logs now show `AgentHostStartupService: operator assistant purpose configured...; skipping sandbox provisioning` -> `RemoteAgentProxy: SetupAsync complete` with **no exception**, and the bare `Received: None` error did not reproduce in any of 3 runs. This specific defect is CONFIRMED FIXED. See the new entry below for a *different*, still-open bug (NetworkPolicy egress) that now blocks Operator Assistant end-to-end for an unrelated reason.

---

## Staging Operator Assistant still non-functional end-to-end: AgentHost cannot reach in-cluster MCP service (NetworkPolicy egress gap)

- date: 2026-07-21
- category: bug
- surface: api
- status: fixed (PR #381)

Discovered while re-verifying the PR #376 SetupAsync fix (see entry above) against a fresh staging redeploy of commit `4f57729b861f`. With the original bare `Received: None` bug confirmed fixed, Operator Assistant first turns still fail 100% (3/3 runs), now with a different, structured error: `agenthost_unavailable` / `ProviderUnavailable` / "Initialization timed out". Root cause confirmed via direct `kubectl exec ... curl` from a live AgentHost pod to the in-cluster MCP service: both the DNS name (`agentweaver-mcp`) and the ClusterIP (`10.0.215.29:8080`) time out at the TCP layer (curl exit 28) -- `AgentweaverMcpToolProvider.ConnectAsync` -> `McpClient.ConnectAsync` blocks for ~60s before failing. Two NetworkPolicies selecting agent-host pods (`agenthost-egress-allowlist`, `agentweaver-agent-host-network-policy`) explicitly exclude `10.0.0.0/8` (which contains the MCP ClusterIP) from egress. A third, more permissive policy (`sandbox-egress-allowlist`) also selects the same pods but does not appear to actually permit the traffic in practice -- the precedence/interaction between these 3 overlapping NetworkPolicies is unclear and needs investigation. Net effect: Operator Assistant is completely non-functional end-to-end on staging, purely due to this network policy gap (unrelated to PR #376's session-creation fix, which is independently confirmed working). Example failing run ids: `0c3dd3bc-1ee7-4c6e-9ead-67ea51312d7e`, `4338a887-c072-47f9-a98f-e9d6f6d88d48`, `820aaff3-20fa-46ba-a783-8726fe0d4f5d`. Evidence: `scripts/api-harness/verdicts/agenthost-fix-validation-evidence.json`, `scripts/api-harness/verdicts/agenthost-fix-validation-verdict.json`.

Fixed in PR #381 (`k8s/base/networkpolicy-agenthost-egress.yaml`): added an explicit, tightly-scoped egress rule in `agenthost-egress-allowlist` allowing AgentHost pods to reach `app: agentweaver-mcp` on TCP 8080 (not a blanket `10.0.0.0/8` allow -- kept scoped to the actual MCP pod selector, matching this repo's existing narrow-scoping convention). `sandbox-egress-allowlist` alone was confirmed insufficient under the live kata/ACNS enforcement path even though it looked broader on paper. **Re-verified independently live** on the redeployed staging environment: a fresh `POST /api/assistant/runs` call got a real `201` response with a genuine tool call (`tools_invoked:["project_list"]`) and a correct assistant reply listing the user's actual project -- not just a curl-level TCP check.

---

## Staging Operator Assistant: AgentHost pod fails /healthz readiness within 90s (new failure mode, blocks PR #467 tool-approval verification)

- date: 2026-07-25
- category: bug
- surface: api
- status: open

Discovered 2026-07-25 during post-security-hardening-batch regression hunt (PRs #460-485 merged 2026-07-23). 3 independent POST /api/assistant/runs attempts against staging all failed with HTTP 503 agenthost_unavailable/ProviderUnavailable after ~90s: 'AgentHost pod ... did not become ready at http://<pod-ip>:8088/healthz within 90s; failing the launch.' Confirmed on 2 distinct pods (agentweaver-agent-host-vdfjz, agentweaver-agent-host-4hthh, different IPs) ruling out a single-node flake. Event streams show run.started -> agent.message -> sandbox.execution_pod.bound -> run.error with zero tool.call/tool_approval events ever emitted -- the pipeline never reaches PR #467's fail-closed tool-approval gating logic, so that specific security regression question is CANNOT_DETERMINE, not pass/fail. This is a NEW failure mode, distinct from the previously-fixed #376 (session-creation SetupAsync bug) and #381 (NetworkPolicy egress gap) -- both of those are believed already fixed; this is a fresh /healthz readiness timeout. Also observed two run-bookkeeping inconsistencies: GET /api/runs/{id} reports status=in_progress (ended_at/result null) despite a terminal run.error event, and a subsequent POST /api/assistant/runs returned 201 with a run_id that turned out to be a stale reused run (identical events, no new activity) rather than a fresh run. Example run ids: ef478f66-66d1-4995-a6ce-59f5876e18ed, 9a792528-7538-4d72-aeec-2618ae03cdbc. Evidence: scripts/api-harness/transcripts/oracle-operator-20260725T033449.jsonl, scripts/api-harness/verdicts/oracle-operator-verdict.json.

Root-caused to a code/manifest gap in PR #463: the production mTLS overlay (`k8s/overlays/production/patch-agenthost-mtls.yaml`) set `AgentHost:RequireMtls=true`, which disables the plain-HTTP Kestrel fallback bind in `apps/Agentweaver.AgentHost/Program.cs`, but no `Kestrel:Endpoints:A2A` config was ever added to bind the secure listener using the mounted server cert -- so Kestrel bound zero endpoints and silently fell back to `ASPNETCORE_HTTP_PORTS` (8080), leaving nothing listening on 8088. Verified via `kubectl exec` that `curl 127.0.0.1:8088` from inside the pod itself returns instant connection-refused (rules out NetworkPolicy, which would time out/drop silently instead). Certs were correctly generated and mounted (not an install-crash). Filed as issue #499, fixed in PR #500 (adds `Kestrel:Endpoints:A2A` wiring plus CA-pinned `ClientCertificateMode.RequireCertificate` validation, security-reviewed by Seraph).

---

## Run pages 403 after Outcome confirm when org membership cannot be re-verified

- date: 2026-07-25
- category: bug
- surface: ui
- status: open

On staging v0.11.1, a browser session can still load /projects and render authenticated chrome, but after confirming an orchestration outcome plan the run routes can flip to Permission required with GET /runs failed (403). The API error body is: 'Could not verify membership of the required GitHub organization. Ensure your org membership is set to Public in GitHub org settings (the private membership endpoint is blocked by SAML SSO enforcement).' In the 2026-07-25 blueprint-demo dry-run, parent run b42fa206-a0cf-4884-ab57-574371baf89f reached awaiting_confirmation, revise took ~18s, confirm returned 200, then the run page blocked and the child run 660a35bd-e5a1-4e54-8c52-39cede8b7815 could not be opened due to this 403. This is distinct from the earlier AgentHost mTLS launch failure: the child run was dispatched, but UI/API access to run details failed on org-membership re-verification.

---

## Staging AgentHost readiness: api/worker client mTLS flag mismatched vs AgentHost server (fixed)

- date: 2026-07-25
- category: bug
- surface: api
- status: fixed

Follow-on to #499/#500: patch-agenthost-mtls.yaml correctly flips the AgentHost pod's own Kestrel A2A listener to https-only mTLS (RequireMtls=true), but never flipped the matching client-side Sandbox__AgentHost__RequireMtls env var on api-deployment.yaml/worker-deployment.yaml, which still defaulted to the PoC 'false' value from k8s/base. Left mismatched, api/worker build plain http:// AgentHost readiness-probe/A2A URLs against a TLS-only listener, so every readiness check's connection got dropped mid-handshake (HttpIOException: response ended prematurely) -- a deterministic mismatch, not transient flakiness, once the mTLS overlay patch was applied. Confirmed via kubectl: agenthost-config configmap on the pod showed RequireMtls:true/https://0.0.0.0:8088, while both agentweaver-api replicas and the worker replica showed Sandbox__AgentHost__RequireMtls=false. The client cert secret (agentweaver-a2a-client-tls) was already provisioned and mounted, so no cert work was needed -- just the env var. Immediate unblock applied live via kubectl set env on staging; permanent fix added as k8s/overlays/production/patch-agenthost-mtls-client.yaml (new patch flipping both Deployments' env var to true), wired into kustomization.yaml patches list. Verified the built manifest via kubectl kustomize k8s/overlays/production shows RequireMtls=true for both api and worker containers after the fix.

---

## Backlog pickup run can stall in outcome-plan drafting

- date: 2026-07-25
- category: bug
- surface: ui
- status: open

On staging build f2e7983, a coordinator run spawned by board autopilot from a Ready backlog task (`ea20292c-e034-4f2d-94a8-2e53a0415eee` in project `e93c3b6d-5501-4b6f-85ac-5d14bb65c612`) remained stuck in `coordinator_status=drafting` for more than 6 minutes. Persisted state never advanced past three events (`run.options_set`, `coordinator.started`, `coordinator.outcome_spec.drafting`); `/api/runs/{id}/outcome-spec` stayed `status=drafting` with empty `desiredOutcome`/`scope`/`assumptions`, and no work plan or approval surfaced. This is distinct from direct orchestrations in the same project, which continued to draft/confirm normally.

---

## Assembly build-test can fail at AgentHost configure after all subtasks finish

- date: 2026-07-25
- category: bug
- surface: ui
- status: fixed

On staging build f2e7983, direct bug-fix run `c6a6eb31-00dc-4898-aac3-e41964cfe3da` confirmed successfully with independent task promotion left OFF, persisted work plan 12, and drove three child subtasks (`7ab82034-20fd-4fb6-917f-f09ec32d473d`, `70d26d58-74f1-4dbd-b1fa-4351a3fa5cba`, `9c1e6e4d-ca5a-4cb9-8d79-1fba9bed167d`) all the way to `assemble_ready`. RAI then passed green, preview applicability reported `preview_required`, and assembly immediately failed at the Build & Test gate with `build_test_infra_agenthost_configure_failed`: `AgentHost /configure for run 'c6a6eb31-00dc-4898-aac3-e41964cfe3da' failed: HTTP 500`. No preview or review surface followed, so this is now the hard blocker after child execution completes.

**Root cause identified (2026-07-25, commit 06007b71) — cross-pod assembly-lease race via `CoordinatorReconciler`'s `in_review` orphan re-arm.** Reproduced again on run `d2ad3035-afef-4e74-93e9-a7c45bfb60ee` (work plan 15) with full kubectl log evidence from `deploy/agentweaver-worker` and `deploy/agentweaver-api`:

1. `20:07:22` — worker pod's `CoordinatorReconciler.SweepAsync` logs `"re-arming orphaned coordinator assembly for run d2ad3035... (status was in_review)"`. Per the `InReview` case in `SweepAsync` (`CoordinatorReconciler.cs` ~line 182-205), this only fires when BOTH `HasPendingReviewGateAsync` returns false AND `IsAssemblyActive(plan)` (an in-memory, per-pod-only fast path) returns false on the sweeping pod.
2. With auto-approve enabled (as in this demo), the review gate the api pod armed can be resolved almost instantly — there is a narrow window where `WorkPlans.Status` is still `in_review` in the DB but the gate's "pending" row has already been cleared by the owning pod (api), and the owning pod hasn't yet advanced `Status` off `in_review`. A worker-pod sweep landing in that window sees "no pending gate" + "not active in MY process memory" (true regardless of api's real in-process state, since `IsAssemblyActive` is per-pod) and concludes — incorrectly — that the run is orphaned.
3. `20:07:46`-`20:07:49` — worker's re-armed assembly loop starts `KubernetesSandboxExecutor`, finds the AgentHost `SandboxClaim` for this run already exists (created/owned by the api pod's still-live assembly), deletes and recreates it, binds it to a new pod, and begins an in-flight `/configure` call.
4. `20:07:52` — the worker's OWN assembly-lease heartbeat (`AssemblyHeartbeatTickAsync`) ticks and discovers the api pod actually owns `WorkPlans.CoordinatorPodId` for this plan (logged as `"Assembly lease for plan 15 is owned by peer pod agentweaver-api... stopping assembly heartbeat"`). Note: this ONLY stops the heartbeat — it does NOT cancel the already-running `RunAssemblyCoreAsync`/sandbox-executor work on the worker pod, so the worker's in-flight configure call and the collision it caused are not aborted.
5. `20:07:54` — the sandbox claim/AgentHost pod (the one the worker's in-flight `/configure` call is targeting) gets deleted/released — likely by the TRUE owner (api pod) continuing its own legitimate lifecycle and tearing down what it (correctly) still believes is its own claim, unaware the worker pod had just deleted-and-recreated it out from under it.
6. `20:07:56` — the worker's in-flight `/configure` call surfaces as `AgentHostConfigureException: HTTP 500` (pod deleted mid-request), non-retryable, failing the whole assembly.

This is a genuine, timing-dependent multi-pod race (not deterministic — it requires the reconciler's sweep to land in the narrow post-auto-approve gate-clear window), distinct from the already-fixed `#239` stale-lease issue the reconciler was built to guard against. Root cause is in `CoordinatorReconciler.HasPendingReviewGateAsync`'s interaction with the timing of the owning pod's gate-clear vs. status-transition writes — NOT yet fixed. Workaround: none identified yet other than retrying (the window is narrow, so most attempts do not hit it). Do not attempt a rushed fix to this cross-pod reconciliation path without careful review — getting it wrong risks reintroducing the original stuck-forever-in-review bug (#239) this reconciler exists to prevent.

**Fixed (2026-07-25):** the true root cause was even more precise than the narrative above — `CoordinatorAssemblyService`'s human-review gate handling flipped `WorkPlans.Status` to `WorkPlanStatus.InReview` via `SetStatusAndStageAsync` BEFORE calling `CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync` to create the durable `AssemblyReviews` row (the upsert ran last, after graph-emission and an event emit). This left a genuine window — not merely "gate resolved but status not yet advanced" but "status already `InReview`, review row not yet written at all" — where `CoordinatorReconciler.HasPendingReviewGateAsync` (the authoritative cross-pod signal `SweepAsync`'s `InReview` case relies on) returns false, so a peer pod's sweep concludes the run is orphaned and re-arms assembly. This reproduced 3 times total on staging (runs `d2ad3035`, and again right at the review-gate transition on a retry that had otherwise reached `awaiting_review` cleanly), which is what proved the window wasn't as rare/narrow as first assumed. Fix: swapped the order in `CoordinatorAssemblyService.cs` so `UpsertReviewRequestAsync` now runs BEFORE `SetStatusAndStageAsync(..., WorkPlanStatus.InReview, ...)` — any sweep observing `InReview` now always also observes the pending row, closing the window entirely. `UpsertReviewRequestAsync` has no dependency on `WorkPlans.Status` so reordering is safe. Added a deterministic regression test (`RunAssembly_ReviewGate_NeverExposesInReviewStatusBeforeReviewRowExists` in `CoordinatorAssemblyServiceTests.cs`) that polls the DB concurrently with the gate opening and fails if `InReview` is ever observed with no backing review row — this is a true invariant post-fix (not just "less likely"), since the two sequential awaited DB writes are strictly ordered. Existing 73-test suite for this class still passes unchanged.

---

## Fresh bug-fix coordinator web_fetch approvals now fail with generic 500

- date: 2026-07-25
- category: bug
- surface: ui
- status: fixed

On staging build f2e7983, two fresh direct bug-fix runs that otherwise had the correct issue URL blocked during coordinator grounding on the first `web_fetch https://github.com/sabbour/agentweaver-demo-dryrun/issues/1` approval. Run `db469dc6-7dda-4464-8521-c0048a4e7398` requested approval at `2026-07-25T15:37:01.3414140+00:00`; manual POST to `/api/runs/{id}/tool-approvals` returned `500 {"error":"An unexpected error occurred."}` and the run remained stuck in `coordinator_status=drafting` with repeating `tool.approval_pending`. A second fresh project/run (`c04b41ee-c8d1-4654-b111-02a9620e4841`) reproduced the same failure even when `auto_approve_tools=true` was enabled immediately after run creation: approval required at `2026-07-25T15:41:56.5762430+00:00`, and repeated manual approve still returned the same generic 500 (captured again at `2026-07-25T15:43:54.054Z`). This regresses the earlier coordinator-approval-path fix and currently blocks reaching outcome confirmation, decompose, and the later Build & Test repro path.

Root-caused via kubectl API-pod logs at the exact 500 timestamps: `System.FormatException: Guid should contain 32 digits...` at `RunId.Parse` inside `RunEndpoints.cs`'s `/tool-approvals`/`/tool-denials` handlers. `EndpointHelpers.ResolveApprovalOwningRunIdAsync` can legitimately return a synthetic coordinator-phase key (e.g. `{runId}-coordinator-draft`) used only to key the approval-gate lookup for coordinator-phase LLM turns — not a real run-store row — but both endpoints unconditionally called `RunId.Parse(targetRunId)` whenever `targetRunId != id`, crashing on this synthetic (non-GUID) string. Fixed by adding `EndpointHelpers.IsCoordinatorPhaseSuffixedId(candidateRunId, postedRunId)` so callers can distinguish "same run, different gate-lookup key" from "genuinely different child run requiring `RunId.Parse`". Regression test added (`Approve_CoordinatorPhaseApproval_DoesNotCrash_AndResolvesToCoordinatorRun`), verified to fail pre-fix/pass post-fix. Fixed in PR #506, deployed to staging.

---

## Fresh bug-fix run now approves correctly but build-test rejects docs-only repo

- date: 2026-07-25
- category: bug
- surface: ui
- status: fixed

On staging build 43632442177b with api scaled to 1 replica, fresh run 7d3f8d19-2494-492d-9e79-b912a1199ca7 in fresh project 17124a09-4eeb-4602-a6b3-61677b12a93c proved the coordinator approval fix: the first coordinator-draft web_fetch https://github.com/sabbour/agentweaver-demo-dryrun/issues/1 approval succeeded with HTTP 200 at 2026-07-25T18:41:24.239Z instead of failing. The run then reached outcome confirmation, persisted work plan 13, dispatched/ran children, and reached assembly Build & Test. But Build & Test did not reproduce the earlier AgentHost /configure failure; instead it revised at 2026-07-25T18:49:44.8970787+00:00 with feedback that the repository contains no source code, package manifests, build config, or tests -- only .squad/, .agentweaver/, and docs/bugfix/issue-1-triage.md. Preview was skipped as not applicable (llm_docs_or_non_runtime), and the coordinator redispatched subtasks 18/19/20 rather than reaching preview/review/merge. This blocks the intended recording path because the seeded repo no longer presents a runnable bug-fix target.

Root cause: sabbour/agentweaver-demo-dryrun was genuinely empty (gh api repos/.../contents returned "This repository is empty", gh repo view showed isEmpty: true, no default branch) -- a pre-existing gap in demo environment setup, not a code bug. Fixed by seeding a minimal, dependency-free static web app (index.html/styles.css/package.json/build.js/test.js) with an intentional, verified-reproducing CSS bug matching issue #1 (banner absolutely positioned at 96px base height; tablet-breakpoint main margin-top drops to 64px, less than the banner's realistic ~140px wrapped height on narrow tablet widths) and pushing it to main. test.js was verified locally to fail pre-fix with a clear message and pass once the CSS margin is corrected, giving Build & Test something genuine to catch.

---

## Org-membership check flip-flops between 200 and 403 across adjacent polls even when membership is public

- date: 2026-07-25
- category: bug
- surface: api
- status: fixed

During demo-recording runs, /api/runs/... /work-plan and /events routes intermittently returned 403 "Could not verify membership of the required GitHub organization. Ensure your org membership is set to Public..." (OrgAuthResult.OrgAccessNotGranted) even though the caller's microsoft org membership was confirmed already-public (gh api orgs/microsoft/public_members/<login> returns 204). The same login/org combination alternated between success and this 403 across polls seconds apart in the same session.

Root cause: in GitHubOrgAuthorizationService.ResolveSingleOrgAsync (apps/Agentweaver.Api/Auth/GitHubOrgAuthorizationService.cs), when the primary AUTHENTICATED private-members check returns a SAML-enforcement 403 (CheckResult.OrgAccessNotGranted), the code correctly falls back to an UNAUTHENTICATED public_members check. That fallback call is itself subject to GitHub's 60/hr-per-IP unauthenticated rate limit (shared across the whole cluster's egress NAT IP). CheckEndpointAsync already classifies a rate-limited response as CheckResult.Inconclusive, but ResolveSingleOrgAsync never inspected the public fallback's result for Inconclusive -- it only special-cased publicResult == CheckResult.Member, and otherwise fell through to branch on the stale primary orgResult alone. A rate-limited (thus inconclusive) public-fallback check was silently conflated with "definitively not a public member," so whenever the primary check had also been a SAML 403, the aggregate result came back as the actionable-looking "authorize SSO" denial (OrgAuthResult.OrgAccessNotGranted) instead of a retryable Inconclusive -- even though the membership genuinely was public and only the fallback lookup itself had transiently hit the unauthenticated rate limit.

Fix: ResolveSingleOrgAsync now checks publicResult == CheckResult.Inconclusive before falling through to the orgResult-based branches, returning the same "transient, not yet a member" tuple used for primary-check Inconclusive so the caller retries instead of hard-denying. Added a regression test (CheckMembershipAsync_Inconclusive_WhenPrivateSamlForbiddenAndPublicFallbackRateLimited in tests/Agentweaver.Tests/Projects/GitHubOrgAuthorizationServiceTests.cs) simulating a SAML-403 private check plus a rate-limited (403 + X-RateLimit-Remaining: 0) public fallback, asserting the aggregate result is Inconclusive.

---

## Seeded blueprint-demo run can fail before fix stage with a2a_transport_interrupted

- date: 2026-07-25
- category: bug
- surface: ui
- status: open

On staging build 06007b71, blueprint-demo run 7ba969e8-ad68-41e9-b3de-ebe71d1924d1 against https://github.com/sabbour/agentweaver-demo-dryrun got past outcome confirm and work-plan persistence after the org-membership fix, but never reached the intended CSS-fix/build-test path. Work plan 14 created only two planning subtasks. Child subtask 21 (

---

## Blueprint-demo seeded repo still reproduces Build & Test AgentHost /configure HTTP 500 after assembly restart

- date: 2026-07-25
- category: bug
- surface: all
- status: open

Fresh blueprint-demo run d2ad3035-afef-4e74-93e9-a7c45bfb60ee (work plan 15) on staging drove all three bug-fix subtasks to assemble_ready by 2026-07-25T20:05:02Z, then reached coordinator.children_complete and assembly. RAI passed green at 2026-07-25T20:06:10Z. The first preview applicability pass then skipped preview as not applicable (state=preview_skipped_not_applicable, reason=llm_docs_or_non_runtime) and Build & Test completed/approved at 2026-07-25T20:07:19Z. Immediately afterward the coordinator restarted assembly, emitted a human-review request, flipped preview applicability to preview_required at 2026-07-25T20:07:40Z, bound assembly pod agentweaver-agent-host-zwvh9, and then failed at 2026-07-25T20:07:56.2285446Z with build_test_infra_agenthost_configure_failed: AgentHost /configure for run 'd2ad3035-afef-4e74-93e9-a7c45bfb60ee' failed: HTTP 500. No review/merge/recordable preview surface followed.

---

## Fresh seeded bug-fix project still fails preview heuristics and merge after Build & Test passes

- date: 2026-07-25
- category: bug
- surface: all
- status: open

Fresh project b3608f95-e4f9-4fcf-93bb-5245e1f69ed9 (blueprint-demo-live-2325) cloned the seeded repo correctly (index.html, styles.css, package.json, test.js present). First coordinator run b875731d-7c48-4619-bb3d-21edd71a06b1 reproduced the narrow AgentHost configure race at assembly Build & Test (build_test_infra_agenthost_configure_failed). Immediate retry f9e7867c-48f7-40af-8236-5cd0c9f9e53f progressed farther: subtask 37 stalled twice and was redispatched twice before third child 6f952975-f3b5-4259-b76b-ab172b004a55 reached assemble_ready; subtask 38 then reached assemble_ready; RAI passed green; Build & Test completed successfully; preview was REQUIRED but failed with sandbox.preview_failed / 'Could not determine how to run the app from the worktree (Phase-1 heuristics)' even though the repo is a runnable static site with package.json/build.js. Human review approval then succeeded, but merge still failed on this truly fresh project with assembly_merge_failed: 'the working tree cannot be safely reconciled with the merge result because uncommitted content diverges from the merge result and cannot be safely reconciled; commit, stash, or discard the local changes and retry.' This disproves the earlier assumption that merge failure was only reused-project contamination and introduces a new preview-heuristics blocker on the seeded demo app.

---

## Preview heuristics fail to detect a runnable static site (index.html + package.json + build.js)

- date: 2026-07-25
- category: bug
- surface: all
- status: open

Seen on run `f9e7867c-48f7-40af-8236-5cd0c9f9e53f` (project `blueprint-demo-live-2325`) and again on later blueprint-demo runs: even though preview applicability correctly returns `preview_required` for the seeded static-site repo (`index.html`, `styles.css`, `package.json`, `build.js`, `test.js`), the actual preview launch fails with `sandbox.preview_failed`: "Could not determine how to run the app from the worktree (Phase-1 heuristics)." Not yet root-caused in code (deferred — non-blocking, since review/merge can still proceed without a working preview). Likely candidate: the Phase-1 run-command heuristics don't recognize a plain `build.js`-based static site (no `npm start`/`dev` script, no framework marker file) as a runnable app. Needs investigation if preview becomes required for the recording.

---

## `.squad/` state files tracked by the demo's own Squad-cast step get mutated-uncommitted by the bug-fix subtask, causing assembly_merge_failed

- date: 2026-07-25
- category: bug
- surface: all
- status: open (operational workaround only)

Seen on runs `f9e7867c` and `3a4f3eeb` (blueprint-demo-live projects): after Build & Test passes and human review is approved, merge fails with `assembly_merge_failed`: "the working tree cannot be safely reconciled with the merge result because uncommitted content diverges from the merge result and cannot be safely reconciled." Root cause (confirmed via `kubectl exec` into the live `agentweaver-worker` pod, inspecting `/workspace/{projectId}/`): the demo's own "cast a Squad team" step legitimately commits `.squad/` state files (`decisions.md`, `agents/*/history.md`, `identity/now.md`, or lighter scaffold like `.gitignore`/`.agentweaver/settings.yml`/`.gitattributes`) as TRACKED git content in the target repo. When the later bug-fix subtask's own sandboxed coding agent runs inside that same repo, it discovers these same Squad conventions and — following the "mutable state is written via runtime tools, not git commits" pattern — writes new decision/history entries directly to those already-tracked files WITHOUT committing. This leaves the worktree dirty in a way `WorktreeManager.cs`'s `IsWorkingTreeReconcilable` correctly refuses to Hard-Reset over (by design, to never silently discard content), so the merge-safety check blocks.

This is inherent to the demo's recursive "Squad builds using Squad" premise, not a simple product bug — a real fix needs either (a) auto-committing dirty leftover subtask-worktree content before computing merge safety, or (b) constraining the sandboxed coding agent to always commit its own `.squad/` writes. Both are non-trivial, higher-risk changes to safety-critical merge code, deferred for now. **Operational workaround used successfully**: `kubectl exec` into `agentweaver-worker`, `cd /workspace/{projectId}`, `git add -A && git commit` to clean the dirty tracked state BEFORE the merge step runs. Watch for this proactively (poll `git status --porcelain` on the active project workspace) as soon as the run reaches `assemble_ready`/RAI-passed, ideally before the review gate even opens, to avoid extra round-trips.

---

## PR #513 did not eliminate initial Build & Test AgentHost configure 500 on fresh seeded project

- date: 2026-07-25
- category: bug
- surface: all
- status: open

After staging redeploy to commit 6d7d9aa8 (PR #513 reorder fix for WorkPlans.Status/InReview vs durable review-gate row), fresh project blueprint-demo-live-232803 (95174ba2-affc-4329-b020-eb357da6282c) and fresh coordinator run b552be51-602d-4095-9073-1cb0ca04507e still failed at the FIRST Build & Test / preview-required assembly pass with build_test_infra_agenthost_configure_failed before any human-review gate appeared. Path reached: subtasks 42/43/44 all assemble_ready, RAI green at 2026-07-25T23:44:12Z, build-test gate requested, preview required (llm_unavailable_default_required), then AgentHost /configure failed at 2026-07-25T23:44:30.3814169Z. This means the deployed reorder fix did not eliminate all configure-race/failure cases; the earlier post-review re-arm race may be fixed, but an initial build-test configure failure still reproduces on fresh projects.

---

## Azure KEYVAULT_NAME hardcoded default silently corrupted GitHub OAuth Key Vault references

- date: 2026-07-26
- category: bug
- surface: all
- status: fixed

Azure deploy tooling (scripts/azure/variables.mjs) hardcoded KEYVAULT_NAME's DEFAULTS entry to the generic name 'agentweaver-kv', which was NEVER a real Key Vault in the affected subscription (az keyvault show --name agentweaver-kv -> 'not found'). Any deploy invocation (azure:deploy-from-local, azure:deploy-from-release) where an operator forgot to pass KEYVAULT_NAME silently fell back to this bogus default and rendered the agentweaver-runtime-config ConfigMap (KEYVAULT_NAME, AGENTHOST_KEYVAULT_URI, TOKEN_STORE_KEYVAULT_URI) plus the agentweaver-secrets/agentweaver-user-tokens SecretProviderClass keyvaultName fields against it. Two silent-corruption modes were observed live in one incident: (1) literal bogus default 'agentweaver-kv' -> loud DNS failure ('Name or service not known (agentweaver-kv.vault.azure.net:443)'), users cannot log in; (2) a manually-typed override with transposed letters ('akwvkv' instead of the real 'agwvkv') that happened to be a REAL but wrong, stale vault already present in the subscription -> failed SILENTLY with wrong GitHub OAuth client id/secret instead of erroring at all -- this mode is worse because it looks like a normal login failure, not an infra problem. Fix: KEYVAULT_NAME now has NO default in variables.mjs (resolveVariables() throws MissingRequiredVariableError if unset), and steps/30-deploy.mjs verifies az keyvault show succeeds for the resolved name BEFORE rendering/applying any manifest -- this catches mode (2) as well as mode (1), since a nonexistent OR wrong-but-real vault both fail the existence probe against the caller's actual resource group context. See scripts/azure/params.example.json and scripts/azure/tests/deploy-apply.test.mjs for the corresponding safeguards/tests.

---

## UI harness accepts empty Playwright storageState as valid auth

- date: 2026-07-25
- category: bug
- surface: ui
- status: open

Verified on 2026-07-25 during the staging demo dry-run against https://agentweaver.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io. scripts/ui-harness/.auth/staging.storageState.json existed but contained cookies=[] and origins=[]. scripts/ui-harness/lib/auth.mjs loadStorageState() accepted that file because it only checks Array.isArray, so tools.mjs init/capture proceeded instead of exiting AUTH_EXPIRED. The first capture on '/' then produced only a progressbar DOM snapshot, which is a confusing false start for a non-interactive run that should have stopped immediately for human login. Treat empty storageState as expired (or add an authenticated-session probe before action commands) so harness runs fail fast instead of pretending auth is reusable.
