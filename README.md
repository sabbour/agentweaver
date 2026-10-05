# Agentweaver 1.0

This branch (`v1`) is where Agentweaver 1.0 is built. It starts from an empty history on
purpose: 1.0 is a rebuild, not a refactor of 0.x.

- **0.x keeps shipping** on [`dev`](https://github.com/sabbour/agentweaver/tree/dev).
  It is the behavior reference and a source of compatible, reviewed code for
  selective reuse.
- **1.0 preserves behavior behind new contracts.** Each 0.x capability is tracked
  in a parity map; compatible, reviewed code may be reused selectively.
- **Retain the existing UI.** Future P1 frontend work keeps most of the existing
  Agentweaver UI and adapts its API wiring to the new services. Redesign requires
  an explicit user request; no UI work is included in P0.
- **Status:** P0 foundations are under development. The architecture remains Proposed;
  the first implementation slice supplies libraries and validation tooling, not a
  deployable platform. [Phase progress](docs/architecture/decisions/0001-platform-architecture.md#phases)
  distinguishes merged libraries, in-progress candidates, and remaining work.

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

Provider selection covers exclusive and platform-singleton seams, ordered Guardrails/
Telemetry sets, and layered Network Policy (required L3/L4, optional L7). Cost
meter-keyed and Application Hosting per-app selection remain subsequent work; callers
receive an explicit error for unsupported single-provider resolution rather than a
fallback. Resolving a descriptor produces a candidate, not a provisioned resource.
A run binding pins negotiated capabilities only after provisioning; layered pinning
also requires the confirmed applied egress-intent generation.

The release manifest describes independently versioned libraries and the unpublished Identity broker candidate.
It is a draft composition, not evidence of deployment or a released platform.

The [P0 Azure infrastructure story](docs/specs/1777-azure-p0-infrastructure.md) adds
a dedicated-environment layout: native Bicep modules under `infra/bicep/`
(public AKS API with OIDC/workload identity, Entra-only PostgreSQL, RBAC-only Key
Vault, Blob storage, Azure Monitor, least-privilege per-service identities), a
Kustomize base under `deploy/k8s/base/`, and dependency-free Node CLI tooling
under `scripts/azure/` for read-only plan, confirmed exact-SHA deployment, and
optional acceptance evidence collection. The full foundation template is for
an approved initial deployment; an AKS-only path updates an existing cluster
and its workload-identity federation without redeploying external P0 resources.
Source definitions are not deployment or runtime proof; the approved target's
deployment and smoke acceptance are tracked separately in
[#1812](https://github.com/sabbour/agentweaver/issues/1812) and
[#1814](https://github.com/sabbour/agentweaver/issues/1814). See
[Azure infrastructure tooling](scripts/azure/README.md) for the guardrails.

The [Azure installer](scripts/azure/README.md) offers guarded PostgreSQL setup
through the separate, default-off `--bootstrap-identity-postgres` option.
Ordinary `--execute` does not run this setup. The Identity broker supports
upstream authorization-code login with PKCE and no required client secret.
An upstream client secret is optional for explicitly configured confidential clients.

The [P0 Foundation Probe story](docs/specs/1784-foundation-probe.md) adds an
acceptance-only .NET workload Job, source-bound image receipt, and ownership-safe
Key Vault, Blob, PostgreSQL, and Azure Monitor checks. Its default CLI plan is
non-mutating; no cloud resource, secret fixture, or image is created or published
by CI.

The provider-neutral Secrets foundation defines opaque, versioned references and
purpose/run-bound requests for trusted Identity redemption. Short-lived credential
values remain outside descriptors, bindings and durable state. This is a contract
and an Azure Key Vault adapter library: the adapter resolves exact versions through
an injected Azure SDK client or credential, or through explicit AKS workload-identity
host composition. It does not authorize callers, provision a vault or deploy a
service. See the [workload-identity story](docs/specs/1766-aks-workload-identity.md)
for this slice's boundaries.

The [OpenTelemetry foundation](packages/Agentweaver.Telemetry/README.md) supplies
native in-process trace, metric, and log composition with service resource
identity. The dependent
[Azure Monitor integration](packages/Agentweaver.Telemetry.AzureMonitor/README.md)
adds an opt-in exporter for all three signals; neither library starts a service,
provisions an Azure resource, or proves cloud delivery.

The [Identity authorization library](packages/Agentweaver.Identity/README.md)
is an **implemented CANDIDATE** for #1776: it wraps trusted secret redemption with
server-owned, exact run grants. It compares grant identity, revision, expiry and
bindings after asynchronous acquisition, narrows lifetime metadata on the original
credential without reading its value, and invalidates acquired credentials on
every subsequent error or cancellation. It does not provide a grant store,
authenticate network callers, or deploy an Identity service.

The [Identity broker candidate](docs/specs/1779-identity-broker.md) adds a native
.NET 10/OpenIddict service with owned PostgreSQL stores, external OIDC login,
authenticated consent, S256 PKCE, resource audiences, and refresh replay rejection.
It requires explicit host credentials and has no ambient user-secrets or development-certificate fallback.
Its P0 redemption composition (#1783) validates bearer issuer, signature, lifetime,
and configured audience before using token subject/project/run claims; append-only
grant revisions are changed with PostgreSQL compare-and-swap and durable idempotency.
The broker issues project/run claims only after matching authorize selectors to
an active Identity-owned grant for the authenticated subject, and rechecks that
binding at token exchange.
The endpoint composes `AuthorizedSecretRedemption` with the existing exact-version
Key Vault adapter and explicit workload identity. The draft service entry has no
image digest because it remains unpublished. Azure publication/proof (#1790) is
still separate.

The Postgres P0 slice adds `Agentweaver.Persistence.Postgres`: service-schema migration,
transaction-coupled outbox enqueue, per-stream sequencing, leased claims, and fenced
acknowledgment. Consumer-scoped inbox receipts admit a stable message identity in the
same caller-owned transaction as domain changes and optional outbox enqueue, returning
an explicit duplicate outcome. Real PostgreSQL tests cover atomicity, duplicate handling,
concurrency, and recovery. Delivery remains at least once; this is a persistence
library, not a running relay or consumer service. A bounded, caller-driven
`OutboxRelay` publishes through an injected transport before fenced acknowledgment;
it does not start a daemon or guarantee exactly-once delivery.

The Blob Object Store foundation adds provider-neutral opaque-object operations
and an injected Azure SDK adapter for platform artifacts. It does not expose
agent workspace storage or provision an Azure account; see the
[Object Store guide](packages/Agentweaver.ObjectStore.AzureBlob/README.md).

The Identity broker now composes the admitted authorization boundary with an
Identity-owned PostgreSQL run-grant authority and the exact-version Key Vault
workload-identity adapter. Its source now also defines a dedicated HTTPS
ClusterIP runtime, separate runtime and migration workload identities, and an
opt-in schema migration Job. Runtime PostgreSQL authentication uses an async
Entra token callback per new physical connection; normal startup verifies the
schema and never applies migrations. The source is still unpublished and
undeployed: approved image, certificate, Secret, ConfigMap, PVC, database
principal/bootstrap, and egress inputs remain operator responsibilities. No
Azure, PostgreSQL, or Kubernetes write occurred.

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
dotnet test tests\Agentweaver.Identity.Tests\Agentweaver.Identity.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Persistence.Postgres.Tests\Agentweaver.Persistence.Postgres.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Telemetry.Tests\Agentweaver.Telemetry.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Telemetry.AzureMonitor.Tests\Agentweaver.Telemetry.AzureMonitor.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.ObjectStore.AzureBlob.Tests\Agentweaver.ObjectStore.AzureBlob.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.FoundationProbe.Tests\Agentweaver.FoundationProbe.Tests.csproj --no-build --no-restore --configuration Release
npm run release:validate
npm run test:release
npm run test:azure
az bicep build --file infra\bicep\main.bicep --stdout
kubectl kustomize deploy\k8s\base
kubectl kustomize deploy\k8s\acceptance\foundation-probe
```
The Node tooling has no external dependencies; no npm installation is required.
`npm run release:plan`, `npm run release:apply`, and `npm run release:pack`
compute deterministic version bumps, apply them to the manifest and checked-in
project mirrors, and prepare locked package and service-image artifacts with
source-bound provenance. None of these commands publishes or deploys artifacts.
Pass `--packages-only` to `release:pack` to prepare only the manifest's NuGet
packages without rebuilding service images.
The manual publication workflow requires a separate explicit choice.
See [release planning and package preparation](releases/README.md#release-planning-and-package-preparation)
for the full command contract and failure-mode guarantees.

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
checked-out branch. This process-only extension has a version-neutral changeset;
product changes require fresh component changesets on v1 even without an npm
Changesets CLI. The extension does not release or deploy
the product and does not change branch protection. Its rehearsal does not prove
live CI or merge behavior.

If the coordinator denies admission after publication, ordinary delivery and
rehearsal remain unchanged. An explicitly selected `mode: "correct"` in the
**originating session** adopts fully staged, surgical corrections on the same
source branch and PR. Pass the original issue's task, labels and milestone, plus
a `source` object with `runId`, original published `headSha` and `treeSha`,
`branch`, `prNumber`, `publicationCommentId`, `denialCommentId`, and selected
`paths` equal to the actual staged correction paths. Paths absent from the old
PR diff also require `scopeCommentId`: an exact-tree coordinator scope
confirmation on the same PR after the denial, naming the native run, PR, issue,
old head/tree, denial comment, staged tree, and every staged path as a separate
``- `relative/path` `` line. It is **not** admission authorization. Set
`reviewedTreeSha` only if the original pair reviewed a different tree and a
native correction stage produced the published tree. The route reads the
originating session's **terminal native run snapshot and run detail** to verify
the actual review pair, any corrective transition, publication, and issue
scope. A GitHub timeline claim by itself is never sufficient; missing or
ambiguous native evidence blocks. The owning workflow verifies this SDK
receipt before delegating affected validation; a subagent's separate session
cannot independently read the originating run. The same owner-verified source
receipt is forwarded to the corrected publisher, which must still check live
Git and GitHub state before committing or pushing; a child-session SDK lookup
failure is not a reason to discard the owner's native proof.

If a corrected publication itself stops **after** publishing (for example, a
publication-receipt format check rejects an otherwise valid comment), do not
alter the native journal, edit its timeline comment, or reuse the original
source arguments for a different staged tree. In the same originating session,
`mode: "correct"` also accepts `sourceChain` **instead of** `source`, with only
`runId` (the stopped corrective run), `parentRunId` (its original normal
delivery run), and `denialCommentId` (a fresh coordinator denial of the
corrective run's exact published head/tree). The owning workflow derives the
PR, head, tree, comment and allowed paths from both runs' terminal SDK
receipts, their typed agents, Git, and live GitHub; it accepts only a single
normal parent followed by a single failed post-publication correction, not an
arbitrary chain. The corrected publication must directly follow the original
published commit; an already-rebased correction blocks rather than claiming
unverified lineage. An additional `scopeCommentId` is required if newly staged
paths extend beyond the prior owned correction and PR diff. The old review
pair remains historical findings, never approval of the revision. Publication
reuses the same PR and still requires fresh CI and a new exclusive exact-head
coordinator grant before admission.

The original remote PR/head/tree and denial remain bound to that stopped run.
If `v1` advanced, the route commits only the staged owned corrections, rebases
the owned branch without stashing or discarding changes, and verifies the
original feature and correction deltas with zero-context verbatim patch IDs
before validating the resulting current-base tree. This permits unrelated
upstream context changes without accepting changes to the owned patches.
A conflict leaves work in place and blocks for resolution.
Otherwise it validates the staged tree directly. Historical pair results are
old-tree evidence, **not approval of the new tree**. Failed validation cannot
publish; successful validation pins the corrected tree. Publication reuses the
same owned PR with a fresh head/tree comment. New CI, an unchanged current
target tip, and an explicit exclusive coordinator grant for that new head
remain mandatory for admission. A withdrawn old grant is a denial, never a
fresh approval. A changed source, unrelated/unstaged files, material scope
changes, or stale base/head evidence block rather than authorize merge.
On replay the live PR must still link the source issue, and the fresh publication
comment must be on that PR by the source receipt's author. A failed pre-publication
validation can be retried with a newly staged owned tree; successful validation
pins the candidate, and publication cannot be silently replaced. If the target
tip advances after the coordinator grant, admission blocks and requires a fresh
grant on retry.
The admission owner may use a live, coordinator-authored top-level comment on
the same PR as the actual coordinator response when a child-session reply is
unavailable. Its comment ID/URL is the stable response ID, not a fabricated
message ID. Approval must be explicit on the first line, nonwithdrawn, and
bound to the coordinator session and decision ID, exact PR/head/tree/current
target base, and sole admission slot. A quoted or self-authored grant cannot
authorize admission; the separate merge executor still rechecks head, base,
CI and withdrawal before merging.

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
