# MCP tools reference

Use this reference to discover the tools that the Agentweaver Model Context
Protocol (MCP) host exposes from the Gateway. The host reads
`GET /openapi/v1.json` from its configured Gateway and caches the admitted
operations. Each tool name is `agentweaver_` followed by the OpenAPI
`operationId`. The Gateway document defines each tool's path, parameters,
request body, and summary.

For route authorization and native-only operations, see the
[HTTP API reference](./api.md). For provider setup, see
[Configure providers](../guide/configure-providers.md). For host settings, see
[Runtime configuration](./runtime-configuration.md).

## Connect and authenticate

Download the v1 operator definition from the Gateway at
`GET /agents/agentweaver.agent.md`. Save it as
`.github/agents/agentweaver.agent.md` in a repository where you want to use it.
The download is anonymous and returns Markdown; it is not an MCP tool.
It does not write the file into project or run repositories automatically.

Before you connect, configure the absolute HTTPS Broker issuer, protected
resource audience, and Gateway service root listed below. Obtain a valid Broker
access token for the MCP audience. The host maps `/mcp` with the MCP SDK's
stateless Streamable HTTP transport. It validates the token and forwards the
same token to the configured Gateway; the MCP host does not issue another
identity or grant.

| Host configuration | Requirement |
| --- | --- |
| `Identity:Issuer` | Absolute HTTPS Broker issuer. |
| `Identity:Audience` | Absolute HTTPS protected-resource audience. |
| `Gateway:BaseAddress` | Absolute HTTPS Gateway service root, without a path. |

To discover and call tools:

1. Send an authenticated MCP initialize request to `/mcp`.
2. Call `tools/list` to retrieve the exact schemas from the configured Gateway.
3. Call a listed tool with `tools/call` and arguments that match its schema.

The host derives the protected-resource metadata path from the audience path.
For an audience ending in `/mcp`, discovery uses
`/.well-known/oauth-protected-resource/mcp`. The metadata returns `resource` and
`authorization_servers`. If `/mcp` returns `401`, use the metadata URI in the
`WWW-Authenticate` challenge to identify the protected resource.

`GET /health/live` and `GET /health/ready` are anonymous process checks. They do
not validate a Gateway tool call.

## Arguments

| Input | Mapping |
| --- | --- |
| Path parameter, such as `projectId` | Required parameter with the same name; each value is URI-escaped. |
| Query parameter, such as `cursor` | Advertised scalar property with the same name. |
| `tenantSelector` | `X-Agentweaver-Tenant`; required for Source Control tools, optional where advertised elsewhere. |
| `idempotencyKey` | `Idempotency-Key`; required for Knowledge writes. |
| `body` | JSON body defined by the operation's request schema. |

The input schema rejects unknown properties. Body fields are not flattened into
top-level tool arguments. Operations whose idempotency key belongs inside the
owner request body retain that field inside `body`.

Use `tools/list` to get the exact schema from the configured Gateway. The
operation list on this page is an index; the live schema is authoritative for
the configured Gateway.

## Tool index

Every name below has the `agentweaver_` prefix. For example, `listProjects` is
`agentweaver_listProjects`. These are the operation IDs in the checked-in
`GatewayRouteCatalog`.

### Caller attention

| Operation ID | Purpose |
| --- | --- |
| `listUserNotifications` | Read pending attention across authorized projects and runs; supports cursor, page size, project filter and dismissed-item inclusion. |
| `dismissUserNotification` | Dismiss a notification for the caller without resolving its gate. |

These operations come from the same Gateway catalog as the existing tools.
There is no separate Chief of Staff notification store or bell. See
[Notifications](../guide/notifications.md).

### Projects and configuration

| Operation ID | Purpose |
| --- | --- |
| `listProjects` | List visible projects. |
| `createProject` | Create a project; does not assign an Owner. |
| `getProject` | Read project metadata. |
| `updateProject` | Update metadata at an expected revision. |
| `listProjectRoleAssignments` | Read project role assignments. |
| `assignProjectRole` | Assign a project role through owner authorization. |
| `revokeProjectRoleAssignment` | Revoke a project role assignment. |
| `getProjectConfiguration` | Read current or retained configuration. |
| `getProjectConfigurationChoices` | Read available project model and provider choices. |
| `getHostedModelReasoningCatalog` | Read live hosted reasoning levels and availability. |
| `updateProjectConfiguration` | Append a configuration revision. |
| `getAuthorizationContext` | Read current tenant and effective authority. |
| `getPlatformRuntimeDefaults` | Read platform defaults. |
| `updatePlatformRuntimeDefaults` | Append platform defaults. |
| `getPlatformComponents` | Read active platform components and their readiness. |
| `acceptRunSelection` | Accept an immutable run selection. |
| `getRunSelection` | Read the accepted selection. |
| `readIssueSync` | Read periodic issue sync status. |
| `configureIssueSync` | Enable, disable, and bound periodic issue sync. |
| `createIssueSyncGrant` | Create project issue-sync authority. |
| `readIssueSyncGrant` | Validate current issue-sync authority. |
| `revokeIssueSyncGrant` | Revoke project issue-sync authority. |
| `listRemoteMcpConnections` | List Remote MCP connections with configuration checksums. |

### Casting

Casting tools forward the bearer and optional tenant selector to Projects.
Revision checks stay inside `body`; no idempotency header is required.

| Operation ID | Purpose |
| --- | --- |
| `createProjectCastingProposal` | Stage a complete configuration draft. |
| `createScenarioProjectCastingProposal` | Stage a built-in scenario. |
| `createManualProjectCastingProposal` | Stage selected catalog roles. |
| `listProjectCastingProposals` | List proposals with optional `limit`. |
| `getProjectCastingProposal` | Read a proposal. |
| `updateProjectCastingProposal` | Replace a pending draft at its expected revision. |
| `confirmProjectCastingProposal` | Confirm reviewed casting at current revisions. |
| `rejectProjectCastingProposal` | Reject without changing configuration. |
| `exportProjectCasting` | Export charters and casting; requires write authority. |
| `importProjectCasting` | Stage a digest-checked transfer; request body limit 1 MiB. |
| `listProjectCastingTemplates` | Read built-in templates. |
| `listProjectCastingRoles` | Read built-in roles. |

### Project backlog

| Operation ID | Purpose |
| --- | --- |
| `readProjectBacklog` | Read project backlog tasks and dependencies. |
| `captureBacklogTask` | Capture project backlog work. |
| `editBacklogTaskContent` | Replace backlog task content and lifecycle. |
| `setProjectBacklogTaskState` | Queue or return an unclaimed task. |
| `archiveProjectBacklogTask` | Archive an unclaimed task. |
| `addProjectBacklogDependency` | Add a prerequisite to a task. |
| `removeProjectBacklogDependency` | Remove a task prerequisite. |
| `claimBacklogTask` | Atomically claim eligible work for an accepted run with an idempotency key. |

### Skills and marketplace

| Operation ID | Purpose |
| --- | --- |
| `listMarketplaceDefinitions` | List suggested marketplaces for a project. |
| `listMarketplaceSources` | List project marketplace sources. |
| `createMarketplaceSource` | Add a public source. |
| `updateMarketplaceSource` | Append a source revision. |
| `removeMarketplaceSource` | Tombstone a source at an expected revision. |
| `browseMarketplaceSource` | Browse a selected source revision. |
| `previewMarketplaceSkill` | Validate selected content at the pinned commit. |
| `importMarketplaceSkill` | Import the selected, previewed content. |
| `previewSkillContent` | Validate submitted local content without importing. |
| `packageSkillContent` | Turn uploaded files, a ZIP archive, or draft fields into skill candidates. |
| `importProjectSkill` | Import validated local content. |
| `updateProjectSkillAssignment` | Update project assignments at an expected configuration revision. |
| `listProjectSkillInventory` | Read paginated revision metadata. |
| `revokeProjectSkillRevision` | Revoke one imported skill revision with a reason. |

### AgentSuite imports

| Operation ID | Purpose |
| --- | --- |
| `importProjectAgentSuite` | Validate an AgentSuite archive and stage a casting proposal; request body limit 24 MiB. |
| `getProjectAgentSuiteImport` | Read a retained AgentSuite import record by source identity. |
| `getProjectAgentSuiteAgentPolicy` | Read the policy bound to an imported agent. The policy is not enforced. |

Import doesn't activate or run agents. Confirm or reject the staged proposal with the casting tools.
See [AgentSuite import](./agentsuite-import.md).

### Coordination

| Operation ID | Purpose |
| --- | --- |
| `acceptRunRoot` | Accept root owner state. |
| `createProjectRun` | Create an interactive run. |
| `startProjectRun` | Deliver the first run message. |
| `cancelProjectRun` | Cancel a run. |
| `listProjectRuns` | List accepted project runs, newest first. |
| `listProjectIssues` | List synced repository issues stored in the project backlog. |
| `listProjectPullRequests` | List pull requests that project runs recorded after migration 032 was applied. |
| `readProjectMetrics` | Read bounded project run counts and daily UTC activity from Orchestrator. |
| `listChiefOfStaffConversations` | List my private Chief of Staff conversations. |
| `bindChiefOfStaffConversation` | Bind a run I created to a Chief of Staff conversation. |
| `getChiefOfStaffConversation` | Read one of my Chief of Staff conversations. |
| `listChiefOfStaffPriorities` | List my ordered Priorities with resolved links. |
| `createChiefOfStaffPriority` | Create a Priority. Requires `Idempotency-Key`. |
| `reorderChiefOfStaffPriorities` | Reorder my Priorities at an expected order revision. |
| `getChiefOfStaffPriority` | Read one Priority. |
| `updateChiefOfStaffPriority` | Change a Priority at an expected revision. Never changes run state. |
| `addChiefOfStaffPriorityLink` | Link a Priority to a run, conversation, issue, pull request, or artifact. Issue and pull request bodies include `providerId`, `repository: { owner, name }`, and `number`; before the database update they return `database_update_pending`. |
| `removeChiefOfStaffPriorityLink` | Remove a Priority link at an expected revision. |
| `proposeOutcome` | Propose an outcome decision. |
| `readRunStatus` | Read current run owner state. |
| `recoverRun` | Request owner recovery. |
| `proposeOutcomeSpec` | Submit an outcome specification. |
| `selectWorkflow` | Select a workflow. |
| `proposeWorkPlan` | Submit a work plan. |
| `reviseWorkPlan` | Submit a revised plan. |
| `requestAssembly` | Request assembly. |
| `readDecisions` | Read decision state and pending gate. |
| `readSessionTree` | Read the session tree. |
| `readSessionStatus` | Read lifecycle, activity, and blockers. |
| `readRuntimeObservationV1` | Read current runtime evidence. |
| `reportTurnFailure` | Report a failed turn. |
| `answerGate` | Answer an exact question gate. |
| `askQuestion` | Request a question gate. |
| `askOutcomeQuestion` | Request the next outcome question. |
| `requestApproval` | Request an approval gate. |
| `acknowledgeGate` | Acknowledge a gate. |
| `registerChild` | Register a child session. |
| `spawnSession` | Request a child spawn; acceptance is not execution. |
| `forkSession` | Fork a session through owner admission. |
| `detachSession` | Detach a child. |
| `archiveChild` | Archive an eligible child. |
| `subscribeToIdle` | Request an idle subscription. |
| `steerSession` | Submit steering. |
| `approveGate` | Approve an exact pending gate. |
| `rejectGate` | Reject an exact pending gate. |
| `sendMessage` | Submit an addressed message; acceptance is not delivery. |
| `advanceTurnBoundary` | Advance an owner turn boundary. |
| `completeTurn` | Record turn completion. |
| `readNotifications` | Read owner notifications. |
| `acknowledgeNotification` | Acknowledge a notification. |
| `acknowledgeMessage` | Acknowledge a message. |
| `readPolicyEvaluation` | Read a policy receipt. |
| `validatePolicyEvaluationAdmission` | Revalidate receipt admission. |

`readProjectMetrics` accepts optional `from`, `to`, and `bucket=day`.
`readProjectUsage` accepts optional `from` and `to`, and reads Events &
Sessions ledger totals grouped by model and agent. Both tools use fresh
project read authority and a half-open UTC interval of at most 92 days,
defaulting to the last 30 days. A run-bound caller sees only its own run.
Cost units and dispositions remain separate, unknown values remain `null`,
and usage reads do not price or change budgets. Cross-project overview is out
of scope. See [Project metrics and usage](./api.md#project-metrics-and-usage)
for metric definitions and differences from the 0.x dashboard.

### Source Control

| Operation ID | Purpose |
| --- | --- |
| `pinSourceControlRepository` | Pin a run repository; `body` is optional for legacy secret mode. |
| `createSourceControlIssue` | Create a repository issue. |
| `createSourceControlPullRequest` | Create or reuse a pull request. |
| `readSourceControlReviews` | Read pull request reviews. |
| `prepareSourceControlWorkspace` | Prepare a pinned repository workspace. |
| `readSourceControlWorkspaceDiff` | Read a workspace diff. |
| `prepareSourceControlMergeIntent` | Request merge approval. |
| `readSourceControlMergeIntent` | Read a merge intent. |
| `executeSourceControlMergeIntent` | Execute an approved intent. |
| `captureSourceControlWorkspaceOutput` | Capture the produced output of a workspace. |
| `listSourceControlOutputCaptures` | List admitted output captures, newest first. |
| `readSourceControlOutputCapture` | Read a capture and its file list. |
| `readSourceControlOutputCaptureDiff` | Read the diff of a capture. |
| `readSourceControlOutputCaptureFile` | Read one captured file. The tool returns the body as text. |

### Knowledge

| Operation ID | Purpose |
| --- | --- |
| `createKnowledgeRecord` | Create a scoped record. |
| `searchKnowledge` | Search and page records. |
| `readKnowledgeRecord` | Read one record. |
| `updateKnowledgeRecord` | Apply a revision-checked update. |
| `restoreKnowledgeRecord` | Append a restored revision. |
| `approveKnowledgeDecision` | Approve a Decision. |
| `exportKnowledgeRecords` | Export an exact-scope bundle. |
| `importKnowledgeRecords` | Import a validated bundle. |
| `readKnowledgeRevisions` | Read revision history. |
| `promoteKnowledgeProposal` | Promote a Proposal through owner validation. |
| `rejectKnowledgeProposal` | Reject a Proposal. |
| `compileKnowledgeContext` | Compile bounded agent context. |

### Events and usage

| Operation ID | Purpose |
| --- | --- |
| `replayRunEvents` | Read a finite committed journal page. |
| `readSessionTurnContent` | Read recorded turn text with current run-bound project authority. Excludes cache, configuration, and tool-result material. |
| `readRunUsage` | Read run usage totals. |
| `readProjectUsage` | Read persisted project usage grouped by model and agent. |

## Results and errors

Successful dispatch returns text plus structured content:

```json
{
  "status": 202,
  "ownerResponse": {}
}
```

`status` preserves the HTTP status. `ownerResponse` contains the parsed owner
JSON, a string for non-JSON text, or null for an empty response. `IsError` is true
for HTTP errors or an owner response with `accepted: false`.

An accepted-only operation or HTTP `202` is described as acceptance, not
completion. Read current owner state to establish the result.

Local error codes include `unknown_tool`, `invalid_arguments`,
`gateway_contract_invalid`, `gateway_timeout`, `gateway_unavailable`,
`unauthenticated`, and `gateway_response_too_large`.

## Limits and exclusions

- The catalog supports `GET`, `POST`, `PUT`, `PATCH`, and `DELETE` under
  `/api/v1/`.
- `streamRunEvents` is excluded because it is SSE. Use finite replay tools.
- Browser connection lifecycle routes and callbacks are outside the catalog.
- Native-only run backlog, runtime, Environment, and private material routes
  are not tools.
- Catalog downloads are limited to 2 MiB; owner responses are limited to 8 MiB.
  Gateway request deadlines are ten seconds.
- The host does not add background scheduling or automatic execution.

## Source

- [MCP host](https://github.com/sabbour/agentweaver/tree/v1/services/mcp/Agentweaver.Mcp)
- [Gateway route catalog](https://github.com/sabbour/agentweaver/blob/v1/services/gateway/Agentweaver.Gateway/GatewayRouteCatalog.cs)
- [Gateway guide](../guide/gateway.md)
