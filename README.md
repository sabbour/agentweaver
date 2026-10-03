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

## Implemented foundations

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

The provider-neutral Secrets foundation defines opaque, versioned references and
purpose/run-bound requests for trusted Identity redemption. Short-lived credential
values remain outside descriptors, bindings and durable state. This is a contract
and an Azure Key Vault adapter library: the adapter resolves exact versions through
an injected Azure SDK client or workload-identity-ready credential. It does not
authorize callers, provision a vault or deploy a service.

The [OpenTelemetry foundation](packages/Agentweaver.Telemetry/README.md) supplies
native in-process trace, metric, and log composition with service resource
identity. The dependent
[Azure Monitor integration](packages/Agentweaver.Telemetry.AzureMonitor/README.md)
adds an opt-in exporter for all three signals; neither library starts a service,
provisions an Azure resource, or proves cloud delivery.

The Postgres P0 slice adds `Agentweaver.Persistence.Postgres`: service-schema migration,
transaction-coupled outbox enqueue, per-stream sequencing, leased claims, and fenced
acknowledgment. Real PostgreSQL tests cover atomicity, duplicate handling, concurrency,
and recovery. This is a persistence library, not a running relay or a deployed service.

The Blob Object Store foundation adds provider-neutral opaque-object operations
and an injected Azure SDK adapter for platform artifacts. It does not expose
agent workspace storage or provision an Azure account; see the
[Object Store guide](packages/Agentweaver.ObjectStore.AzureBlob/README.md).

Remaining P0 work includes wiring these primitives into real services, Identity
authorization and workload identity, and the dedicated Azure integration environment.

## Build and check the foundation

Use the .NET 10 SDK selected by `global.json`, Node.js 24, and a running
Docker-compatible engine. PostgreSQL tests start and dispose their own Testcontainers
database; they do not use a shared database or start local product services.
Run these commands from the repository root:

```powershell
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
dotnet test tests\Agentweaver.Providers.Tests\Agentweaver.Providers.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Secrets.AzureKeyVault.Tests\Agentweaver.Secrets.AzureKeyVault.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Persistence.Postgres.Tests\Agentweaver.Persistence.Postgres.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Telemetry.Tests\Agentweaver.Telemetry.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Telemetry.AzureMonitor.Tests\Agentweaver.Telemetry.AzureMonitor.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.ObjectStore.AzureBlob.Tests\Agentweaver.ObjectStore.AzureBlob.Tests.csproj --no-build --no-restore --configuration Release
npm run release:validate
npm run test:release
```

The Node tooling has no external dependencies; no npm installation is required.

## Project issue-to-merge workflow

Agentweaver project sessions discover the executable Copilot SDK extension in
`.github/extensions/agentweaver-issue-to-merge/extension.mjs`. Reload extensions
after checking out a branch containing it. This is a project extension, not a
GitHub Actions workflow or a global user installation. Discovery only registers
the workflow; it does not automatically start it. [Project routing instructions](.github/copilot-instructions.md)
identify when an agent may offer it.

With explicit user workflow authorization (or a matching skill/slash command),
invoke `run_dynamic_workflow` by name with arguments such as:

```json
{
  "name": "agentweaver-issue-to-merge",
  "args": {
    "task": "Implement the linked issue with focused tests and documentation",
    "issueNumber": 1742,
    "milestone": "Squad",
    "labels": ["type:chore", "area:workflows"],
    "baseBranch": "v1"
  }
}
```

The default `mode: "rehearse"` traverses eight phases with zero agents and no
mutations. Add `"mode": "deliver"` only for authorized work in a clean isolated
feature worktree. Optional `repository` defaults to `sabbour/agentweaver`;
`baseBranch` defaults to `v1`. Type and area labels and a milestone are required.
Do not guess workflow resource limits; native approval precedes invocation.
Inspect the run's durable status and resume its run ID when appropriate. Journals
are scoped to the initiating session even though the extension is project-wide.

Delivery stages implementation, targeted validation, one parallel local review
pair, surgical corrections, exact-head PR and review evidence, fresh CI and
serialized admission, then a scoped cleanup handoff. Admission blocks if
exclusive ownership or exact candidate evidence cannot be verified; it does not
provide a global lock. The running session cannot archive itself or delete its
checked-out branch. This process-only extension has a Changeset exemption on
v1 (which has no installed Changesets pipeline); it does not release or deploy
the product and does not change branch protection. Its rehearsal does not prove
live CI or merge behavior.

Run focused native tests with
`node --test .github/extensions/agentweaver-issue-to-merge/workflow.test.mjs`.
The [repository workflow canvas](.github/extensions/agentweaver-workflow-canvas/README.md)
offers a read-only view of native dynamic workflow runs published cooperatively
by sessions that have loaded its project extension; it does not discover runs
from unobserved sessions.
The `v1 foundation CI` workflow runs the same suites with coverage for PRs targeting
`v1` and pushes to `v1`. After restore and build, use `npm run coverage:dotnet`
and `npm run coverage:node` instead of the test commands above to reproduce its
coverage reports. CI publishes a readable summary and downloadable reports with
the tested source and PR head SHAs; no coverage percentage threshold is imposed.
See [coverage commands and scope](docs/guide/testing.md#code-coverage).
See [Azure Key Vault adapter](packages/Agentweaver.Secrets.AzureKeyVault/README.md)
for trusted-host composition and transport-test scope.
See [provider foundation](packages/Agentweaver.Providers/README.md)
for supported resolution behavior and [release composition](releases/README.md)
for manifest constraints. [Testing and Azure acceptance](docs/guide/testing.md)
distinguishes current integration coverage from future deployed E2E tests.
