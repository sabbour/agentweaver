# Installed agent applications

> Part of [ADR 0001](../decisions/0001-platform-architecture.md). **Status:** Proposed. This is a post-core extension, not an implemented installer or runtime.

## Scope

Agentweaver will host installed agent applications as well as individually configured agents and workflows.
An application bundle can contain agents, tools, skills, restrictive policies, canvases, workflows, and required assets.
Existing owners resolve, verify, configure, activate, upgrade, and uninstall it.
There is no new installation service, model harness, or registry protocol.
The [proposed Canvas provider contract](applications-and-surfaces.md#canvas-provider-contract-and-adapters)
uses the separately planned Canvas seam beside Application Hosting.
A2UI and bounded GitHub declaration/action compatibility are its adapter targets.
MCP Apps remains a protocol integration, not a third provider seam.
The plan has sixteen seams; the current source catalog implements fifteen and no Canvas contract.

The initial distribution profile uses one OCI image manifest, a typed configuration blob, and one inventoried tar+gzip content layer.
It excludes multi-platform variants and package install hooks.
Packaged tools and scripts run only through existing host enforcement.
Unsupported required content blocks activation rather than creating a new extension mechanism.

The [post-core plan](../decisions/0001-platform-architecture.md#installed-agent-applications---planned-post-core-extension) contains the delivery phases.
The existing nineteen-item P1 scope, acceptance criteria, counts, and completion gates remain unchanged.
This extension does not add scope to [#1848](https://github.com/sabbour/agentweaver/issues/1848) or [#1851](https://github.com/sabbour/agentweaver/issues/1851).
It is not an additional P1 completion or cutover prerequisite.

## Product model and host boundary

| Term | Identity and lifetime |
| --- | --- |
| Portable application bundle | Publisher-qualified app ID, SemVer, exact OCI manifest digest, typed manifest, and inventoried content. It contains no tenant assignment, selected provider, credential value, or self-granted permission. |
| Installed application instance | A project-owned `installationId`, revision head, admission state, retained revisions, and lifecycle operations. Several instances can use the same bundle with different configuration. |
| Installed revision | Immutable bundle/dependency digests, configuration binding, selected-provider references, content references, and compatibility evidence |
| Activated revision | The installed revision selected by the instance's compare-and-swap active pointer. Active means eligible for an authorized run request, not a provisioned environment or running server. |
| Run/execution instance | A real Orchestrator run bound to one installed revision and entry point. It retains exact content/configuration references and genuine resource pins. |
| Provider resource | A real consumer-owned resource with identity, positive generation, and negotiated capabilities after provisioning |
| Generated application deployment | An output produced by a run and served at `live`, `preview`, or `published`. Its deployment lifecycle remains separate from installation. |

Docker assembles and distributes container images.
OCI Image and Distribution define interoperable content and transport.
The OCI Runtime specification defines a container filesystem bundle and lifecycle contract.
[`runc`](#sources) consumes that runtime bundle and runs Linux containers.
It does not install agent applications.

Agentweaver uses the same separation of concerns.
OCI carries the portable application.
An Agentweaver install/host contract binds it to existing owners.
Orchestrator, AgentHost, Environment, Identity, Policy, and the surface host enforce execution.
This contract is not a new VM runtime, container engine, or replacement for Kubernetes isolation.

An installed application can produce a generated application.
The producing run links the two records for audit.
An installed agent application's activation is not publication of its generated output.
The existing [Applications and surfaces](applications-and-surfaces.md) contract still governs output deployment and viewer access.

### Provider architecture of the application host

The bundle host composes existing provider contracts rather than becoming an all-purpose application provider.
The package describes required content, profiles, and capabilities.
Trusted host composition selects enabled adapters and keeps core authority outside them.

| Host duty | Contract and owner | Default implementation and remaining work |
| --- | --- | --- |
| Resolve and verify the portable package | OCI Image/Distribution reader and host-owned trust configuration | OCI digest transport, approved registry credentials, strict verification. No new registry/package-provider seam or install hook. |
| Retain verified content | Existing Object Store contract and ownership-safe callers | Azure Blob foundation. Bundle inventories, retention, and exact installation receipts remain B1/B2 work. |
| Execute agents and workflows | Orchestrator/AgentHost with existing Sandbox, Storage, Network Policy, Policy, Secrets, and model-selection contracts | Cloud-only Copilot SDK, AKS, Azure workspace/network defaults. B3 wires real authorized runs, not an alternate model harness. |
| Expose a bundled Canvas | Proposed `ICanvasProvider` contract under the planned Canvas seam | A2UI and bounded GitHub-compatible adapter targets, with retained native views and MCP Apps protocol integration. Current source has no product Canvas adapter. |
| Serve a generated application | Existing Application Hosting boundary; Environment owns deployments | Built-in AKS web hosting only. Canvas renders declarative content separately or embeds an authorized endpoint; it cannot approve or perform publication. |
| Record progress, actions, and recovery | Existing session, journal, owner outbox, and core action contracts | Current foundations plus B2-B4/C1-C5 owner integration. No new event bus, action authority, or surface service. |

Each adapter preserves exact versions, typed options, explicit unsupported results, and current core enforcement.
Installed revisions retain compatibility and selected-adapter references without provisioning resources.
Real runs and deployments obtain genuine resource bindings through their own owners.
Canvas instances retain a separate surface-scoped binding.
None uses an installation ID as a run ID or claims provider capabilities from package content.

## Existing foundation and compatible reuse

The preserved specification originated at `892ffde6f93243c33636b28c3c637289a50194db`.
Provider placement and Canvas evidence follow the admitted
[#1909](https://github.com/sabbour/agentweaver/pull/1909) and
[#1918](https://github.com/sabbour/agentweaver/pull/1918) decisions.
This reconciliation uses source commit
`e6d8d392cb3634da6243b06b1397a25973ac3615`, including the optional Cosmos Memory
adapter in [#1931](https://github.com/sabbour/agentweaver/pull/1931).
Knowledge still owns Memory operations, accepted-effect receipts, and selected-backend
delivery intent. Events owns the authoritative journal; Object Store owns referenced bytes.
These source boundaries do not establish a deployed bundle or Canvas runtime.
Existing libraries and service candidates provide foundations, not evidence of a deployed bundle runtime.
All installation records, routes, component loaders, and lifecycle behavior in this document are proposed.

| Existing contract | Reuse and limit |
| --- | --- |
| [Project configuration](../../../services/projects-config/Agentweaver.Projects.Config/ProjectConfiguration.cs) | Agent charters, casting, workflow references, skill settings, model references, run limits, and provider requirements. The service does not materialize executable workflows. |
| [Projects authorization](../projects-config.md#api-surface) | Current memberships, roles, and effective permissions. The authenticated caller supplies identity. Saved actor IDs and bundle claims do not create authority. |
| [Provider contracts](../../../packages/Agentweaver.Abstractions/ProviderContracts.cs) and [resolver](../../../packages/Agentweaver.Providers/ProviderResolver.cs) | Existing cardinalities, exact adapter/options-schema checks, candidates, negotiation, and resource pins |
| [Workflow contracts](../../../services/orchestrator/Agentweaver.Orchestrator.Core/WorkflowContracts.cs) | Step catalogs, bounded work, immutable snapshots, first-use confirmation, and platform-owned gates. These validators are not a durable installer or AgentHost scheduler. |
| [Identity and Policy](../identity-secrets.md) | Exact `SecretRef` references and current grants. Run-bound redemption cannot use an installation ID. Successful protected effects still require the completed grant-owner and trusted-writer path. |
| [Object Store](../../../packages/Agentweaver.Abstractions/ObjectStore.cs) | Create-only writes, streamed reads, and exact-key deletes. It is not a workspace, registry, extractor, permission store, or reference-counted garbage collector. |
| [Services and release](services-and-release.md) | Existing coarse owners, owned schemas, fenced commands, outboxes, AgentHost, gateway, and surface boundaries |

### 0.x prior art and incompatibilities

The original references below use 0.x commit `d984e8bba4e6932eef4de69e93bc5be94868b16b`.
The released `v0.34.2` baseline is
[`013ba5e12915b6a729763e04221c297438b1cd11`](https://github.com/sabbour/agentweaver/tree/013ba5e12915b6a729763e04221c297438b1cd11).
Its package validator, contract tests, owner library, and persistence tests retain
the compatible strict validation, exact-byte identity, scope, and immutable-conflict behavior.
They define behavior to retain, not automatic package or runtime compatibility:

| Prior art | Compatible behavior | Concrete incompatibility |
| --- | --- | --- |
| [Blueprint package models](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/packages/Agentweaver.Squad/BlueprintPackages/BlueprintPackageModels.cs), [validator](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/packages/Agentweaver.Squad/BlueprintPackages/BlueprintPackageValidator.cs), and [contract tests](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/tests/Agentweaver.Tests/Blueprints/BlueprintPackageContractTests.cs) | Raw-byte digests, strict JSON, inventory coverage, bounds, schema parity, and immutable snapshots | The definitions-only schema accepts Blueprint, Role, Workflow, and Skill. It does not accept the full application kinds or define OCI installation. |
| [GitHub package import](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/apps/Agentweaver.Api/Blueprints/GitHubBlueprintPackageImportService.cs) and [tests](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/tests/Agentweaver.Tests/Blueprints/GitHubBlueprintPackageImportServiceTests.cs) | Resolve a ref once, read immutable objects, reject size mismatch, symlinks, submodules, LFS pointers, truncated trees, and case collisions | Git commit/tree identities and GitHub authentication are not OCI descriptors or a portable registry protocol. |
| [Owner package library](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/packages/Agentweaver.Domain/BlueprintPackages/OwnerBlueprintPackageLibrary.cs) and [tests](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/tests/Agentweaver.Tests/Blueprints/SqliteOwnerBlueprintPackageLibraryTests.cs) | Exact-byte retention, idempotent identical material, immutable-version conflicts, server-owned scope, and credential-free provenance | Private library deletion is not safe uninstall with active runs, dependencies, and retained audit. |
| [Catalog reader](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/packages/Agentweaver.Squad/Catalog/CatalogReader.cs) and [embedded resources](https://github.com/sabbour/agentweaver/tree/d984e8bba4e6932eef4de69e93bc5be94868b16b/packages/Agentweaver.Squad/Catalog/Resources) | Stable agent, role, charter, workflow, and skill identities. Reserved roles remain platform-owned. | An assembly catalog is not an installed-app catalog. A bundle cannot mint roles or replace Coordinator/Scribe authority. |
| [Executable workflow snapshots](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/apps/Agentweaver.Api/Workflows/ExecutableWorkflowSnapshots.cs) and [workflow-binding tests](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/tests/Agentweaver.Tests/Graph/RunWorkflowDefinitionBindingTests.cs) | Exact definition identity, version, bytes, digest, and explicit reload errors | Legacy YAML compatibility does not establish v1 step catalogs or platform gate authority. |
| [Skill paths](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/apps/Agentweaver.Api/Skills/SkillPaths.cs), [security tests](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/tests/Agentweaver.Tests/Skills/SkillSecurityTests.cs), and [pod-delivery test](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/tests/Agentweaver.Tests/Skills/SkillPodPerRunDeliveryTests.cs) | Traversal checks, scoped materialization, assignment behavior, and execution-location coverage | The pod-delivery test exposes an API-local pointer that the executing pod cannot read. Strict extraction and actual host delivery remain necessary. |
| [Sandbox tools](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/packages/Agentweaver.AgentTools/SandboxToolRegistry.cs) | Tool availability depends on host capability and permission. | The direct host-shell path does not carry into cloud-only v1. |
| [Sandbox previews](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs), [cluster tests](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/tests/Agentweaver.Tests/SandboxPreviewServiceClusterTests.cs), and [credential-scrub tests](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/tests/Agentweaver.Tests/Preview/PreviewRunnerAuthAndScrubTests.cs) | Durable route ownership, fences, recovery, and credential scrubbing | A live sandbox preview is generated output, not an installed agent application. |
| [AgentHost state](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs) and [model invocation guard](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/apps/Agentweaver.Api/Auth/RunModelInvocationGuard.cs) | One-time run configuration, in-memory purpose-bound credentials, current authority, and exact accepted-model checks | Process-local state and shared storage do not supply durable installation records. |
| [Image publication](https://github.com/sabbour/agentweaver/blob/d984e8bba4e6932eef4de69e93bc5be94868b16b/.github/workflows/publish-images.yml) | Source-bound publication receipts and separation of publication from deployment | Platform-image releases do not define app install/upgrade or publisher trust. |

Compatible validation code can be reused after review.
A 0.x package converter, full application installer, or completed canvas loader is not assumed.

## Manifest and content contract

The proposed application schema is `agentweaver.application/1`.
The initial host contract is `agentweaver.agent-app-host/1`.
Neither identifier describes an implemented schema or an OCI standard.
Each existing owner validates its component schema.
Unknown required kinds, schemas, or host versions block activation.

| Field | Required meaning |
| --- | --- |
| `schemaVersion`, `appId`, `version` | Schema 1, stable publisher-qualified ID, and SemVer 2.0.0. Changed bytes at an accepted publisher/app/version conflict. A separately identified fork requires explicit acceptance. |
| `host` | Host-contract major version, inclusive minimum/exclusive maximum product versions, and required component-schema versions. Pre-release compatibility requires an explicit host admission rule. |
| `entryPoints` | Stable ID, kind (`agent`, `workflow`, or typed `surface`), component ID, and bounded input schema. No shell command or install hook is an entry point. |
| `components` | Typed agent, tool, skill, restrictive policy, canvas/surface, workflow, and optional asset declarations. Empty categories are valid. |
| `files` | Canonical relative path, exact byte size, SHA-256 digest, media type, schema version, kind, and component identity. Every content file appears once. Every declared reference resolves. |
| `dependencies` | App ID, inclusive minimum/exclusive maximum SemVer, optional exact manifest digest, and dependency kind. Resolution creates a bounded immutable lock. |
| `providerRequirements` | Existing `ProviderRequirement` fields, exact adapter version, exact options-schema version, and required capabilities. Network uses existing layer requirements. Cost names its meter source. |
| `canvasRequirements` | Optional surface profile, Canvas contract version, exact adapter/options-schema versions, and required renderer/action capabilities. C1 defines selection under the planned Canvas seam; current source has no `ProviderSeam.Canvas` or compatible resolver path. |
| `configurationInputs` | Typed nonsecret slots and protected credential-reference slots. A protected slot declares its purpose and scope, not a credential value. |
| `constraints` | Restrictive limits and required destinations. The host intersects them with current project/platform policy. |

The enclosing OCI manifest digest identifies the bundle.
The configuration blob does not embed that enclosing digest.
The installed receipt records the manifest, configuration, and layer digests.
A semantic comparison digest never replaces exact-byte identity.

Packages declare capabilities, not selected providers or permissions.
Provider registrations remain trusted host composition.
SDK/model selection, tools, skills, MCP, and renderer protocols remain existing concerns, not infrastructure seams.
Canvas uses the explicit proposed contract and per-canvas selection.
Installation compatibility does not fabricate run-resource negotiation.

### Minimal example

This proposed evidence assistant uses existing domain shapes.
Its agent maps to `ProjectAgentCharter`.
Its workflow maps to `WorkflowDefinition` and `WorkflowStepDefinition`.
Its requirement maps to `ProviderRequirement`.
String enum names belong to the proposed package loader, not current HTTP serialization.

```json
{
  "schemaVersion": "agentweaver.application/1",
  "appId": "org.example.evidence-assistant",
  "version": "1.0.0",
  "host": {
    "contract": "agentweaver.agent-app-host/1",
    "minimumVersion": "1.0.0",
    "maximumVersionExclusive": "2.0.0",
    "componentSchemas": {
      "agentCharter": 1,
      "workflowDefinition": 1,
      "skill": 1
    }
  },
  "entryPoints": [
    {
      "id": "summarize",
      "kind": "workflow",
      "componentId": "summarize",
      "inputSchema": { "type": "object", "additionalProperties": false }
    }
  ],
  "components": {
    "agents": ["analyst"],
    "tools": [],
    "skills": ["evidence"],
    "policies": [],
    "canvases": [],
    "workflows": ["summarize"],
    "assets": []
  },
  "files": [
    {
      "kind": "agent",
      "id": "analyst",
      "schemaVersion": 1,
      "path": "agents/analyst.json",
      "mediaType": "application/json",
      "size": 127,
      "digest": "sha256:48fdc4cbbb656f88ab26ffd0776dc7a67c6a8679c1272dd671b56b280f4d09e3"
    },
    {
      "kind": "workflow",
      "id": "summarize",
      "schemaVersion": 1,
      "path": "workflows/summarize.json",
      "mediaType": "application/json",
      "size": 414,
      "digest": "sha256:500fe33708c50e60f027663e59a03a24d5772d887cdb2a81d85f54ba148639a6"
    },
    {
      "kind": "skill",
      "id": "evidence",
      "schemaVersion": 1,
      "path": "skills/evidence/SKILL.md",
      "mediaType": "text/markdown",
      "size": 120,
      "digest": "sha256:160109c210ee875b94473cd8fe910434c68dff2b49f5e5a473764fb4aa158f6f"
    }
  ],
  "dependencies": [],
  "providerRequirements": [
    {
      "seam": "Sandbox",
      "requiredAdapterVersion": "1.0.0",
      "requiredOptionsSchemaVersion": 1,
      "requiredCapabilities": []
    }
  ],
  "configurationInputs": [
    { "name": "modelSelection", "type": "modelSelectionReference", "required": true },
    {
      "name": "modelCredential",
      "type": "SecretRef",
      "required": false,
      "purpose": "model-invocation",
      "scope": "run"
    }
  ],
  "constraints": {
    "requiredEgress": [],
    "runLimits": { "maxChildren": 1, "maxConcurrentChildren": 1 }
  }
}
```

The adapter version is illustrative, not an available-provider claim.
The credential purpose is a requested use, not an existing grant or authority.
Empty capability requirements do not waive mandatory host isolation or policy.
The host adds its own requirements before selection.
These are documentation fixtures, not a signed or installable release.

The following files use exact UTF-8 bytes.
The JSON files have no final newline.
The skill uses LF and one final newline:

`agents/analyst.json`:

```json
{"agentId":"analyst","name":"Analyst","role":"researcher","charter":"Summarize project evidence. Do not change project files."}
```

`workflows/summarize.json`:

```json
{"id":"summarize","revision":"1.0.0","catalogVersion":"1","origin":"Generated","maximumWorkItems":1,"steps":[{"id":"research","purpose":"Summarize project evidence","mode":"Open","order":0,"cardinality":{"minimum":1,"maximum":1},"dependsOn":[],"allowedRoles":["researcher"],"allowedPhases":["analysis"],"allowedIsolationChoices":["sandbox"],"requiredProviderCapabilities":[],"fixedWork":null,"platformGate":null}]}
```

`skills/evidence/SKILL.md`:

```markdown
---
name: evidence
description: Summarize evidence with source references.
---
Read project evidence. Cite each source.
```

The host binds casting, skill assignment, and model selection in an immutable instance-specific projection.
It reuses `ProjectAgentCast`, `SkillCatalogSetting`, and `ModelSelectionSettings`.
The projection maps package-local component IDs to installation/revision-scoped effective IDs.
Projects persists the map with the installed revision.
Each consuming owner validates the map and all component references before use.
Effective IDs obey existing domain identifier bounds.
Conflicts reject the candidate without replacing existing definitions.
Package IDs cannot shadow other installations, project definitions, trusted built-in tools, or reserved roles and gates.
It does not overwrite unrelated user configuration.

Imported workflows remain external content.
The initial loader maps them to `Generated` and requires first-use confirmation.
A package cannot claim `BuiltIn` authority or fill a platform gate with agent-authored work.

### Layout and component profiles

The authoring layout uses `application.json` for the OCI configuration blob.
The content archive contains only declared files under these package paths:

| Path/category | Host contract |
| --- | --- |
| `agents/` | Charters and casting references, without platform-role creation |
| `tools/` | Typed input/output and permission metadata for a supported built-in tool, approved MCP binding, or inventoried sandbox script |
| `skills/` | `SKILL.md` and declared supporting files |
| `policies/` | Restrictive rules in the existing Policy owner's supported format |
| `canvases/` | Open/action schemas and a supported surface kind or inventoried isolated web resource |
| `workflows/` | Validated v1 step catalogs, bounds, joins, eligibility, and platform-gate references |
| `assets/` | Files required by an explicit component reference and supported loader |

Portable paths use canonical slash-separated package paths, independent of the host filesystem.
Empty directories and unused assets are not required.
Tool files cannot register adapters or load assemblies into trusted services.
Canvas files cannot create a generic plugin bridge.
Unsupported tool, policy, or renderer schemas block activation until their existing owners support them.

### Bundled Canvas declarations

**Canvas is a first-class optional bundle component.**
A declaration contains its local ID, supported profile, open/action input and output schemas, view/content references, and exact action bindings.
Declared assets join the same bounded inventory.
An installed Canvas does not register a .NET assembly, start a server, or open a panel during installation.
An authorized caller explicitly opens an accepted declaration through `surface_open`.
The host supplies the installation/revision-scoped effective ID map and selected Canvas adapter.

For the evidence assistant, the following fragment appends one file and surface entry point.
It sets `components.canvases` and adds the optional `canvasRequirements` field.
It does not replace the original three files, workflow entry point, or infrastructure provider requirements:

```json
{
  "components": { "canvases": ["evidence-panel"] },
  "entryPoints": [
    {
      "id": "evidence-view",
      "kind": "surface",
      "componentId": "evidence-panel",
      "inputSchema": { "type": "object", "additionalProperties": false }
    }
  ],
  "canvasRequirements": [
    {
      "profile": "native",
      "contractVersion": 1,
      "requiredAdapterVersion": "1.0.0",
      "requiredOptionsSchemaVersion": 1,
      "requiredCapabilities": ["actions.typed", "state.reconnect"]
    }
  ],
  "files": [
    {
      "kind": "canvas",
      "id": "evidence-panel",
      "schemaVersion": 1,
      "path": "canvases/evidence.json",
      "mediaType": "application/json",
      "size": 474,
      "digest": "sha256:2c9643133885533d4c85e19d6523139c4e8db1c3561927e4acdcd3f3bc65e269"
    }
  ]
}
```

`canvases/evidence.json` uses exact UTF-8 bytes with no final newline:

```json
{"schemaVersion":1,"id":"evidence-panel","profile":"native","view":{"kind":"builtIn","id":"workflow-progress"},"inputSchema":{"type":"object","additionalProperties":false},"actions":[{"name":"summarize","inputSchema":{"type":"object","additionalProperties":false},"outputSchema":{"type":"object","required":["operationId"],"properties":{"operationId":{"type":"string","maxLength":240}},"additionalProperties":false},"binding":{"kind":"workflow","componentId":"summarize"}}]}
```

The `native` profile, capabilities, view ID, and adapter version are proposed fixture values, not installed capability claims.
The manifest's required component schemas also add `canvas: 1`.
The loader validates the fragment against that Canvas schema and the complete accepted inventory.
It resolves `summarize` through the effective component-ID map and a core-owned workflow action binding.
Invoking it still requires current run admission, first-use confirmation, and applicable gates.
The returned `operationId` describes accepted workflow intent, not completed execution.

A2UI declarations require an exact supported protocol and host catalog revision.
The GitHub profile supports only the admitted declaration, action, and lifecycle subset.
It pins the supported SDK/CLI pair, Agentweaver profile, and verified owned content.
No portable GitHub content format, component catalog, or browser bridge is established.
The [research boundary](applications-and-surfaces.md#bounded-adapter-subset) remains authoritative.
MCP Apps declarations reference an approved connection/resource binding and enforceable exact resource revision.
They cannot create a remote MCP connection or embed credentials.
Isolated web profiles inventory their own static bytes and use the Canvas host's CSP/bridge contract.
An unsupported profile, unversioned required resource, invalid action binding, or absent provider blocks activation.
Closing a Canvas never deletes its underlying bundle, artifact, or workflow result.
Old instances retain accepted revisions across upgrade, but current authorization still applies.

## Configuration, selection, and real run pins

Configuration binding separates nonsecret values from exact protected references.
Credential values never enter bundles, configuration revisions, or durable operation state.
An accepted installed revision freezes these references:

| Reference | Frozen content |
| --- | --- |
| Bundle and dependencies | Exact manifest/configuration/layer digests and resolved dependency lock |
| Configuration | Immutable instance projection, project/platform revision inputs, limits, egress needs, and exact model-selection reference |
| Protected inputs | Exact opaque `SecretRef` ID/version and declared purpose/scope, never its value |
| Selection | Selected provider ID, exact adapter/options-schema versions, options revision, cardinality/order/layer/meter metadata, and required/advertised capabilities |
| Components | Validated workflow, agent, skill, tool, policy, and surface receipts for exact content |
| Canvas adapters | Exact selected adapter, options, supported profile, and declaration/content references. A2UI also pins its specification/catalog; the GitHub subset pins the tested SDK/CLI pair without inventing an upstream catalog. Installation records no open instance, negotiated resource, or run pin. |

Resolution uses platform defaults and allowed project overrides through the existing resolver.
It retains exclusive, ordered, singleton, layered, meter-keyed, and per-application cardinality.
Projects cannot replace a singleton or choose a Network Policy provider.
A bundle cannot supply provider registrations or unchecked adapter options.
Trusted composition binds typed options and preserves their immutable revision.
An unsupported required selection path returns incompatibility.
Installation does not add Application Hosting selection support to admit a package.

The current resolver requires exact adapter and options-schema versions.
Bundle/dependency SemVer constraints do not change that rule.
A referenced configuration change requires an explicit rebind or upgrade and creates another installed revision.
It does not mutate accepted runs or silently replace an unavailable producer.

**Installation references are not resource-bound run pins.**
Installation validates trusted descriptors, typed options, schemas, and requirements.
It does not provision Sandbox, Network, or Storage merely to claim compatibility.
It does not use `installationId` as a fake run ID.
It does not fabricate resource generation or negotiated capabilities.

After acceptance, the real run's existing owners provision and negotiate resources.
They create `PinnedProviderBinding` with the real `runId`, resource identity, positive generation, and genuine capabilities.
Network pinning retains the confirmed applied-intent generation.
A resource that lacks a required capability blocks the run without rewriting the installation receipt.

Immutable references preserve reproducibility, not authorization.
New runs, retries, resumes, turns, and protected effects require current authority.
Missing pinned content, options, credentials, models, or required producers return structured unavailable/incompatibility results.
There is no fallback to another revision or provider.

## Existing owners and proposed APIs

| Owner | Proposed responsibility | Boundary |
| --- | --- | --- |
| Projects & Config | Project app catalog, installation identity, desired/admission state, immutable installed/configuration revisions, dependencies, and active-head CAS | No direct workflow/run/environment writes, provider registration, secret redemption, or executable loading |
| Orchestrator | Durable lifecycle operation, validation/staging order, workflow receipts, first-use gates, run admission, version affinity, leases/fences, retries, and recovery | No cross-owner SQL transaction or duplicate membership/grant framework |
| AgentHost | Validate the accepted host contract, materialize exact content at the actual execution location, register supported components, and run through core gates | No mutable-tag fetch, registry credential, install hook, or trusted-service code execution |
| Environment | Real Sandbox/Storage/Network resources, separately authorized hosting operations, owned receipts, retention, and cleanup | No resource provisioning for installation compatibility |
| Policy and Identity | Restrictive AGT rules, current grants/revocation, purpose-bound credentials, and protected-effect enforcement | Publisher trust never supplies actor authority |
| Object Store and its callers | Verified immutable content/evidence under exact owned keys, create-only writes, and ownership-safe retention | No permission decision, guessed-prefix deletion, or registry provider seam |
| Gateway, MCP, and web | Authenticated projections, operation status, staged/active version display, and typed actions through owner APIs | No independent installer, authority store, or orchestration engine |
| Trusted catalog composition | Enabled providers, approved tool/MCP/renderer registrations, schemas, registry sources, and trust configuration | Bundles cannot change the platform catalog |
| Events & Sessions | Lifecycle audit linkage through existing events/outbox collaboration | A journal receipt is not authorization or a cross-owner commit |

Projects owns proposed `AgentApplicationInstallation`, `InstalledApplicationRevision`, `ApplicationConfigurationBinding`, and `ApplicationActivationReceipt` records in `projects_config`.
Orchestrator owns proposed `ApplicationLifecycleOperation`, stage receipts, deduplication, and operation fences in its schema.
Object Store retains bytes.
Environment and Identity retain resource and grant authority.
This document defines the proposed bundle schemas/APIs.
Existing source contracts remain authoritative for their existing duties.

The proposed public route family is `/api/projects/{projectId}/agent-applications`:

| Proposed route/contract | Operation |
| --- | --- |
| `POST` collection | Accept install intent with locator, expected project/configuration revisions, and idempotency key. Return an operation reference. |
| `GET` collection/instance/revision | Read authorized catalog and retained revisions without credentials or trust-store contents. |
| `POST {installationId}/operations` | Accept typed `activate`, `upgrade`, `retry`, `rollback`, or `uninstall` intent with expected instance/active revisions. |
| `GET {installationId}/operations/{operationId}` | Read durable phase, blockers, exact candidate/active revisions, and cleanup progress. |
| Internal owner commit contract | Submit fenced stage receipts and CAS desired/active/admission state through Projects, not direct database access. |
| Existing run-selection family, future versioned extension | Admit exact installed revision and entry point under current Orchestrator authority. It returns immutable candidate metadata, not a resource pin. |

Projects accepts intent and its outbox record in one owned transaction.
Orchestrator deduplicates that command and leads the lifecycle.
Each owner commits its own records and receipts.
Projects changes the active pointer only after exact stage receipts and current authorization.
Late events cannot undo a newer fence.
Gateway and MCP delegate to these owner contracts.

Install, activate, rebind, upgrade, rollback, and uninstall require current project `WriteProjects`.
That permission follows existing project Owner/tenant-admin and `projects.admin` rules.
Catalog reads require current `ReadProjects`.
Run-selection acceptance/read retain current Orchestrator-role and `projects.orchestrator` checks.
Platform registry/trust configuration remains in the existing administrator/operator boundary.
Bundle claims cannot assign a tenant, membership, role, or scope.

The current authorization-context API does not impersonate saved actor IDs.
Durable operations never persist bearer tokens or send fabricated subject selectors.
If caller credentials expire, privileged continuation waits for fresh authorized caller credentials.
Unattended continuation requires a separately reviewed existing-owner delegation path.
That missing capability is not authority supplied by this specification.

Installation binds a protected reference without redeeming its value.
The current Identity contract requires a real authorized run and purpose for redemption.
An installation ID cannot become a fake run for a credential probe.
A missing reference blocks binding.
An unavailable credential or revoked grant blocks the real run at redemption.
Required metadata validation belongs to Identity without broader redemption authority.

## Lifecycle, durable states, and recovery

Installation admission states are `staged`, `active`, `draining`, and `uninstalled`.
Operation progress is separate from that state.
Each operation reports a phase and an explicit outcome:
`pending`, `awaitingConfiguration`, `awaitingAuthorization`, `blocked`, `failed`, `cleanupPending`, or `completed`.

An initial failed install has no active revision.
A failed upgrade leaves the old active revision intact.
Its new candidate and failure evidence remain visible to authorized owners.

| Phase | Durable behavior |
| --- | --- |
| Resolve | Validate the approved source. Resolve a tag once and persist its exact digest before further requests. Resolve a bounded dependency lock. Missing/ambiguous versions or unavailable producers block. |
| Verify | Verify raw manifest/configuration/layer size and digest, publisher signature, and required provenance. Preserve bounded evidence and trust-policy revision. Tampered content or missing trust evidence cannot advance. |
| Compatibility validation | Validate schema, inventory, entry points, workflow grammar, host/source profiles, and trusted provider descriptors/options. Preserve candidate selection without provisioning resources. |
| Configuration binding | Bind nonsecret values to an immutable configuration projection and exact protected references. Validate limits and egress narrowing. Missing inputs hold the operation. |
| Stage | Persist verified bytes and owner-specific definitions under the candidate revision. Exact stage receipts remain invisible to new run admission. No package hook runs. |
| Activate | Recheck current authority, trust, expected heads, dependencies, and receipts. Projects CAS switches the active pointer and emits its event. This does not run an agent or prove resource readiness. |
| Upgrade | Repeat resolve through stage for a new revision. Show configuration/requirement differences. Keep the old revision until successful CAS. New runs use the new revision. Accepted runs keep the old one. |
| Uninstall | Validate dependents, then CAS closes new admission and disables future triggers. Default behavior drains accepted runs. Exact owned cleanup follows retained-use checks. |

Commands carry project/installation scope, operation ID, expected instance/active revisions, and an idempotency key.
Identical normalized input replays the original operation after a fresh authority check.
A reused key with different input returns conflict.
One mutating operation holds the instance's current fence.
Concurrent upgrades cannot both activate against the same head.
Leases expire and their fences increase.
Restart resumes durable phases and exact owner receipts, not process memory.

An uncertain effect returns reconciliation-required progress.
The owner reads the exact receipt before retry.
It does not blindly create another resource or claim success.
Cancelling an HTTP wait does not cancel an accepted operation.
Cancellation or supersession requires a separate authorized fenced transition.
Outboxes and consumer deduplication provide at-least-once delivery, not exactly-once effects across owners.

### Dependencies and pending work

The initial resolver accepts bounded SemVer intervals and optional exact digests.
It rejects cycles, ambiguous matches, identity mismatches, excessive graph depth/count, and conflicting constraints.
Dependency content receives the same digest/trust checks.
OCI referrers are not the dependency graph.

A reusable-content dependency resolves to an exact verified bundle digest.
A required installed-app dependency must already have an authorized compatible active revision.
No lifecycle operation implicitly installs or upgrades another application.
Dependency receipts freeze the referenced revision.
Its upgrade does not silently move dependent installations.
A required dependent blocks uninstall before admission or active-head changes.

Projects checks the active/admission fence when it accepts an application run-selection snapshot.
That owned transaction determines whether admission precedes upgrade/uninstall.
Orchestrator registers the accepted run through its own idempotent transaction.
A lost response replays the original accepted revision.
No cross-owner SQL transaction is required.

Accepted queued runs and children retain their accepted revision.
Unaccepted requests use the current active revision under fresh authorization.
A stale explicit-revision request fails instead of silently upgrading.
Pending approvals/questions retain their original request, run, workflow, and bundle references.
Upgrade never answers a gate or migrates an in-flight workflow.
Future trigger deliveries use the active revision and current authority.
Previously admitted deliveries remain distinct from unaccepted work.

### Migration and rollback limits

Initial upgrades permit compatible schemas and pure configuration transforms owned by the trusted configuration service.
They do not run package-authored migration hooks.
A service/database migration waits for its owner's separately approved release.
An incompatible workflow cannot mutate an existing checkpoint.
Shared mutable data requires compatible versions or drain before activation.

Rollback is an authorized pointer change to a retained compatible revision.
It rechecks current trust, configuration, dependencies, producer availability, and data compatibility.
It does not reverse external tool effects, user-data changes, cloud actions, or irreversible migrations.
Missing old resources or incompatible data can block rollback.
The operation reports that limit instead of a fabricated rollback success.

### Uninstall and owned cleanup

Uninstall does not erase repositories, user-owned volumes, Knowledge records, results, or audit.
It does not delete upstream registry artifacts or shared dependencies.
Run cancellation, user-data purge, and generated-output retirement are separate explicit owner operations.
Default uninstall does not cancel accepted runs or answer their pending gates.

Cleanup receipts identify the exact owner, key/resource ID, generation, and operation.
Owned grant cleanup uses existing revocation/CAS/audit semantics, not physical ledger deletion.
Shared content remains while an installation, run, dependency, or retention record references it.
The existing Object Store does not count cross-owner references.
Missing retention/ownership evidence produces `cleanupPending`, not a guessed delete.
The final `uninstalled` state requires all necessary owned cleanup receipts.
A retained tombstone records deliberate retention and incomplete cleanup.

## Supply-chain and execution boundaries

The initial archive is application content, not a container rootfs.
It uses no filesystem stacking, whiteouts, or executable install hooks.
The host verifies content before interpreting it.

The initial limits retain the conservative 0.x definitions-package envelope:

| Limit | Initial maximum |
| --- | --- |
| Configuration blob | 1 MiB |
| Content files | 256 |
| Individual expanded file | 1 MiB |
| Expanded content total | 16 MiB |
| Compressed layer | 16 MiB |
| Package-relative path | 240 characters |

Streamed counters enforce compressed and expanded limits.
Host release configuration can lower them.
A bundle cannot raise them.
Larger assets require a separately admitted profile and acceptance evidence.
These initial limits are proposed product limits, not OCI limits.

Extraction rejects rooted/drive/UNC paths, traversal, empty/dot segments, duplicate paths, case/Unicode collisions, and alternate streams.
Portable-path checks reject reserved Windows device basenames, including names with extensions.
They reject trailing-dot and trailing-space aliases in every path segment.
It rejects symlinks, hardlinks, device nodes, FIFOs, setuid/setgid modes, sparse-file escapes, and external archive links.
It does not restore archive owners or executable permissions blindly.
It validates UTF-8, JSON depth, duplicate properties, actual sizes, inventory coverage, and expansion budgets.
Bounded parsing precedes expensive processing.
An isolated staging root uses no-follow containment checks.
Failed staging never changes active content.

Exact digests and sizes protect integrity, not publisher identity or safe behavior.
The initial trust profile recommends Notary Project signatures under strict host-owned repository/publisher policy.
The verifier matches the signed target descriptor to the exact root bundle digest.
Unknown critical attributes or required verification plugins block.
A bundle cannot install a verifier plugin or trust root.

Provenance and SBOMs remain detached metadata about an exact subject.
Required provenance must be authenticated and match subject, builder/source constraints, and policy.
Self-authored provenance and publisher signatures do not grant user authority.
Historical verification receipts preserve evidence, not irrevocable permission.
The host rechecks current trust before activation and new runs.
Current revocation can block future protected effects for an old pinned run.

Verified executable, skill, policy, and canvas content remains immutable and read-only at the real host execution location.
The host separates this content from mutable workspace files and user data.
It enforces immutability and verifies exact accepted bytes before load or effect.
A historical digest receipt does not authorize later-modified local bytes.
Missing enforcement or any byte mismatch fails before load or effect.

Tools and skill scripts run only inside the admitted Sandbox and current tool grants/Policy.
Skill instructions remain untrusted input.
They cannot widen permissions or change selected providers through prompt text.
External MCP uses approved gateway bindings and scoped credential injection.
Package-controlled endpoints do not receive credentials by declaration.

Declarative canvases use a pinned supported renderer/catalog.
Custom web content uses an isolated origin, restrictive iframe/CSP, typed bridge, and validated messages.
It receives no AgentHost token, browser bearer, platform secret, or ambient control-plane access.
Canvas actions traverse current authorization and audit.
A registry or signature supplies neither a sandbox nor network/renderer permission.

Registry access stays in the trusted control plane under approved read-only credentials and egress configuration.
Credentials remain outside packages, durable operation state, diagnostics, and AgentHost.
Redirects and token realms cannot inherit registry credentials automatically.
Descriptor download URLs are not unrestricted fetch authority.
There is no insecure TLS fallback or automatic trust-policy weakening.
Runtime consumers read verified retained content by digest, not mutable tags.

Local tests use inert fixtures and disposable owner stores.
They do not prove live installation or execution.
Registry publication, real bundle execution, paid-model use, and cloud deployment require separate target-specific authority.

## OCI distribution decision

**Recommendation:** Use OCI Artifacts with the adopted OCI image-manifest format and ORAS transport.
Do not use the obsolete proposed `application/vnd.oci.artifact.manifest.v1+json` media type.
No new transport or package-manager ecosystem is necessary.

The versioned baseline is OCI Image 1.1.1 and Distribution 1.1.1.
Their adopted formats take precedence over old overview links to release-candidate artifact formats.
ORAS 1.3.4 includes registry-origin credential scoping and diagnostic-redaction fixes.
A future implementation pins that reviewed version or a reviewed successor.
The runtime-boundary reference is OCI Runtime 1.3.0, not a new Agentweaver container runtime.
Authoritative references appear in [Sources](#sources).

### Initial interoperability profile

| Feature | Support and rule |
| --- | --- |
| Root manifest | `application/vnd.oci.image.manifest.v1+json`, `schemaVersion: 2`, custom `artifactType`, required configuration descriptor, and one content layer |
| Application type | Proposed `application/vnd.agentweaver.application.v1+json`. This is a product type, not an OCI or IANA-registration claim. |
| Configuration | Proposed `application/vnd.agentweaver.application.config.v1+json`. It contains the application manifest and inventory. |
| Layer | Proposed `application/vnd.agentweaver.application.content.v1.tar+gzip`. It contains declared files, not a rootfs. |
| Addressing | Resolve tags once. Retain exact SHA-256 manifest/configuration/layer digests. Verify bytes and sizes independently of optional registry headers. |
| Index | Image 1.1.1 supports manifest/platform entries, `artifactType`, and `subject`. Initial bundle roots explicitly reject multi-platform/nested indexes. Referrers and offline-layout indexes remain separate supported uses. |
| Subject | Image 1.1 defines a weak association with another manifest. The association supplies discovery, not trust or dependency semantics. |
| Referrers | Distribution 1.1 returns an OCI image index. Consumers handle pagination, optional type filters, and local subject/type validation. |
| Fallback | Only a referrers-API 404 selects the standard `sha256-<digest>` referrers tag. Empty/missing evidence does not waive signatures. Auth, transport, and server errors are not absence. |
| Copy/export | ORAS transfers the graph. Detached signatures/provenance require explicit copy and retention. Offline OCI layouts preserve exact digests and all required evidence. |
| Dependencies | The application manifest and installed lock define dependencies. `subject` and referrers do not supply install order. |
| Signatures | OCI stores/discovers metadata. ORAS transports it. An existing supported verifier applies the concrete trust profile. Cosign requires its own explicit profile. |

The initial reader does not mutate referrers fallback tags.
Publishing tooling must maintain those tags safely if the registry lacks the referrers API.
Concurrent fallback-tag updates can lose entries without client conflict control.
Native referrers avoid that tag-maintenance requirement.
Missing required evidence blocks activation in either mode.

An admitted registry needs a tested capability profile.
Artifact storage support alone does not prove exact-byte preservation, referrers, fallback maintenance, copy, retention, or verification.
ACR is an appropriate Azure-first configured source.
Microsoft documents OCI artifacts and supply-chain graph support with ORAS.
That documentation is not live proof for the Agentweaver environment.
GHCR or another registry requires the same interoperability suite.
Registry configuration does not become a provider seam.

### Alternatives

| Alternative | Decision |
| --- | --- |
| Plain tar/zip over HTTP | Retain tar as the content layer. A standalone archive lacks the chosen descriptor, authentication, discovery, and detached-evidence conventions. |
| OCI image-layout archive | Permit offline/test transfer of the same verified graph. It is not another install format or a trust exception. |
| NuGet/npm/Python package | Use for host implementation dependencies, not the app boundary. Language semantics, install scripts, and dependency solvers are unnecessary here. |
| GitHub repository package | Reuse 0.x import invariants. Git refs are not the portable full-app installation contract. Conversion is separate work. |
| Generic plugin/install service | Reject initially. Existing owners and OCI/ORAS cover the required duties without another service or extensibility framework. |

## Phased implementation and acceptance

This is a planned post-core extension.
It uses completed core contracts without widening their current issue acceptance.
Tool/canvas content also waits for the applicable P2 gateway/surface enforcement.
The phase labels do not change the existing P0-P3 ledger or cutover gate.
The release backlog is `v1.0.0` P2, after applicable P1 foundations.
The [Canvas C1-C5 plan](applications-and-surfaces.md#canvas-provider-delivery-and-milestone-placement)
defines the abstraction, concrete adapters, bundle integration, and exact-SHA acceptance.
User-directed tracking issue [#1878](https://github.com/sabbour/agentweaver/issues/1878)
records the bundle and Canvas plan in milestone `v1.0.0`.
Separate implementation children and an execution schedule remain pending.
The issue does not add another P1 or cutover prerequisite.

| Slice | Existing owners and prerequisites | Acceptance |
| --- | --- | --- |
| B1: Contract and inert validation | Projects, Orchestrator, trusted composition. Existing configuration, selection, workflow validators, and outbox foundations. | Schema/loader parity, raw identity, incompatibility, dependency locks/cycles/conflicts, signature/provenance checks, bad digest/archive/bounds, and no code execution/resource pin at install |
| B2: Durable installation and activation | Projects authority/active head, Orchestrator operation leadership, Object Store callers. Completed typed gates and fresh authority. | Missing inputs/references, denied activation, duplicates/conflicts, restart, lost responses, stale fences, concurrent operations, isolated staging, and one active-head CAS |
| B3: Accepted runs and components | Orchestrator, AgentHost, Environment, Policy/Identity, Tool & MCP gateway. Real grants, negotiation, content delivery, and enforcement. | Genuine run/resource pins, lower negotiated capabilities, unavailable producers, actual host-readable content, first-use gates, revocation, credential redaction, and canvas isolation |
| B4: Upgrade, rollback, uninstall | Projects, Orchestrator, Environment, Identity, retention owners. B2/B3 and exact owner receipts. | Old-run affinity, fresh new-run authority, pending-gate continuity, honest migration/rollback limits, drain, user-data retention, and exact cleanup or pending state |
| B5: Interoperability and approved live acceptance | Registry/release configuration, Gateway/MCP/web, acceptance coordinator. Separate target-specific authority. | Native referrers/404 fallback, tag mutation, missing evidence, copy/export, auth/TLS/redirect failures, and exact-SHA Azure API/UI/MCP journeys |

The current v1 source includes a pure, inert Projects.Config validator for the
application manifest. It checks raw configuration identity, manifest and
entry-point shape, declared inventory metadata and documented bounds,
dependency constraints against a supplied graph, and compatibility against
explicit host context and the read-only provider catalog. It does not inspect
content-layer bytes or archives, verify signatures or provenance, import skill
content, persist an installation or dependency lock, or load executable
content. This is a source-only validation foundation, not completion of B1 or
installation acceptance; the remaining B1 checks require their owning
implementations and evidence.

Implementation issues can use those owner-aligned slices.
The tracking issue contains B1-B5 and Canvas C1-C5.
This specification does not add a new internal coordination ledger.
Future product changes require component changesets and affected documentation.
Source-only candidates remain distinct from deployed acceptance.

### Required integration scenarios

| Scenario | Required result |
| --- | --- |
| Full install/configure/activate/run/upgrade/drain/uninstall | Durable exact-revision receipts at each phase. No phase implicitly authorizes the next. |
| Incompatible host/schema/provider/options or missing model | Structured incompatibility/unavailable, no fallback, and no fabricated negotiation |
| Missing nonsecret input or protected reference | Configuration wait without an initial active revision. Real-run credential absence/revocation denies execution. |
| Bad signature/digest/size/archive | No active content change, trusted code load, activation, or secret access |
| Valid publisher signature, unauthorized actor | Activation denied through current Projects authority |
| Restart at each phase and response-loss boundary | Original operation/receipt recovery without duplicate effects or lost active-head state |
| Duplicate key, conflicting key, concurrent upgrade, stale completion | Replay or conflict with fenced reconciliation. At most one successful active-head transition. |
| Upgrade with an old accepted run and pending gate | Old bundle/configuration/provider pins and gate request remain exact. New runs use the new active revision with fresh authority. |
| Role/grant revocation after installation | New run, resume, or protected effect denied despite historical receipts |
| Uninstall with accepted runs | Admission closes, accepted work drains by default, and pins/gates remain intact |
| Uninstall with a required dependent | Explicit blocker before admission or active-head change |
| Owned resources mixed with user/shared data | Only exact receipt-owned resources are released. User data/audit and referenced content remain. Missing proof reports `cleanupPending`. |
| Effective ID maps, references, bounds, or collisions | Persisted installation/revision-scoped IDs and references validate before use. Conflicts or shadowing project definitions, other installations, trusted tools, or reserved roles/gates reject. |
| Bundled Canvas declaration and both supported profiles | Validate the exact inventory, schemas, adapter/catalog versions, and action references. Installation opens nothing. Authorized open/action/close uses the same core path as ordinary UI/MCP. |
| Canvas refresh, concurrent actions, response loss, or renderer failure | Restore exact instance state without another run. CAS/idempotency and effect-owner reconciliation prevent duplicate effects. Explicit status, safe shell, artifact access, and keyboard recovery remain available. |
| API-local staging, writable host content, or modified local bytes | Real host reads exact immutable/read-only content, separate from mutable workspace/user data. Missing enforcement, byte mismatch, or dangling control-plane paths fail before load/effect. |
| Referrers unavailable/unauthorized/incomplete | Only API 404 selects fallback. Required evidence absence/errors block trust. |
| Archive links, collisions, Windows device basenames/trailing-dot or space aliases, expansion, unknown executable/renderer schema | Explicit rejection without staging escape, trusted-process execution, or silent schema downgrade |

### Remaining authority and capability decisions

Approved publishers/trust roots, registry access/retention, and unattended delegation need existing-owner decisions before implementation.
Enlarged asset profiles, data migrations/purge, runtime installation, and paid/cloud acceptance need separate authority.
This specification creates no signing key, trust root, credential, resource, or destructive operation.
Those gaps do not change current P1 completion.

## Sources

| Reference | Basis |
| --- | --- |
| [OCI Image 1.1.1 manifest](https://github.com/opencontainers/image-spec/blob/v1.1.1/manifest.md) | Adopted manifest media type, `artifactType`, configuration/layers, subject, and artifact guidance |
| [OCI Image 1.1.1 descriptors](https://github.com/opencontainers/image-spec/blob/v1.1.1/descriptor.md) | Exact size/digest identity and verification |
| [OCI Image 1.1.1 index](https://github.com/opencontainers/image-spec/blob/v1.1.1/image-index.md) | Index/platform/subject support |
| [OCI Image 1.1.1 layout](https://github.com/opencontainers/image-spec/blob/v1.1.1/image-layout.md) | Offline content-addressed graph |
| [OCI Distribution 1.1.1](https://github.com/opencontainers/distribution-spec/blob/v1.1.1/spec.md) | Digest pull, referrers, pagination, fallback tags, and update-race limits |
| [ORAS 1.3 push](https://github.com/oras-project/oras-www/blob/main/versioned_docs/version-1.3/commands/oras_push.mdx) | Custom type/configuration/content and explicit image-spec behavior |
| [ORAS 1.3 discovery](https://github.com/oras-project/oras-www/blob/main/versioned_docs/version-1.3/commands/oras_discover.mdx) | Native referrers and tag-schema options |
| [ORAS 1.3.4 release](https://github.com/oras-project/oras/releases/tag/v1.3.4) | Reviewed-tool baseline and credential-origin/redaction fixes |
| [ORAS registry guidance](https://oras.land/docs/compatible_oci_registries/) | Registry support is capability-specific |
| [ACR artifacts](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-manage-artifact) | Published OCI and supply-chain graph support |
| [Notary signature specification](https://github.com/notaryproject/specifications/blob/main/specs/signature-specification.md) | Signed target descriptor, critical attributes, and supported envelopes |
| [Notary trust policy](https://github.com/notaryproject/specifications/blob/main/specs/trust-store-trust-policy.md) | Host-owned roots, repository scope, publisher identity, and strict verification |
| [Sigstore verification](https://docs.sigstore.dev/cosign/verifying/verify/) | Distinct signature/identity/attestation checks, not implicit Notary compatibility |
| [SLSA 1.1 provenance](https://slsa.dev/spec/v1.1/provenance) | Subject, builder, inputs, and authenticated provenance constraints |
| [OCI Runtime 1.3.0 bundle](https://github.com/opencontainers/runtime-spec/blob/v1.3.0/bundle.md) | Filesystem bundle/runtime boundary |
| [`runc` reference](https://github.com/opencontainers/runc/blob/v1.3.0/README.md) | Linux container runtime role, not application installation |
