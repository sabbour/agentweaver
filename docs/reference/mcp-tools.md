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

Every name below has the `agentweaver_` prefix. For example, the first entry is
`agentweaver_listProjects`. These are the operation IDs in the checked-in
`GatewayRouteCatalog`.

### Projects and configuration

| Operation ID | Purpose |
| --- | --- |
| `listProjects` | List visible projects. |
| `createProject` | Create a project; does not assign an Owner. |
| `getProject` | Read project metadata. |
| `updateProject` | Update metadata at an expected revision. |
| `getProjectConfiguration` | Read current or retained configuration. |
| `updateProjectConfiguration` | Append a configuration revision. |
| `getAuthorizationContext` | Read current tenant and effective authority. |
| `getPlatformRuntimeDefaults` | Read platform defaults. |
| `updatePlatformRuntimeDefaults` | Append platform defaults. |
| `acceptRunSelection` | Accept an immutable run selection. |
| `getRunSelection` | Read the accepted selection. |

### Skills and marketplace

| Operation ID | Purpose |
| --- | --- |
| `listMarketplaceSources` | List project marketplace sources. |
| `createMarketplaceSource` | Add a public source. |
| `updateMarketplaceSource` | Append a source revision. |
| `removeMarketplaceSource` | Tombstone a source at an expected revision. |
| `browseMarketplaceSource` | Browse a selected source revision. |
| `previewMarketplaceSkill` | Validate selected content at the pinned commit. |
| `importMarketplaceSkill` | Import the selected, previewed content. |
| `previewSkillContent` | Validate submitted local content without importing. |
| `importProjectSkill` | Import validated local content. |
| `updateProjectSkillAssignment` | Update project assignments at an expected configuration revision. |
| `listProjectSkillInventory` | Read paginated revision metadata. |

### AgentSuite imports

| Operation ID | Purpose |
| --- | --- |
| `getProjectAgentSuiteImport` | Read a retained AgentSuite import record by source identity. |
| `getProjectAgentSuiteAgentPolicy` | Read the policy bound to an imported agent. The policy is not enforced. |

Importing a suite stages a casting proposal, so it is a native Projects route and not an MCP tool.
See [AgentSuite import](./agentsuite-import.md).

### Coordination

| Operation ID | Purpose |
| --- | --- |
| `acceptRunRoot` | Accept root owner state. |
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
| `readRunUsage` | Read run usage totals. |

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
- Native-only casting, backlog, runtime, Environment, and private material routes
  are not tools.
- Catalog downloads are limited to 2 MiB; owner responses are limited to 8 MiB.
  Gateway request deadlines are ten seconds.
- The host does not add background scheduling or automatic execution.

## Source

- [MCP host](https://github.com/sabbour/agentweaver/tree/v1/services/mcp/Agentweaver.Mcp)
- [Gateway route catalog](https://github.com/sabbour/agentweaver/blob/v1/services/gateway/Agentweaver.Gateway/GatewayRouteCatalog.cs)
- [Gateway guide](../guide/gateway.md)
