# Agentweaver 1.0

This branch (`v1`) is where Agentweaver 1.0 is built. It starts from an empty history on
purpose: 1.0 is a rebuild, not a refactor of 0.x.

- **0.x keeps shipping** on [`dev`](https://github.com/sabbour/agentweaver/tree/dev). It is the reference for behavior, not
  a source of code to copy.
- **1.0 ports behavior, not code.** Each 0.x capability is tracked in a parity map and
  rebuilt behind the 1.0 contracts.
- **Status:** P0 foundations are under development. The architecture remains Proposed;
  the first implementation slice supplies libraries and validation tooling, not a
  deployable platform.

## Start here

| Document | What it covers |
| --- | --- |
| [ADR 0001: Agentweaver 1.0 platform architecture](docs/architecture/decisions/0001-platform-architecture.md) | Overview: goals, decisions, architecture at a glance, delivery strategy, roadmap alignment, phases, risk register |
| [Provider seams](docs/architecture/design/provider-seams.md) | Provider model, the 15 seams, per-seam designs, conformance |
| [Sessions and coordination](docs/architecture/design/sessions-and-coordination.md) | Run journal, suspend/resume, session tree, messages, knowledge records |
| [Orchestration](docs/architecture/design/orchestration.md) | Thin coordinator, workflow step catalogs, typed decision tools, rules in code |
| [Applications and surfaces](docs/architecture/design/applications-and-surfaces.md) | One application model (`live` → `preview` → `published`), Application Hosting, the surface panel |
| [Services and release](docs/architecture/design/services-and-release.md) | Service decomposition, data plane, per-service versioning and release |

## What 1.0 changes

- Backends plug in through **provider seams** instead of being wired into one monolith.
- The platform splits into independently versioned **control-plane and data-plane
  services**.
- Agent memory, decisions, and inbox move from repository files into the **session tree
  and messages**.
- The coordinator prompt stays **thin**; workflow rules live in code.
- Previews and published applications become **one application model**, shown in
  Agentweaver's own UI next to a **surface panel**.
- 1.0 runs **in the cloud only** (AKS and Azure first). There is no local runtime mode.

## First foundation slice

The initial P0 delivery contains provider descriptor and binding contracts, a pure
catalog/resolver, conformance-focused tests, and build/release validation. It does not
start any product services or provision Azure resources.

Provider selection initially covers exclusive and platform-singleton seams. Composite,
layered, meter-keyed, and application-scoped selection remain subsequent work; callers
receive an explicit error for unsupported selection rather than a fallback. Resolving a
descriptor produces a candidate, not a provisioned resource. A run binding pins the
resource's negotiated capabilities only after provisioning.

The release manifest describes the independently versioned foundation libraries.
It is a draft composition, not evidence of deployment or a released platform.

Remaining P0 work includes service-owned Postgres schemas and the transactional outbox,
Identity/Key Vault, Blob storage, telemetry, and deployable control-plane services.

## Build and check the foundation

Use the .NET 10 SDK selected by `global.json` and Node.js 24. Run these commands from
the repository root; tests do not start local product services or contact Azure.

```powershell
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
dotnet test tests\Agentweaver.Providers.Tests\Agentweaver.Providers.Tests.csproj --no-build --no-restore --configuration Release
npm run release:validate
npm run test:release
```

The Node tooling has no external dependencies; no npm installation is required.
The `v1 foundation CI` workflow runs these same checks for PRs targeting `v1` and
pushes to `v1`. See [provider foundation](packages/Agentweaver.Providers/README.md)
for supported resolution behavior and [release composition](releases/README.md)
for manifest constraints.
