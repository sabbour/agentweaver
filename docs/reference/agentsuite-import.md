# AgentSuite import

> Implements [#2114](https://github.com/sabbour/agentweaver/issues/2114). Pinned to [agent-primitives/suite](https://github.com/agent-primitives/suite) commit `a09950a75359cdc7de4d3361694582613f24c732` (`1.0.0-draft`).

`POST /api/projects/{projectId}/agentsuite-imports` validates a native AgentSuite archive offline
and stages its agents as one pending casting proposal. Import is inert: it doesn't activate,
build, or run anything, contact a tool endpoint, fetch a schema, or choose a model. The archive
is the only source for built-in and uploaded team blueprints.

```json
{
  "expectedConfigurationRevision": 4,
  "format": "oci-layout-tar",
  "archive": "<base64>",
  "toolConnections": [
    { "toolProvider": "github@1.0.0", "connectionId": "6f1c2d4e-8a9b-4c3d-9e2f-1a2b3c4d5e6f" }
  ],
  "blueprint": { "sourceBlueprintId": "web-team", "workflows": ["default"], "defaultWorkflow": "default" }
}
```

- `format` is `oci-layout-tar` (a tar of an OCI image layout) or `content-tar-gzip` (the raw
  content layer). The decoded archive is limited to 16 MiB, and the request body to 24 MiB.
- `toolConnections` and `blueprint` are optional. The response is `201 Created` with the import
  record, the proposal, and `unresolvedSkillBindings`, or `200 OK` with a null proposal when every
  agent already exists unchanged and the blueprint changes nothing.

## Validation

The .NET validator in `Agentweaver.Projects.Config/AgentSuite` follows the pinned Go validator.
It uses the JSON schemas embedded from the pinned commit and never fetches a schema. The
committed fixtures in `tests/Agentweaver.Projects.Config.Tests/Fixtures/AgentSuite` carry the Go
verdicts in `parity.json`. The .NET tests require the same verdicts and error lines.

After native validation passes, Agentweaver refuses two more cases:

- A model `endpointEnv` or `secretRefs` (`agentsuite_secret_reference`). Packages never carry
  secrets or secret slots.
- An agent ID that is a reserved orchestration role, such as `coordinator`
  (`agentsuite_reserved_role`). The platform coordinator is the team entry point.

A rejected archive returns `422` with `code` and every error line in `errors`.

## Identity and retention

- The source identity is the OCI manifest digest, or the content layer digest for a bare layer.
  Retagging a layout keeps the identity; repacking changes it.
- Each agent gets the effective ID `agentsuite:{suite}:{agent}:{12hex}`, where `12hex` is the
  start of the source identity. The native ID and digests stay in the record, apart from the
  effective ID.
- Original blobs are stored once by digest under `agentsuite/blobs/sha256/`. The import record is
  stored under `agentsuite/projects/{projectId}/imports/`. Both are create-only and never
  rewritten. There is no database migration.
- Each agent becomes a charter with the literal instruction text and a cast entry whose role is
  the native agent ID. The charter name is the agent description when it fits, else the native ID.

## Review and concurrency

- The proposal goes through the normal casting review. Confirm or reject it with the existing
  casting proposal routes.
- `expectedConfigurationRevision` must match the current revision, or the import returns `409`.
- The proposal ID derives from the project, the record, and the expected revision. A repeated or
  restarted request returns the same proposal, including a rejected one.
- An effective ID that already exists with different content returns `409
  agentsuite_identity_conflict`. Importing the same source with different tool connections or
  blueprint data returns `409 agentsuite_import_conflict`. Existing data is never replaced.
- A suite that is incompatible with the project configuration returns `422
  incompatible_agentsuite_import`. A missing object store returns `503`.

## Supported and unsupported capabilities

Every agent has `runnable: false`. `supported: true` means the agent has no blocker except those
that wait on another owner: `runtime_unavailable` and `policy_not_enforced`.

| Blocker | Cause |
| --- | --- |
| `runtime_unavailable` | Agentweaver has no AgentSuite runtime yet. |
| `capability_model_unsupported` | The agent declares model capabilities, not an exact model. No model is substituted. |
| `tool_connection_unbound` | A remote MCP tool provider has no Environment connection. |
| `tool_connection_header_secrets_unsupported` | A remote MCP tool provider uses header secrets. |
| `bundled_tool_provider_unsupported` | A tool provider has only bundled variants. |
| `policy_not_enforced` | The agent has a policy. No owner enforces it yet. |
| `policy_version_unsupported` | The agent policy is v1. Only v2 is read. |
| `extension_unsupported` | The agent declares an extension. |
| `runtime_profile_unsupported` | A composition uses a runtime profile. |
| `platform_unsupported` | The agent has no `linux/amd64` composition. |
| `execution_contract_missing`, `source_epoch_missing` | The build profile is incomplete. |
| `image_not_digest_pinned`, `example_image` | A build image isn't pinned, or is a documentation example. |

Invokes, compositions, runtime profiles, evaluations, and platforms are imported as review data.
Invokes grant nothing.

## Tool providers and connections

A remote tool provider (Streamable HTTP) is recorded with its tools, effects, approvals,
delegated authorization, header names, and timeouts. `endpointRef`, `destinationRefs`, and
`bindingRef` are unresolved slots that are kept for review only. The Environment Remote MCP
connection replaces them.

Each `toolConnections` entry binds one remote provider `id@version` to a project Remote MCP
connection ID: a lowercase GUID that isn't all zeros. Import checks only the shape. The
Environment owner checks the connection, catalog, and tools when it creates a reviewed snapshot.

A provider has `support: remote-mcp` when a connection can bind it. It has `support:
unsupported` when it has only bundled variants or uses header secrets. In the pinned spec, an
agent composition can reference only providers with a bundled variant, so a remote-only provider
stays a catalog declaration.

## Policies

An agent policy is kept as original bytes and bound to its agent by digest. Import reads only
policy v2: `network.egressAllow` and `requiredCapabilities`. Import doesn't enforce a policy, and
every agent with a policy keeps `policy_not_enforced` until an owner that enforces
`network/egress-allow/v1` accepts the binding. Read the bound policy with
`GET /api/projects/{projectId}/agentsuite-agents/{effectiveAgentId}/policy`. It returns a null
policy when none is bound.

## Blueprint data

`blueprint` carries team data that isn't in the suite: `displayName`, `description`,
`workflows`, `defaultWorkflow`, `reviewPolicy`, `sandboxProfile` (`default` or `restricted`),
`skillBindings`, and `sourceBlueprintId`. When it's omitted, the import uses the `default`
workflow, review policy, and sandbox profile, no skills, and the suite name.

The record keeps all of it. When the request includes `blueprint`, import applies it to the
proposal through the same path as an inline blueprint:

1. The projection must pass the 0.x blueprint rules. Each role must be a suite agent, and each
   workflow must be available. Otherwise the import returns `400 invalid_blueprint` and stores
   nothing. Only the `default` workflow is available today.
2. Each workflow becomes a workflow reference under `sourceBlueprintId`, and `defaultWorkflow`
   becomes the project default workflow.
3. The `restricted` sandbox profile removes all project egress. `default` keeps it. A
   `restricted` profile needs platform runtime defaults. If they aren't configured, the import
   returns `409 platform_runtime_unconfigured`.
4. Each bound role's effective agent ID is added to a skill that the project already installed
   and pinned. Import never installs a skill or changes whether it's enabled.
   `unresolvedSkillBindings` lists each binding that doesn't take effect, with a code:
   `skill_not_installed`, `skill_not_pinned`, or `skill_disabled`.

`reviewPolicy`, `displayName`, and `description` are kept on the record for display only. When
`blueprint` is omitted, project configuration changes only for the new charters and casting.

## Regenerate the AgentSuite fixtures

```bash
cd tools/agentsuite
go run ./cmd/agentsuite-fixture -out ../../tests/Agentweaver.Projects.Config.Tests/Fixtures/AgentSuite
```

The command writes each layout and the Go verdicts to `parity.json`. Commit them together.
`tools/agentsuite` is its own Go module and depends only on the pinned suite module.

## Web client

The web client methods `importProjectAgentSuite`, `getProjectAgentSuiteImport`, and
`getProjectAgentSuiteAgentPolicy` in `apps/web/src/v1/api.ts` call the same routes.
