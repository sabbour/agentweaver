# Component releases

`releases/foundation.json` is a draft component manifest, not a platform release.

| Component | Kind | Manifest version |
| --- | --- | --- |
| `Agentweaver.Abstractions` | Contract | `0.0.0` |
| `Agentweaver.Providers` | Library | `0.0.0` |
| `Agentweaver.Orchestrator.Core` | Library | `0.0.0` |
| `Agentweaver.Persistence.Postgres` | Library | `0.0.0` |
| `Agentweaver.Secrets.AzureKeyVault` | Library | `0.0.0` |
| `Agentweaver.Identity` | Library | `0.0.0` |
| `Agentweaver.AgentRuntime` | Library | `0.0.0` |
| `Agentweaver.Telemetry` | Library | `0.0.0` |
| `Agentweaver.Telemetry.AzureMonitor` | Library | `0.0.0` |
| `Agentweaver.ObjectStore.AzureBlob` | Library | `0.0.0` |
| `Agentweaver.Identity.Broker` | Service | `0.0.0` |
| `Agentweaver.EventsAndSessions` | Service | `0.0.0` |
| `Agentweaver.Gateway` | Service | `0.1.0` |
| `Agentweaver.Mcp` | Service | `0.1.0` |
| `Agentweaver.Environment` | Library | `0.0.0` |
| `Agentweaver.Providers.Sandbox.AgentSandbox` | Library | `0.0.0` |
| `Agentweaver.Knowledge` | Service | `0.0.0` |

The manifest pins exact project-reference compatibility but does not pin container
image digests. The existing Identity Broker image `f989` remains separate from
this package baseline. Package components use the 0.0.0 initial baseline. Their
initial release notes come from the archived P0 changesets referenced by the prior
release receipt.
The Gateway is a source-only service candidate at `0.1.0`; its manifest entry and
documentation do not claim deployment or live acceptance.
The first-party MCP host is a source-only service candidate at `0.1.0`; it validates
Broker access tokens and delegates to the Gateway, without claiming deployment or
live acceptance.
The Orchestrator Core compatibility entries include both Abstractions and Providers,
matching its direct project references.
The runtime library pins its native SDK dependency and references Abstractions
and Identity. Environment now references Identity for its separate bootstrap
profile contract. The separate AgentHost service entry describes the unpublished executable and pinned image definition.
CI builds an exact-source amd64 image, measures its compressed OCI bytes, and checks native SDK startup without model execution.
These components do not publish credentials or prove deployment or paid-model acceptance.

Each product component change needs a fresh `.changeset` record. The record names the manifest component ID and semver intent. Documentation-only changes do not need a changeset.

## Validate and prepare

Run the validator:

```powershell
npm run release:validate
npm run release:validate -- --base <full-base-commit-sha>
```

The base-aware form checks changeset coverage for changed components. CI supplies the pull request base commit.

`npm run release:plan` writes a source-bound plan under ignored `artifacts/release/`. It does not change component versions.

`npm run release:apply` updates manifest versions, project version mirrors, changelog, archive records, and a receipt. It requires a clean tree and a valid source-bound plan.

`npm run release:pack` builds and packs the manifest components. It writes local package and image artifacts. It does not push them to a registry or package feed.

## Publication boundary

The manual publication workflow requires an explicit publish choice and environment approval. It publishes the prepared artifacts only.

The source has no platform release composition or deployment gate. Release preparation does not prove Azure acceptance.
