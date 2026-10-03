# ADR 0001: Agentweaver 1.0 platform architecture

- **Status:** Proposed

## Context

Agentweaver 0.x ties its GitHub Copilot SDK runtime, agent-sandbox execution,
Azure Files workspace, Postgres memory, Agent Governance Toolkit (AGT) policy,
and GitHub integration to one API-centered deployment and a repository-wide
`VERSION`. Agentweaver 1.0 is a breaking, blank-slate rebuild: it ports behavior,
not code, while 0.x continues to ship on `dev`.

Paths cited here and in the parity map refer to 0.x code on the `dev` branch.
The coupling preventing a straightforward service split is concrete:

- Workflows, backlog, and coordinator code share `MemoryDbContext` transactions
  and directly access other modules' tables; see
  `apps/Agentweaver.Api/Workflows/WorkflowChildWorkService.cs`,
  `apps/Agentweaver.Api/Backlog/BacklogPromotionService.cs`, and
  `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs`.
- The API, worker, and AgentHost share one read-write-many workspace volume,
  not a per-run isolation boundary; see `k8s/base/api-deployment.yaml`,
  `k8s/base/worker-deployment.yaml`, and
  `k8s/base/sandbox-template-agenthost.yaml`.
- Process-local run options, approval gates, caches, and credentials cannot
  simply be shared across replicas; see `apps/Agentweaver.Api/Program.cs` and
  `apps/Agentweaver.Api/Sandbox/RunRepositoryCredentialRegistry.cs`.
- Release automation, image publishing, and runtime reporting use one
  repository `VERSION`; see `.github/workflows/publish-images.yml` and
  `apps/Agentweaver.Api/Infrastructure/AppVersionProvider.cs`.

The new boundaries make ownership, isolation, and delivery explicit rather
than moving those cross-module dependencies between processes.

## Goals and non-goals

**Goals**

- Define provider-neutral .NET contracts with Azure-first defaults, selectable
  backends, and explicit capability negotiation.
- Split coarse control-plane and data-plane services with independently
  versioned images, owned schemas, and durable cross-service communication.
- Make a run journal, session tree, messages, and knowledge records authoritative
  instead of repository files for coordination, decisions, and memory.
- Keep the coordinator thin: it proposes through typed tools; deterministic
  Microsoft Agent Framework (MAF) workflows validate and execute its plans.
- Unify live previews, durable previews, and published applications, and
  introduce typed surfaces within Agentweaver's own user experience.
- Track 0.x capabilities through an auditable parity map while 0.x stays active.

**Non-goals**

- Migrating existing 0.x installations or building migration tooling.
- A local runtime mode, local executors, or local stores.
- Copying the GitHub Copilot app UI or its dynamic script workflows; the
  design borrows only useful coordination primitives.
- User-level bring-your-own-key (BYOK); model settings come from the
  platform and project.
- Adapters requiring Google Cloud Storage (GCS) or Amazon S3 for durable data.

## Decision

| Topic | Decision |
| --- | --- |
| Delivery | Build on an orphan `v1` branch; use `dev` as a behavior reference. Port behavior, not code. |
| 0.x | Keep releases active; track each post-fork change in the parity map without blocking 0.x releases. |
| Runtime | Run on Azure Kubernetes Service (AKS) with Azure-first services. Use fakes and Testcontainers Postgres for tests, then exact-SHA AKS deployments and persona harnesses for integration. |
| Contracts | Version canonical .NET dependency-injection contracts in `Agentweaver.Abstractions`; keep adapters in-repo, wrapping HTTP, gRPC, or Kubernetes custom resources where needed. |
| Selection | Resolve platform defaults and allowed project overrides; pin effective bindings per run, with seam-specific cardinality. |
| Harness and model | Use the Copilot SDK as the only harness. Keep Copilot/BYOK model choice in the existing resolver, not a new provider seam. |
| Provider seams | Sessions, Snapshots, Sandbox, Storage, Memory, Policy, Guardrails, Network Policy, Cost, Application Hosting, Secrets, Source Control, Telemetry, Messaging, Object Store: 15 total. |
| Not seams | Model, Context, Tools, Skills, Model Context Protocol (MCP) Servers, Image Registry, State Store (Postgres), and Surfaces are configuration, protocols, or core logic. |
| Core domains | Coordination (sessions and messages), Orchestration (MAF workflows and work planning), and Applications & surfaces remain product logic. |
| UX | Keep Agentweaver's run page, topology, approvals, and chat; add a canvas-like surface panel without adopting the Copilot app's UI. |
| Orchestration | Require step catalogs and step-snapped WorkPlans; typed coordinator proposals undergo schema, policy, and workflow validation. |
| Applications | One application model has `live`, `preview`, and `published` stages; Application Hosting serves durable stages. |
| Surfaces | Render application, Agent-to-User Interface (A2UI), MCP App, and built-in surfaces; agents use typed `surface_*` tools. |
| Neutrality | Use Agentweaver vocabulary in contracts. Include a concept only when two providers need it; Azure is a default, not a contractual assumption. |
| Services and release | Use roughly ten coarse services, owned Postgres schemas, gRPC/HTTP, and an outbox. Release per-service semver versions as one tested manifest with N-1 compatibility (the preceding contract version). |

### Architecture at a glance

The layers keep core product decisions above provider contracts and external systems.

```mermaid
flowchart LR
    Clients["Web, CLI, and MCP clients"] --> Core["Core services: identity, projects, orchestration, coordination, applications"]
    Core --> Contracts["Agentweaver.Abstractions: .NET contracts"]
    Contracts --> Adapters["In-repo provider adapters"]
    Adapters --> Systems["AKS, Azure services, GitHub, and other enabled systems"]
```

The **control plane** owns identity, desired state, policy decisions, and
durable workflow transitions:

- Gateway/backend for frontend (BFF) provides authenticated REST and live
  events; Identity brokers sign-in, OAuth, and purpose-bound credentials.
- Projects & Config owns projects, casting, skills, provider bindings, model
  settings, egress narrowing, and budget settings.
- Orchestrator owns runs, MAF workflows, the session tree, coordination verbs,
  typed decisions, gates, checkpoints, and recovery.
- Environment manager owns sandbox and storage lifecycle, networking,
  application hosting, previews, and control-plane image publication.
- Source Control & Merge owns repository preparation, pull requests, webhooks,
  and merge locks. Knowledge owns memory, decisions, and prompt composition.
- Events & Sessions owns the run journal, message delivery, usage ledger,
  and replay streams. The MCP server and web frontend expose these domains
  through Agentweaver's own user experience.

The **data plane** runs the GitHub Copilot SDK AgentHost **inside** a Sandbox
environment, with execution tools and core enforcement gates. A Tool & MCP
gateway mediates outbound agent traffic; an app router exposes authenticated
routes. Hosted applications live outside AgentHost environments, while
provider sidecars and services supply optional implementations.

A run resolves and pins provider/model bindings, provisions an environment,
applies and verifies egress intent, attaches a workspace volume, and composes
context. Turns cross core policy/tool gates. The journal and usage ledger record
outcomes before workflow gates advance build/test, review, publish, or merge.
Services coordinate through idempotent commands, fencing, and at-least-once
outbox events rather than shared database transactions.

### Provider seams

A **provider seam** is a .NET contract between core behavior and a selectable
implementation. Selection can be exclusive, ordered, platform-wide, layered,
keyed by meter source, or per application. Pin implementation identity and
compatible capabilities, never credentials; fail if a pinned provider disappears.
Core gates enforce authorization and AGT policy regardless of adapter choice.

This map groups all 15 seams by primary owning service; it also shows core
domains and the categories reviewed but not made into provider seams.

```mermaid
flowchart TB
    subgraph Domains["Core domains"]
        Coordination["Coordination"]
        Orchestration["Orchestration"]
        Applications["Applications and surfaces"]
    end
    subgraph Environment["Environment manager"]
        Sandbox["Sandbox: agent-sandbox"]
        Snapshots["Snapshots: none until gated"]
        Storage["Storage: Azure Files and Elastic SAN"]
        Network["Network Policy: Cilium"]
        Hosting["Application Hosting: AKS and A2UI shell"]
    end
    subgraph Engine["Orchestrator and core enforcement"]
        Policy["Policy: AGT"]
        Guardrails["Guardrails: Prompt Shields"]
    end
    subgraph Identity["Identity"]
        Secrets["Secrets: Azure Key Vault"]
    end
    subgraph Knowledge["Knowledge"]
        Memory["Memory: native Postgres"]
    end
    subgraph Events["Events and Sessions"]
        Sessions["Sessions: native Postgres journal"]
        Cost["Cost: Copilot credits"]
        Messaging["Messaging: Postgres outbox"]
    end
    subgraph Source["Source Control and Merge"]
        SourceControl["Source Control: GitHub"]
    end
    subgraph Platform["Platform-wide infrastructure"]
        Telemetry["Telemetry: OpenTelemetry to Azure Monitor"]
        Objects["Object Store: Azure Blob"]
    end
    subgraph Reviewed["Reviewed, not seams"]
        Config["Model, Context, Tools, Skills"]
        Protocols["MCP Servers, Image Registry, State Store, Surfaces"]
    end
    Coordination --> Sessions
    Coordination --> Messaging
    Orchestration --> Policy
    Orchestration --> Sandbox
    Applications --> Hosting
```

| Seam | Primary owner | Azure-first default | Later or additional adapters |
| --- | --- | --- | --- |
| Sessions | Events & Sessions | Native Postgres journal | agentsessions capture sidecar (P3) |
| Snapshots | Environment manager | None until gated Azure Blob-backed AKS pod snapshot/restore capability | Compatible sandbox-native snapshots |
| Sandbox | Environment manager | agent-sandbox on AKS | OpenSandbox; Agent Substrate after AKS proof |
| Storage | Environment manager | Azure Files | Elastic SAN for environment volumes (P2); filesystem providers for agents when ready |
| Memory | Knowledge | Native Postgres | Cosmos memory service (P3) |
| Policy | Orchestrator/core enforcement | AGT .NET kernel with YAML rules | No second decision kernel planned |
| Guardrails | Orchestrator/core enforcement | Azure AI Content Safety Prompt Shields (P2) | Purview DLP; other classifiers |
| Network Policy | Environment manager | Cilium L3/L4; Agentweaver Tool & MCP gateway for L7 | Kubernetes NetworkPolicy where applicable; agentgateway (P3) |
| Cost | Events & Sessions | Copilot AI-credit pricing | Azure BYOK (P2); priced third-party meter sources |
| Application Hosting | Environment manager | Built-in AKS web runtime; Agentweaver A2UI shell (P2) | Image-backed and owner-managed providers if shipped in 0.x; Azure Container Apps evaluation (P3) |
| Secrets | Identity | Azure Key Vault with workload identity | None planned |
| Source Control | Source Control & Merge | GitHub | Other source-control adapters if justified |
| Telemetry | Platform-wide | OpenTelemetry to Azure Monitor | Other OpenTelemetry Protocol (OTLP) exporters |
| Messaging | Events & Sessions | Postgres transactional outbox and relay | Azure Service Bus |
| Object Store | Platform-wide | Azure Blob | No GCS- or S3-backed adapters |

The reviewed non-seams are **Model**, **Context**, **Tools**, **Skills**,
**MCP Servers**, **Image Registry**, **State Store**, and **Surfaces**. MCP
already defines a plug-in protocol; Postgres remains the fixed transactional
state store. The [provider-seam design](../design/provider-seams.md) defines
cardinality, contracts, pinning, conformance, and enforcement in detail.

## Design documents

| Document | Scope |
| --- | --- |
| [Provider seams](../design/provider-seams.md) | Provider contracts, 15 seams, adapter selection, capabilities, security invariants, and conformance. |
| [Sessions and coordination](../design/sessions-and-coordination.md) | Journal and Copilot session state, suspend/resume consistency, nested sessions, messages, and knowledge records. |
| [Orchestration](../design/orchestration.md) | Thin coordinator, typed decisions, step catalogs, step-snapped plans, and deterministic MAF gates. |
| [Applications and surfaces](../design/applications-and-surfaces.md) | The `live`/`preview`/`published` application lifecycle, hosting, and surface panel. |
| [Services and release](../design/services-and-release.md) | Control/data-plane decomposition, owned schemas, versioning, manifests, and delivery. |

## Delivery strategy

### Port behavior, not code

For each capability, start with its 0.x specification and observable behavior,
then define a 1.0 contract, conformance tests, and an implementation. Copy
only small, reviewed pieces when warranted; do not add shims, dual paths,
or 0.x implementation names to new contracts. The parity map tracks each
0.x capability and open roadmap item, its 1.0 owner, disposition (rebuild,
redesign, drop, or defer), and evidence.

The verified behavior guarantees in
[#1405](https://github.com/sabbour/agentweaver/issues/1405) become the
acceptance list **when published**; that document does not exist yet.
The appendix seeds the map, not a claim that parity is complete.

### Keep 0.x active

0.x continues on `dev` while 1.0 grows on `v1`. Add a field to the 0.x
pull-request template asking for “1.0 parity row: id / n.a.”, and have Squad
sweep changes when each 0.x milestone closes. This is a team process, **not**
a GitHub required check, and never delays a 0.x release. Shipped work updates
parity rows; open work at cutover moves to the owning 1.0 backlog.

### Cut over on evidence

The gate is a complete parity map plus green API, UI, and MCP persona harnesses
against the same exact-SHA AKS deployment. Start harness runs at the end of P1,
not at cutover. Once parity is complete, set a cut line: later 0.x changes go
into the 1.0 backlog rather than moving the gate forever ([R22](#risk-register)).

Tag the final 0.x release on `dev` and keep `release/0.x` for patches. Preserve
both histories without a force push: merge old `dev` into `v1` with
`--allow-unrelated-histories -s ours`, then fast-forward `dev` to `v1`
([R21](#risk-register)). 1.0 is a fresh installation, not a data migration.

The timeline shows active 0.x milestones feeding parity alongside 1.0 phases.
These milestones are roadmap inputs, not prerequisites that all must ship.

```mermaid
flowchart LR
    subgraph Legacy["Active 0.x on dev"]
        Fork["Fork to orphan v1"] --> V35["v0.35"] --> V36["v0.36"] --> V37["v0.37"] --> V38["v0.38"] --> V39["v0.39"]
    end
    subgraph New["1.0 on v1"]
        P0["P0 foundation"] --> P1["P1 core"] --> P2["P2 parity"]
    end
    Fork --> P0
    V35 --> Parity["Every post-fork change updates parity map"]
    V36 --> Parity
    V37 --> Parity
    V38 --> Parity
    V39 --> Parity
    P0 --> Release["Per-service semver and platform release manifest"]
    Release --> Contracts["N-1 contract tests"]
    Parity --> CutLine["Parity complete: declare cut line"]
    CutLine --> Gate["Exact-SHA AKS API, UI, MCP harnesses green"]
    P2 --> Gate
    Contracts --> Gate
    Gate --> Tag["Final 0.x tag"]
    Tag --> Cutover["Preserve histories and fast-forward dev"]
    Cutover --> P3["P3 later adapters"]
```

## Roadmap alignment

Shipped 0.x issues create parity obligations. Items still open at the cut
line enter the 1.0 backlog under the listed owner.

| 0.x milestone | Issues | 1.0 owner and treatment |
| --- | --- | --- |
| v0.35 | Remote MCP [#1229](https://github.com/sabbour/agentweaver/issues/1229) ([#1230](https://github.com/sabbour/agentweaver/issues/1230), [#1231](https://github.com/sabbour/agentweaver/issues/1231), [#1232](https://github.com/sabbour/agentweaver/issues/1232), [#1233](https://github.com/sabbour/agentweaver/issues/1233), [#1234](https://github.com/sabbour/agentweaver/issues/1234), [#1235](https://github.com/sabbour/agentweaver/issues/1235)); MCP surface [#1407](https://github.com/sabbour/agentweaver/issues/1407), [#1427](https://github.com/sabbour/agentweaver/issues/1427), [#1428](https://github.com/sabbour/agentweaver/issues/1428) | MCP catalog and Tool & MCP gateway ([Provider seams](../design/provider-seams.md)). |
| v0.35 | Tracked addressed messages [#1406](https://github.com/sabbour/agentweaver/issues/1406); run state and recovery effects [#1402](https://github.com/sabbour/agentweaver/issues/1402) | Message primitive and status snapshot ([Sessions and coordination](../design/sessions-and-coordination.md)). |
| v0.35 | Preview retention and abandoned-sandbox reclaim [#1688](https://github.com/sabbour/agentweaver/issues/1688) | Sandbox lifecycle and `live`-to-`preview` capture ([Provider seams](../design/provider-seams.md), [Applications](../design/applications-and-surfaces.md)). |
| v0.35 | Workflow applications [#662](https://github.com/sabbour/agentweaver/issues/662), canvas/A2UI renderer [#665](https://github.com/sabbour/agentweaver/issues/665), authorization [#668](https://github.com/sabbour/agentweaver/issues/668) | Applications and surfaces, including web and A2UI profiles. |
| v0.35 | Verified behavior guarantees [#1405](https://github.com/sabbour/agentweaver/issues/1405) | Parity-map acceptance list once published. |
| v0.36 | Image-backed [#666](https://github.com/sabbour/agentweaver/issues/666) and owner-managed [#667](https://github.com/sabbour/agentweaver/issues/667) app providers; image publication [#761](https://github.com/sabbour/agentweaver/issues/761) | Application Hosting adapters and control-plane registry publication. |
| v0.37 | Suspend/resume [#1410](https://github.com/sabbour/agentweaver/issues/1410); AgentHost startup [#1257](https://github.com/sabbour/agentweaver/issues/1257) | Snapshots and consistency manifest; Sandbox startup phases and size budget. |
| v0.38 | Delivery simplification [#1429](https://github.com/sabbour/agentweaver/issues/1429), [#1430](https://github.com/sabbour/agentweaver/issues/1430) | Fresh 1.0 release tooling ([Services and release](../design/services-and-release.md)). |
| v0.39 | Project-owned durable previews [#1494](https://github.com/sabbour/agentweaver/issues/1494) | Application `preview` stage. |

## Phases

**Progress (2026-10-02):** Merged foundations below are libraries and validation
tooling, not a running or released platform. The workload-identity composition
from [#1771](https://github.com/sabbour/agentweaver/pull/1771) (issue
[#1766](https://github.com/sabbour/agentweaver/issues/1766)) is merged, but
deployed AKS workload identity and Identity authorization remain unverified.
The phase descriptions below remain the scope authority.

| Phase | Status and evidence | Remaining before phase completion |
| --- | --- | --- |
| P0 — Foundation | **In progress.** Merged provider descriptors/resolution/pinning [#1735](https://github.com/sabbour/agentweaver/pull/1735), Postgres schemas/outbox [#1738](https://github.com/sabbour/agentweaver/pull/1738), coverage [#1741](https://github.com/sabbour/agentweaver/pull/1741), Secrets/Key Vault [#1752](https://github.com/sabbour/agentweaver/pull/1752)/[#1756](https://github.com/sabbour/agentweaver/pull/1756), OpenTelemetry/Azure Monitor [#1753](https://github.com/sabbour/agentweaver/pull/1753)/[#1758](https://github.com/sabbour/agentweaver/pull/1758), Blob [#1760](https://github.com/sabbour/agentweaver/pull/1760)/[#1763](https://github.com/sabbour/agentweaver/pull/1763), consumer inbox [#1772](https://github.com/sabbour/agentweaver/pull/1772) (issue [#1768](https://github.com/sabbour/agentweaver/issues/1768)), ordered/layered provider composition [#1770](https://github.com/sabbour/agentweaver/pull/1770) (issue [#1767](https://github.com/sabbour/agentweaver/issues/1767)), and AKS workload-identity host composition [#1771](https://github.com/sabbour/agentweaver/pull/1771) (issue [#1766](https://github.com/sabbour/agentweaver/issues/1766)). | Identity service authorization/redemption and broker; service wiring/runtime/cloud layout; deployment and per-service compatibility proof; dedicated Azure integration environment with exact-SHA evidence. Release-hygiene repair [#1773](https://github.com/sabbour/agentweaver/issues/1773) is in progress; if admitted it will supply changeset tracking, not a platform release. The manifest is still a draft. |
| P1 — Core and defaults | **Not delivered.** The project issue workflow [#1743](https://github.com/sabbour/agentweaver/pull/1743) and workflow canvas [#1755](https://github.com/sabbour/agentweaver/pull/1755)/[#1757](https://github.com/sabbour/agentweaver/pull/1757)/[#1762](https://github.com/sabbour/agentweaver/pull/1762) are delivery tooling, not product P1. | Core services, default adapters, and exact-SHA AKS harness journeys described below. |
| P2 — Parity and cutover | **Not delivered.** | Parity, application/surface capabilities, acceptance and cutover described below. |
| P3 — After cutover | **Deferred and optional.** | Evaluate gated adapters only after cutover; none blocks it. |

- **P0 — Foundation.** Establish cloud-only layout and CI; a platform release
  manifest, per-service Postgres schemas and outbox, Azure Blob Object Store,
  Identity and Azure Key Vault with workload identity, OpenTelemetry to Azure
  Monitor, provider resolution, pinning, and conformance tests.
- **P1 — Core and defaults.** Build Orchestrator step catalogs, typed decisions,
  run limits, checkpoints, gates; the journal, session tree, tracked messages,
  status snapshot, Knowledge records, Projects & Config, Gateway/BFF, web, MCP
  server, and budgeted AgentHost startup. Supply native Sessions/Memory,
  agent-sandbox with retention/reclaim, Azure Files, Cilium egress intent,
  AGT, GitHub, and Copilot cost. Start exact-SHA AKS harness runs at the end of P1.
- **P2 — Parity and cutover.** Add suspend/resume with a consistency manifest,
  Guardrails and tool permission metadata, remote MCP catalog and gateway,
  Elastic SAN, Azure BYOK cost and budgets, prompt ordering and cache telemetry,
  Squad import/export, and `live`/`preview`/`published` applications. Add
  image publication, publish gate, and application/A2UI/MCP App/built-in
  surfaces. Include image-backed and owner-managed app adapters if shipped
  in 0.x. Close parity, pass API/UI/MCP harnesses, and cut over.
- **P3 — After cutover.** Consider gated AKS pod snapshot/restore, OpenSandbox,
  Agent Substrate, Cosmos memory, agentsessions, filesystem providers for agents,
  agentgateway, Azure Container Apps hosting, and Agentweaver surfaces exposed
  as MCP Apps. None of these optional adapters blocks cutover.

## Risk register

**Type:** **Decided** fixes a design choice now; **Gated** means adoption only
after the stated evidence, with no cutover dependency; **Mitigated** means
the design contains the failure mode.

| ID | Risk | Type | Resolution |
| --- | --- | --- | --- |
| R1 | agentsessions adds little while the Copilot SDK owns calls. | Decided | P3 capture-only mirror of the journal with hash verification and playback; no re-execution promise. Drop it if the native journal suffices. |
| R2 | Filesystem providers for agents are pre-product. | Decided | Keep them off the 1.0 path; use Azure Files and Elastic SAN behind the volume contract and negotiate optional capabilities later. |
| R3 | AKS pod snapshot/restore is pre-preview; Blob end-to-end, Kata virtual machine (VM) v2, node pinning, warm-pool restore, and latency are unproven. | Gated | Suspend works without snapshots. Adopt only after public preview, Azure Blob end-to-end success, warm-pool claim restore, and capture/restore within the AgentHost startup budget; switch to Kata v2 then. |
| R4 | Pod snapshots exclude workspace volume data. | Mitigated | Record and validate storage generation separately in a consistency manifest. |
| R5 | User-level BYOK expands the credential boundary. | Decided | Keep platform and project settings only in 1.0. |
| R6 | AGT 5.0 policy language support lacks .NET parity. | Decided | Use the AGT .NET kernel with YAML rules as in 0.x; consider Rego/Cedar later without changing contracts. |
| R7 | AgentMemoryToolkit and agentsessions are previews. | Decided | Native implementations are defaults; optional adapters wait until P3. |
| R8 | Agent Substrate has beta APIs, uncertain AKS viability, and no Blob snapshot store. | Gated | P3 conformance and an AKS proof of VM-level isolation ([#553](https://github.com/sabbour/agentweaver/issues/553)); leave snapshots off until Azure Blob support. |
| R9 | Messages cross service boundaries and may duplicate. | Decided | At-least-once outbox, idempotency keys, consumer deduplication, per-thread sequence numbers. |
| R10 | Operator chat may need to spawn runs. | Decided | Permit spawn under run-start authorization; record the run as the chat's child. |
| R11 | Per-run workflow generation is needed for parity. | Decided | Retain it with grammar/step-catalog validation and human confirmation before first use. |
| R12 | WorkPlans change mid-run. | Decided | Permit bounded revisions within steps; confirm additions/removals of steps or wider scope. |
| R13 | Strict step snapping can reject exploratory work. | Decided | Reject unmapped work unless an open step permits it; built-in workflows include an open implementation step. |
| R14 | A proposed agent volume API may change. | Mitigated | Use Agentweaver vocabulary in contracts; let the adapter absorb provider renames. |
| R15 | Elastic SAN is read-write-once only. | Decided | Use it only for `environment` volumes; use Azure Files for shared volumes. |
| R16 | Cilium cannot guarantee FQDN-only HTTPS egress to public IPs. | Mitigated | Route model, MCP, and agent-to-agent traffic through the L7 gateway; document remaining limits. |
| R17 | agentgateway per-environment policy is not the default. | Decided | Default to Agentweaver's own Tool & MCP gateway; consider agentgateway in P3. |
| R18 | Copilot AI-credit pricing changes. | Mitigated | Version the rate card and persist its version with each cost record. |
| R19 | Azure price lag, provisioned throughput allocation, and reconciliation trail budgets. | Decided | Enforce on estimates; reconciliation corrects reports but never retroactively stops runs. |
| R20 | The new branch needs a stable name. | Decided | Use orphan `v1`. |
| R21 | Cutover could discard 0.x history or require a force push. | Decided | Tag final 0.x on `dev`, keep `release/0.x` for patches, merge old `dev` into `v1` with `--allow-unrelated-histories -s ours`, and fast-forward `dev` to `v1`. |
| R22 | An active 0.x line keeps moving the parity target. | Decided | Set a cut line once the map is complete; later changes enter the 1.0 backlog rather than blocking cutover. |
| R23 | Persona harness coverage could arrive too late. | Decided | Run API/UI/MCP harnesses on exact-SHA AKS deployments from the end of P1; require all green at cutover. |
| R24 | Container Apps, viewer auth, and app-provider timing need ownership. | Decided | Evaluate Container Apps in P3; authenticate viewers at Gateway/Identity under [#668](https://github.com/sabbour/agentweaver/issues/668), never at a provider; [#666](https://github.com/sabbour/agentweaver/issues/666)/[#667](https://github.com/sabbour/agentweaver/issues/667) follow parity. |
| R25 | A2UI 1.0 is unfinished and the Copilot canvas API is not a standard. | Mitigated | Pin renderer spec version (v0.9.1 stable, v1.0 when final) and component catalog; MCP Apps handles custom UI; Agentweaver's own surface actions use MCP. |

## Consequences

- **Easier:** Backends can run side by side; conformance tests make compatibility
  visible. A journal and messages make coordination and audit inspectable
  without repository-file merges.
- **Harder:** Service boundaries, pinned capabilities, fencing, outbox
  delivery, and snapshot/volume consistency add operational work. Per-service
  releases require compatibility tests and a tested platform manifest.
- **Required:** Each port has behavioral evidence and an owner in the parity
  map. Every coordinator “must” or “never” rule needs a code enforcement point
  and test. Cutover needs parity and exact-SHA API/UI/MCP harness evidence.
- **Out of scope:** Migration tooling, local runtime, user-level BYOK, a copied
  Copilot app UI, and GCS- or S3-backed durable adapters.

## Appendix: Parity map (seed)

These rows seed, rather than close, the parity map. Evidence is from 0.x `dev`.
The linked area is accountable for each rebuild, redesign, drop, or deferral.
When the guarantees from
[#1405](https://github.com/sabbour/agentweaver/issues/1405) are published,
they supply the acceptance list.

| ID | Capability | 0.x evidence | 1.0 owner | Disposition |
| --- | --- | --- | --- | --- |
| identity-sign-in | Sign in and carry identity | `specs/identity-access/sign-in-and-carry-identity.md`; `apps/Agentweaver.Api/Auth/` | [Services and release](../design/services-and-release.md) | Rebuild |
| mcp-authorization | Authorize MCP clients for a user | `specs/identity-access/authorize-mcp-clients.md`; `apps/Agentweaver.Mcp/` | [Services and release](../design/services-and-release.md) | Rebuild |
| platform-onboarding | Setup and product tour | `specs/identity-access/complete-platform-setup-and-tour.md` | [Services and release](../design/services-and-release.md) (web) | Redesign |
| project-lifecycle | Create, configure, and recover projects | `specs/projects-workspace/`; `apps/Agentweaver.Api/Projects/` | [Services and release](../design/services-and-release.md) | Rebuild |
| workspace-browse | Browse project/run workspaces read-only | `specs/projects-workspace/browse-project-and-run-workspaces.md` | [Provider seams](../design/provider-seams.md) (Storage) | Redesign |
| team-casting / agent-roster | Cast teams, manage roles and charters | `specs/squad-casting/`; `apps/Agentweaver.Api/Casting/` | [Sessions and coordination](../design/sessions-and-coordination.md) | Rebuild |
| backlog / board | Capture and track work | `specs/work-intake-board/`; `apps/Agentweaver.Api/Backlog/` | [Orchestration](../design/orchestration.md) | Rebuild |
| github-issue-sync | Sync GitHub issues into backlog | `specs/work-intake-board/sync-github-issues-to-backlog.md` | [Provider seams](../design/provider-seams.md) (Source Control) | Rebuild |
| workflow-library / triggers / scheduling | Define, trigger, and schedule workflows | `specs/workflows-automation/`; `apps/Agentweaver.Api/Workflows/` | [Orchestration](../design/orchestration.md) | Rebuild |
| workflow-generation | Generate and edit workflows | `specs/workflows-automation/generate-and-save-workflows.md` | [Orchestration](../design/orchestration.md) | Redesign: validate step catalogs |
| workflow-authoring UI | Visually author, run, and schedule workflows | `specs/workflows-automation/run-schedule-and-visually-author-workflows.md` | [Orchestration](../design/orchestration.md) (web) | Redesign |
| durable-static-branches | Execute durable static branches | `specs/workflows-automation/execute-static-workflow-branches-durably.md` | [Orchestration](../design/orchestration.md) | Rebuild |
| dynamic-work-plan | Embed dynamic coordinator work inside workflows | `specs/workflows-automation/embed-dynamic-coordinator-work-plan.md` | [Orchestration](../design/orchestration.md) | Redesign: snap to steps |
| workflow-actions | Open and push pull requests as workflow actions | `specs/workflows-automation/open-pull-request-action.md` | [Provider seams](../design/provider-seams.md) (Source Control) | Rebuild |
| immutable-output | Pin reviewed output by revision | `specs/workflows-automation/pin-reviewed-output-by-revision.md` | [Orchestration](../design/orchestration.md) | Rebuild |
| single-agent-run / multi-agent-coordination | Run tasks and coordinate goals | `specs/orchestration-runs/`; `apps/Agentweaver.Api/Runs/` | [Orchestration](../design/orchestration.md) | Rebuild |
| run-steering | Steer, pause, recover, and resume runs | `specs/orchestration-runs/steer-and-recover-orchestrations.md` | [Sessions and coordination](../design/sessions-and-coordination.md) | Redesign: messages |
| agent-messaging | Send addressed messages; tracked acknowledgment [#1406](https://github.com/sabbour/agentweaver/issues/1406) | `specs/orchestration-runs/send-addressed-agent-messages.md`; `apps/Agentweaver.Api/Memory/AddressedMessageService.cs` | [Sessions and coordination](../design/sessions-and-coordination.md) | Rebuild |
| review / run-decision / child-output-integration | Review and integrate child output | `specs/review-merge/` | [Orchestration](../design/orchestration.md) | Rebuild |
| tool-governance | Approve tools and answer questions | `specs/agent-execution-sandbox/govern-agent-tools-and-questions.md` | [Provider seams](../design/provider-seams.md) (Policy) + [Sessions and coordination](../design/sessions-and-coordination.md) | Rebuild |
| sandbox-isolation / effective-permissions | Isolate execution and enforce permissions | `specs/agent-execution-sandbox/` | [Provider seams](../design/provider-seams.md) (Sandbox, Policy) | Rebuild |
| sandbox-preview | Show live previews | `specs/agent-execution-sandbox/preview-sandbox-apps.md` | [Applications and surfaces](../design/applications-and-surfaces.md) (`live`) | Redesign |
| preview-deployments | Durable project-owned previews [#1494](https://github.com/sabbour/agentweaver/issues/1494) | `specs/agent-execution-sandbox/project-owned-preview-deployments.md` | [Applications and surfaces](../design/applications-and-surfaces.md) (`preview`) | Redesign |
| image-builds | Build Open Container Initiative (OCI) images with rootless BuildKit | `specs/agent-execution-sandbox/build-images-with-rootless-buildkit.md` | [Services and release](../design/services-and-release.md) | Rebuild |
| agent-skills | Assign, import, sync, and browse skills | `specs/agent-configuration/`; `apps/Agentweaver.Api/Skills/` | [Provider seams](../design/provider-seams.md) (configuration, not a seam) | Rebuild |
| decision-inbox | Curate a file-backed decision inbox | `specs/memory-decisions/curate-decision-inbox.md` | [Sessions and coordination](../design/sessions-and-coordination.md) | Drop: knowledge records and messages replace it |
| repository-memory-sync | Sync memory ledgers with repository files | `specs/memory-decisions/sync-memory-ledgers.md` | [Sessions and coordination](../design/sessions-and-coordination.md) | Drop |
| agent-memory / knowledge-history | Store and restore agent knowledge | `specs/memory-decisions/` | [Sessions and coordination](../design/sessions-and-coordination.md) + [Provider seams](../design/provider-seams.md) (Memory) | Redesign |
| live-events | Watch and replay run events | `specs/observability-operations/watch-run-events-live.md` | [Sessions and coordination](../design/sessions-and-coordination.md) | Rebuild |
| lineage | Inspect execution and decision lineage | `specs/observability-operations/inspect-execution-identity.md` | [Sessions and coordination](../design/sessions-and-coordination.md) | Rebuild |
| usage-metering | Track tokens and AI-credit usage | `specs/observability-operations/track-token-and-cost-usage.md` | [Provider seams](../design/provider-seams.md) (Cost) | Redesign: durable ledger |
| health-operations | Inspect health, heartbeat, and cluster | `specs/observability-operations/operate-health-heartbeat-and-cluster.md` | [Services and release](../design/services-and-release.md) | Rebuild |
| context-pressure | Accept context-budget pressure | `specs/runtime-resilience/context-budget-pressure-acceptance.md` | [Orchestration](../design/orchestration.md) | Rebuild |
| mcp-control / project-copilot / browser-control | Control Agentweaver through MCP and browser chat | `specs/mcp-integrations/`; `apps/Agentweaver.Mcp/` | [Services and release](../design/services-and-release.md) (MCP server) | Rebuild / redesign |
| github-app | Connect GitHub App capabilities | `specs/mcp-integrations/connect-github-app-capabilities.md` | [Provider seams](../design/provider-seams.md) (Source Control) | Rebuild |
| unattended-settings | Manage unattended settings and diagnostics | `specs/mcp-integrations/unattended-project-settings-and-diagnostics.md` | [Services and release](../design/services-and-release.md) | Rebuild |
| cloud-deployment | Deploy and operate on AKS/Azure | `specs/deployment-platform/self-host-agentweaver.md` | [Services and release](../design/services-and-release.md) | Redesign: fresh tooling |
| local-runtime | Local executors, stores, and database modes | `packages/Agentweaver.SandboxExec/`; `apps/Agentweaver.Api/Infrastructure/` | [Services and release](../design/services-and-release.md) | Drop |
| docs-diagrams | Render and author documentation diagrams | `specs/deployment-platform/render-fluent-docs-diagrams.md` | Docs tooling (deferred; [Services and release](../design/services-and-release.md) is the delivery context) | Defer |
