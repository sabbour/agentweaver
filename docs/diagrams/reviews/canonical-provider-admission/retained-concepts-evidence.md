# Retained-text independent completion evidence

Exactly **66** results: **57 completed**, **9 blocked consumer/ownership handoffs**.

Independent retained-text claim/disposition verification and authorized prose work complete. Explicit parent validation/publication responsibilities and normative product gaps are not represented as passed.

## Scope and authority

Completed area audits and reconciliation used as disposition contract; claims independently traced to current implementation/config/tests/current written docs. No visual labels used as ground truth.

Contract: `plan-shared.json`, the five corresponding completed area audit reports, and `reconciliation.json` under `.github/skills/docs-diagram-audit/reports/`. All remediated/promoted lineages are outside this retained-text task; no old lineage blocker is asserted.

17 already-corrected status records and 13 diagram-gated records unchanged; no global audit/inventory/reconciliation/plan, diagram source/PNG/hash, product, pipeline, skill or foreign-page writes.

**No diagram embeds or provenance comments were changed.** No new diagrams, nested agents, staging, commits, dependency installations, deployment calls, or live persona runs.

## Changed pages

- `CONTRIBUTING.md`
- `RELEASING.md`
- `docs/api-test-harness-plan.md`
- `docs/architecture/decisions/README.md`
- `docs/catalog-groupings.md`
- `docs/design/285-prd-story-promotion.md`
- `docs/e2e-harness-plan.md`
- `docs/index.md`
- `docs/mcp-oauth.md`
- `docs/mcp-test-harness-plan.md`
- `docs/public/agents/agentweaver.agent.md`
- `docs/tool-approval-sse-contract.md`
- `docs/ui-test-harness-plan.md`
- `docs/workflow-binder.md`
- `docs/workflow-generation.md`

## Validation

- Focused Node schema/judge/network/auth/release-tag tests: **35 passed, 0 failed**; `retained-focused-tests.log`.

- Focused .NET no-build invocation returned zero with **no test output** and no test bin directory. **Not a pass.** Parent must restore/build and run the focused filters listed in the JSON report; `retained-dotnet-tests.log` is intentionally empty.

- Parent owns final docs build and global publication validation. Screenshot selection/copy descriptions are checked, not screenshot pixels, export byte identity or live deployment freshness.

## Exact blocked consumer gaps

- **deep-dive-core-persistence-memory-domain** — Foreign consumer docs/deep-dive/data-persistence.md: under Memory and Orchestration Store, make this the shared schema-oriented concept owner using a compact table/inline model of the constraints above. Link the focused governance ER in docs/deep-dive/memory-decisions.md back to this definition. Its current core-invariants prose is correct, but the reconciled shared model ownership/link is not enacted. No new diagram name/assets authorized.
- **deep-dive-core-provider-admission** — Foreign consumer docs/deep-dive/api-core.md: Effective model-provider context describes Expected and persisted provenance but omits the complete execution_key/If-Model-Provider-Key acceptance and pre-call fence. Add these steps and reuse canonical-provider-admission (already promoted by separate writer); parent owns embed/provenance. No lineage blocker is asserted.
- **deep-dive-orchestration-memory-entities** — Foreign consumer docs/deep-dive/memory-decisions.md: keep its focused inline ER, but label it as a view of deep-dive-core-persistence-memory-domain and link to data-persistence.md#memory-and-orchestration-store-memory-db. Do not create a second ER asset. Coordinate with the data-persistence owner to establish the anchor/model once.
- **experience-decision-inbox-state** — Foreign consumer docs/experience/agent-communication.md: annotate Proposed=pending, Finalized=merged inbox plus linked decision, Rejected=rejected; link the sketch explicitly to memory-decisions.md#the-inbox-to-promotion-model. Preserve current owner/Coordinator and active-approved eligibility prose. Do not add a separate state PNG.
- **reference-provider-context-lifecycle** — Foreign consumer docs/reference/api.md#ai-execution-context: add acceptance/snapshot/pre-call fence and redacted reprepare/retry semantics, distinguish the display provider fingerprint from execution authority, and reuse canonical-provider-admission rather than an API-only or MCP-only diagram. Parent owns canonical embed/provenance and reference-page handoff.
- **shared-public-agent-preview-and-context** — Generated-consumer handoff: the allowed docs/public/agents/agentweaver.agent.md prose is corrected, but scripts/gen-docs.mjs derives the whole downloadable file from .github/agents/agentweaver.agent.md and also writes apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md and apps/Agentweaver.Web/wwwroot/agents/agentweaver.agent.md. Those three paths are outside plan-shared document_paths. An authorized owner must apply the same prose correction to the source and regenerate/synchronize the mirrors; this worker did not edit them or run that generator. Required source change: remove code-review from Software Development snapshot.
- **shared-public-agent-run-journey** — Generated-consumer handoff: the allowed docs/public/agents/agentweaver.agent.md prose is corrected, but scripts/gen-docs.mjs derives the whole downloadable file from .github/agents/agentweaver.agent.md and also writes apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md and apps/Agentweaver.Web/wwwroot/agents/agentweaver.agent.md. Those three paths are outside plan-shared document_paths. An authorized owner must apply the same prose correction to the source and regenerate/synchronize the mirrors; this worker did not edit them or run that generator. Required source changes: explicit outcome gate in manual sequence and shared operator-journey link. Do not remove artifact discovery before run_get_file.
- **shared-readme-system** — Parent consumer gap: README.md architecture image must change from docs/public/pitch-architecture.png to docs/diagrams/email-architecture.png with final alt/provenance derived from promoted canonical. This worker deliberately did not touch embeds or provenance. No lineage blocker.
- **shared-root-excalidraw-retirement** — Parent cleanup gap: retire docs/aks-architecture.excalidraw and docs/aks-architecture-block.excalidraw under reconciled catalog tombstones after consumer reconciliation; reuse guide-owned canonical-aks-components. Global inventory/tombstones and asset deletions are explicitly outside this task. No new AKS diagram.

## Other explicit handoffs

- `.github/workflows/publish-images.yml` comments say manual builds are SHA-only; actual `ghcr-plan.mjs` classifies the selected branch. Owned prose is corrected; pipeline comments are outside the write boundary.
- Generated public-agent prose needs source/mirror synchronization (paths and exact corrections listed per blocked concept). The 107-tool generated map itself is accurate and unchanged.
- Repository credentials still reach AgentHost/scoped direct commands. This is a disclosed normative-versus-current product gap, not a diagram label to copy or an implemented fix.
- Shared memory model remains prose/inline only; no new ER/inbox PNG is authorized.

## One result per assigned concept

### deep-dive-core-persistence-memory-domain

- **Document:** `docs/deep-dive/data-persistence.md`
- **Result:** blocked; **disposition:** redesign; **representation:** prose plus proposed inline schema/table handoff.
- **Target:** `none`.
- **Action:** Retain current invariants; record foreign-page schema ownership reconciliation, without inventing an ER PNG.
- **Current ground truth:** The unique keys are (ProjectId, Slug) and (ProjectId, SessionId), not globally unique slug/session id. Decision supersession and inbox DecisionId are optional FKs. WorkPlan owns Subtask with cascade; SubtaskDependency.SubtaskId cascades but DependsOnSubtaskId restricts deletion. Project scope is not blanket proof of cross-store enforced FKs.
- **Current written document:** `docs/deep-dive/data-persistence.md:176`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api.Data/Memory/MemoryDbContext.cs:94`; `apps/Agentweaver.Api.Data/Memory/MemoryDbContext.cs:126`; `apps/Agentweaver.Api.Data/Memory/MemoryDbContext.cs:154`.
- **Exact residual:** Foreign consumer docs/deep-dive/data-persistence.md: under Memory and Orchestration Store, make this the shared schema-oriented concept owner using a compact table/inline model of the constraints above. Link the focused governance ER in docs/deep-dive/memory-decisions.md back to this definition. Its current core-invariants prose is correct, but the reconciled shared model ownership/link is not enacted. No new diagram name/assets authorized.

### deep-dive-core-provider-admission

- **Document:** `docs/deep-dive/api-core.md`
- **Result:** blocked; **disposition:** redesign; **representation:** prose/sequence consumer handoff.
- **Target:** `canonical-provider-admission`.
- **Action:** Independently trace preparation, acceptance, pre-call fence and durable snapshot; do not alter promoted lineage.
- **Current ground truth:** Prepare signs caller/project/operation/provider identity and expiry; Accept verifies all bindings and returns replacement context on failure. RevalidateAccepted compares current provider identity before use. A durable run snapshot has single-winner ownership; display provider_key is not execution authority.
- **Current written document:** `docs/deep-dive/api-core.md:249`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:256`; `apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293`; `apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:384`; `apps/Agentweaver.Api/Auth/RunModelProviderSnapshotStore.cs:162`.
- **Exact residual:** Foreign consumer docs/deep-dive/api-core.md: Effective model-provider context describes Expected and persisted provenance but omits the complete execution_key/If-Model-Provider-Key acceptance and pre-call fence. Add these steps and reuse canonical-provider-admission (already promoted by separate writer); parent owns embed/provenance. No lineage blocker is asserted.

### deep-dive-orchestration-inbox-state

- **Document:** `docs/deep-dive/memory-decisions.md`
- **Result:** completed; **disposition:** redesign; **representation:** existing inline state model and prose.
- **Target:** `none`.
- **Action:** Retain source-correct Pending/Merged/Rejected model; no new diagram.
- **Current ground truth:** Persisted values are lowercase pending/merged/rejected; title-case diagram names are display labels. Pending update requires case-insensitive same agent plus matching SourceKind/SourceIdentity. Merge endpoints own a transaction; helper creates active approved decision and links inbox row. Owner or verified Coordinator approval is required; rejection retains history.
- **Current written document:** `docs/deep-dive/memory-decisions.md:189`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:83`; `apps/Agentweaver.Api/Memory/DecisionPromotion.cs:30`; `apps/Agentweaver.Api/Security/RunAuthorship.cs:18`; `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:295`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### deep-dive-orchestration-memory-entities

- **Document:** `docs/deep-dive/memory-decisions.md`
- **Result:** blocked; **disposition:** reuse; **representation:** existing focused inline ER, shared-schema reuse pending.
- **Target:** `deep-dive-core-persistence-memory-domain`.
- **Action:** Retain accurate governance fields and conceptual cross-store qualification; request ownership link.
- **Current ground truth:** Current governance ER already disclaims universal PROJECT foreign keys and states project-wide slug/session uniqueness; the EF mappings independently confirm optional decision references and composite unique indexes.
- **Current written document:** `docs/deep-dive/memory-decisions.md:103`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api.Data/Memory/MemoryDbContext.cs:91`; `apps/Agentweaver.Api.Data/Memory/MemoryDbContext.cs:99`; `apps/Agentweaver.Api.Data/Memory/MemoryDbContext.cs:94`.
- **Exact residual:** Foreign consumer docs/deep-dive/memory-decisions.md: keep its focused inline ER, but label it as a view of deep-dive-core-persistence-memory-domain and link to data-persistence.md#memory-and-orchestration-store-memory-db. Do not create a second ER asset. Coordinate with the data-persistence owner to establish the anchor/model once.

### experience-decision-inbox-state

- **Document:** `docs/experience/agent-communication.md`
- **Result:** blocked; **disposition:** reuse; **representation:** reader-facing inline state aliases.
- **Target:** `deep-dive-orchestration-inbox-state`.
- **Action:** Verify authorization and eligibility; record exact alias/reuse clarification.
- **Current ground truth:** The current experience page correctly requires owner/verified Coordinator and active approved architectural/scope eligibility. Its Proposed/Finalized sketch is UI vocabulary, whereas database inbox statuses are pending/merged/rejected; a merged inbox links to a separate decision.
- **Current written document:** `docs/experience/agent-communication.md:58`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:78`; `apps/Agentweaver.Api/Memory/DecisionPromotion.cs:30`; `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:60`; `tests/Agentweaver.Tests/Memory/MemoryContextCompilerSecurityTests.cs:107`.
- **Exact residual:** Foreign consumer docs/experience/agent-communication.md: annotate Proposed=pending, Finalized=merged inbox plus linked decision, Rejected=rejected; link the sketch explicitly to memory-decisions.md#the-inbox-to-promotion-model. Preserve current owner/Coordinator and active-approved eligibility prose. Do not add a separate state PNG.

### reference-provider-context-lifecycle

- **Document:** `docs/reference/api.md`
- **Result:** blocked; **disposition:** reuse; **representation:** API prose reusing provider admission sequence.
- **Target:** `deep-dive-core-provider-admission`.
- **Action:** Verify header authority and MCP preparation; request missing lifecycle reference.
- **Current ground truth:** API documentation correctly names execution_key and matching operation. MCP PostAiAsync prepares context, rejects unavailable providers, and forwards execution_key in If-Model-Provider-Key. Acceptance/fencing and immutable run snapshot are the same shared admission contract.
- **Current written document:** `docs/reference/api.md:111`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Mcp/AgentweaverApiClient.cs:395`; `apps/Agentweaver.Mcp/AgentweaverApiClient.cs:453`; `apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:384`.
- **Exact residual:** Foreign consumer docs/reference/api.md#ai-execution-context: add acceptance/snapshot/pre-call fence and redacted reprepare/retry semantics, distinguish the display provider fingerprint from execution authority, and reuse canonical-provider-admission rather than an API-only or MCP-only diagram. Parent owns canonical embed/provenance and reference-page handoff.

### shared-adr-promotion

- **Document:** `docs/architecture/decisions/README.md`
- **Result:** completed; **disposition:** retain; **representation:** prose.
- **Target:** `none`.
- **Action:** Clarify repository ADR versus product inbox/compiled-policy distinction.
- **Current ground truth:** Repository durable decisions are numbered ADRs; routine operational decisions remain in .squad/decisions.md. Product inbox states and active-approved compiler eligibility are separate.
- **Current written document:** `docs/architecture/decisions/README.md:13`.
- **Implementation/config/test/policy citations:** `CONTRIBUTING.md:422`; `apps/Agentweaver.Api/Memory/DecisionPromotion.cs:30`; `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:59`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-adr-template

- **Document:** `docs/architecture/decisions/0000-template.md`
- **Result:** completed; **disposition:** retain; **representation:** 15-line prose template.
- **Target:** `none`.
- **Action:** Retain Context/Decision/Consequences and Proposed/Accepted/Superseded template.
- **Current ground truth:** This is a repository ADR authoring template, not a product state-machine schema.
- **Current written document:** `docs/architecture/decisions/0000-template.md:3`.
- **Implementation/config/test/policy citations:** `docs/architecture/decisions/README.md:5`; `docs/architecture/decisions/0001-agent-worktree-isolation.md:3`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-agent-diagram-impact

- **Document:** `AGENTS.md`
- **Result:** completed; **disposition:** reuse; **representation:** reference prose.
- **Target:** `shared-diagram-ownership-and-review`.
- **Action:** Retain pointer to shared diagram review ownership and docs-only scope.
- **Current ground truth:** AGENTS delegates full/cross-page audit versus bounded pitch/iterate to one authoring workflow; product rendering is a factual/style reference, not migration scope.
- **Current written document:** `AGENTS.md:26`.
- **Implementation/config/test/policy citations:** `docs/diagrams/README.md:54`; `docs/diagrams/README.md:11`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-agent-entry-point

- **Document:** `AGENTS.md`
- **Result:** completed; **disposition:** retain; **representation:** reference prose.
- **Target:** `none`.
- **Action:** Retain short cross-tool orientation instead of duplicating contribution policy.
- **Current ground truth:** CONTRIBUTING owns AI lifecycle and exact per-area validation commands; AGENTS points there.
- **Current written document:** `AGENTS.md:8`.
- **Implementation/config/test/policy citations:** `CONTRIBUTING.md:313`; `CONTRIBUTING.md:105`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-aks-redirect

- **Document:** `docs/aks-deployment.md`
- **Result:** completed; **disposition:** retain; **representation:** single link.
- **Target:** `none`.
- **Action:** Retain moved-page redirect.
- **Current ground truth:** Deployment commands remain azure:* entries in package.json; the root page delegates to the guide.
- **Current written document:** `docs/aks-deployment.md:7`.
- **Implementation/config/test/policy citations:** `package.json:9`; `docs/guide/deployment-aks.md:5`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-api-driver-gates

- **Document:** `docs/api-test-harness-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** current contract table plus dated historical detail.
- **Target:** `none`.
- **Action:** Correct as-built status: removed API wrapper/approval modules are history, not current commands.
- **Current ground truth:** Current PersonaActor performs direct live fetch actions and must inspect gate evidence, never blind-approve, and stop at the brief boundary. run-persona.mjs is structural seam capture only.
- **Current written document:** `docs/api-test-harness-plan.md:23`.
- **Implementation/config/test/policy citations:** `.github/agents/persona-actor.agent.md:44`; `scripts/api-harness/run-persona.mjs:12`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-auth-mesh-proposal

- **Document:** `docs/design/auth-architecture-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** explicit historical proposal and conditional YAML.
- **Target:** `none`.
- **Action:** Retain corrected implemented-auth status and conditional mesh proposal.
- **Current ground truth:** The checked-in gateway is approuting-istio and namespace only declares Pod Security, not injection. Named ASP.NET selector and OpenIddict validation are current; old GitHubLegacy middleware examples are historical. This does not establish live cluster configuration.
- **Current written document:** `docs/design/auth-architecture-plan.md:26`.
- **Implementation/config/test/policy citations:** `k8s/base/gateway.yaml:25`; `k8s/base/namespace.yaml:10`; `apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:21`; `apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:42`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-catalog-roster-table

- **Document:** `docs/catalog-groupings.md`
- **Result:** completed; **disposition:** retain; **representation:** tables and short roster lists.
- **Target:** `none`.
- **Action:** Remove nonexistent Content Authoring roster members; use workflows array vocabulary.
- **Current ground truth:** Content Authoring has lead-researcher/writer/editor. Product-management, software-development and combined roster lists match current blueprint JSON; lightweight grouping roles are separate.
- **Current written document:** `docs/catalog-groupings.md:64`.
- **Implementation/config/test/policy citations:** `packages/Agentweaver.Squad/Catalog/Resources/blueprints/blueprint_content_authoring.json:5`; `packages/Agentweaver.Squad/Catalog/Resources/blueprints/blueprint_product_management.json:5`; `packages/Agentweaver.Squad/Catalog/Resources/blueprints/blueprint_software_development.json:5`; `packages/Agentweaver.Squad/Catalog/Resources/blueprints/blueprint_pm_and_software_development.json:5`; `packages/Agentweaver.Squad/Catalog/Resources/groupings/quick_software_development.json:5`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-contribution-branch-reuse

- **Document:** `CONTRIBUTING.md`
- **Result:** completed; **disposition:** reuse; **representation:** policy prose referencing RELEASING.
- **Target:** `shared-release-identity-flow`.
- **Action:** Retain one release identity owner; correct manual GHCR channel description.
- **Current ground truth:** Changesets baseBranch is dev; CI pushes target dev/main; image push refs include release/v*. Manual builds on named channels receive that channel tag. Local checkout VERSION=0.31.0 does not establish the latest deployed/published release.
- **Current written document:** `CONTRIBUTING.md:89`.
- **Implementation/config/test/policy citations:** `.changeset/config.json:7`; `.github/workflows/ci.yml:34`; `scripts/ci/ghcr-plan.mjs:69`; `RELEASING.md:6`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-contribution-docs-gate

- **Document:** `CONTRIBUTING.md`
- **Result:** completed; **disposition:** reuse; **representation:** reference prose.
- **Target:** `shared-diagram-ownership-and-review`.
- **Action:** Retain docs-as-you-go, decisions inbox and shared authoring pointers.
- **Current ground truth:** Contribution decisions and product decision trust are different records; diagram impact guidance points to the canonical docs-only pipeline rather than a duplicated process diagram.
- **Current written document:** `CONTRIBUTING.md:422`.
- **Implementation/config/test/policy citations:** `docs/architecture/decisions/README.md:13`; `docs/diagrams/README.md:102`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-contribution-lifecycle

- **Document:** `CONTRIBUTING.md`
- **Result:** completed; **disposition:** retain; **representation:** ordered policy prose.
- **Target:** `none`.
- **Action:** Retain issue/worktree/PR/review/merge lifecycle and explicit rejection rotation.
- **Current ground truth:** Ordinary changes-requested is not lockout; explicit REJECTED independent rewrite marker rotates the author. ADR0001 requires one worktree per issue and reuse within that issue.
- **Current written document:** `CONTRIBUTING.md:398`.
- **Implementation/config/test/policy citations:** `docs/architecture/decisions/0001-agent-worktree-isolation.md:13`; `CONTRIBUTING.md:402`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-contribution-validation

- **Document:** `CONTRIBUTING.md`
- **Result:** completed; **disposition:** retain; **representation:** commands and CI table.
- **Target:** `none`.
- **Action:** Retain focused commands/private dependencies; add actual Documentation diagrams CI job.
- **Current ground truth:** Current ci.yml includes seven-shard planning, web/node/docs, diagram validation and changeset jobs. Checked-in workflow configuration is not a live GitHub ruleset audit.
- **Current written document:** `CONTRIBUTING.md:189`.
- **Implementation/config/test/policy citations:** `.github/workflows/ci.yml:305`; `.github/workflows/ci.yml:13`; `package.json:28`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-diagram-native-symbols

- **Document:** `docs/diagrams/README.md`
- **Result:** completed; **disposition:** retain; **representation:** notation prose and style contract.
- **Target:** `none`.
- **Action:** Retain native-library semantics and connector language; no source edits.
- **Current ground truth:** Design-system manifest pins draw.io 31.4.5, native namespaces, orthogonal rounded connectors, arc line jumps, real junction size and dashed marigold loopback. None is visual approval evidence.
- **Current written document:** `docs/diagrams/README.md:81`.
- **Implementation/config/test/policy citations:** `docs/diagrams/drawio/design-system.json:52`; `docs/diagrams/drawio/design-system.json:44`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-diagram-ownership-and-review

- **Document:** `docs/diagrams/README.md`
- **Result:** completed; **disposition:** retain; **representation:** policy prose.
- **Target:** `none`.
- **Action:** Retain one shared owner/stable path/A5/pitch/four-pass gates; do not restart research.
- **Current ground truth:** Iteration schema requires A5 orientation, at least four passes and visible-semantic growth ratio >=9 for first pass. Hash consistency is separate from publication review.
- **Current written document:** `docs/diagrams/README.md:102`.
- **Implementation/config/test/policy citations:** `.github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json:14`; `.github/skills/docs-diagram-iterate/references/iteration-manifest.schema.json:25`; `docs/diagrams/README.md:55`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-e2e-operating-history

- **Document:** `docs/e2e-harness-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** dated operating log.
- **Target:** `none`.
- **Action:** Add explicit historical boundary around deployed versions, issue closure and obsolete gh auth.
- **Current ground truth:** July run/release claims are records, not current deployment facts. Current API actor uses Agentweaver recorder session and the Judge has no diagnostic tools.
- **Current written document:** `docs/e2e-harness-plan.md:5`.
- **Implementation/config/test/policy citations:** `scripts/api-harness/lib/auth-providers/recorder-session.mjs:20`; `.github/agents/judge.agent.md:4`; `VERSION:1`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-e2e-release-reuse

- **Document:** `docs/e2e-harness-plan.md`
- **Result:** completed; **disposition:** reuse; **representation:** reference prose.
- **Target:** `shared-release-identity-flow`.
- **Action:** Explicitly supersede old rc-per-batch/resume guidance with RELEASING ownership.
- **Current ground truth:** release:publish validates exact origin/main and prepared version; deployment is separate. The historical e2e log cannot authorize or certify a current release.
- **Current written document:** `docs/e2e-harness-plan.md:8`.
- **Implementation/config/test/policy citations:** `scripts/azure/release-publish.mjs:98`; `RELEASING.md:4`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-e2e-shared-architecture-pointer

- **Document:** `docs/e2e-harness-plan.md`
- **Result:** completed; **disposition:** reuse; **representation:** links.
- **Target:** `shared-harness-feedback-loop`.
- **Action:** Replace competing historical architecture authority with one current API-plan pointer.
- **Current ground truth:** Surface adapters feed shared harness-judge core/schema; e2e is not a fourth architecture.
- **Current written document:** `docs/e2e-harness-plan.md:130`.
- **Implementation/config/test/policy citations:** `docs/api-test-harness-plan.md:11`; `scripts/harness-judge/core.mjs:117`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-email-components

- **Document:** `docs/diagrams/email-exports/README.md`
- **Result:** completed; **disposition:** retain; **representation:** export-description prose.
- **Target:** `email-components`.
- **Action:** Retain distinct component/dependency scope and derived-export owner.
- **Current ground truth:** API project references independently substantiate Data, Domain, SandboxFs, AgentRuntime, Squad and PostgreSQL migration components; the export remains derived from email-components.
- **Current written document:** `docs/diagrams/email-exports/README.md:9`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Agentweaver.Api.csproj:4`; `apps/Agentweaver.Api/Agentweaver.Api.csproj:7`.
- **Exact residual:** Parent owns final canonical-to-email-export copy/provenance validation. This prose verification does not certify pixel freshness, byte identity, or the new lineage's visual review.

### shared-email-system

- **Document:** `docs/diagrams/email-exports/README.md`
- **Result:** completed; **disposition:** retain; **representation:** export-description prose.
- **Target:** `email-architecture`.
- **Action:** Retain one system architecture owner and explicit canonical derivation.
- **Current ground truth:** Entra/broker selector, authoritative API dependencies and AKS gateway substantiate the system roles; sandbox credentials must remain actual-versus-normative qualified, not assumed absent.
- **Current written document:** `docs/diagrams/email-exports/README.md:5`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:59`; `apps/Agentweaver.Api/Agentweaver.Api.csproj:4`; `k8s/base/gateway.yaml:25`.
- **Exact residual:** Parent owns export copy/provenance and README canonical embed. No PNG/hash/visual approval claim is made here; retain the independently verified sandbox discrepancy in any consumer prose.

### shared-harness-judge-boundary

- **Document:** `docs/api-test-harness-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** current table plus technical prose.
- **Target:** `none`.
- **Action:** State actual prompt validation, no-tool agent-native judge, and trusted command-path limits.
- **Current ground truth:** core fences redacted untrusted data, validates required metadata, and restricts child env. Agent-native Judge has tools: []; arbitrary external command is not OS-sandboxed by env filtering.
- **Current written document:** `docs/api-test-harness-plan.md:24`.
- **Implementation/config/test/policy citations:** `scripts/harness-judge/core.mjs:57`; `scripts/harness-judge/core.mjs:68`; `scripts/harness-judge/core.mjs:38`; `.github/agents/judge.agent.md:4`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-harness-network-policy

- **Document:** `docs/api-test-harness-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** current safety notice/table.
- **Target:** `none`.
- **Action:** Retain host-agnostic HTTPS/loopback rule and explicitly supersede allowlists/TLS bypass.
- **Current ground truth:** Network validator rejects non-http(s), userinfo, fragments and nonloopback HTTP. API client rejects cross-origin requests and redirects; target identity never guesses staging/production.
- **Current written document:** `docs/api-test-harness-plan.md:26`.
- **Implementation/config/test/policy citations:** `scripts/harness-shared/target-guard.mjs:20`; `scripts/api-harness/lib/client.mjs:69`; `scripts/api-harness/lib/client.mjs:103`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-harness-persona-layout

- **Document:** `docs/api-test-harness-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** one current file tree plus historical proposal.
- **Target:** `none`.
- **Action:** Publish accurate shared layout and label migration-era trees historical.
- **Current ground truth:** Cores and adapters live in shared personas/surfaces; prompt generators and persona-schema are at package root. Judge methodology can be supplied; no shared JUDGE.md file is assumed.
- **Current written document:** `docs/api-test-harness-plan.md:28`.
- **Implementation/config/test/policy citations:** `scripts/persona-briefs/index.mjs:8`; `scripts/persona-briefs/index.mjs:9`; `scripts/persona-briefs/generate-core.mjs:13`; `scripts/harness-judge/core.mjs:123`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-harness-rollout-history

- **Document:** `docs/api-test-harness-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** historical plan.
- **Target:** `none`.
- **Action:** Label all July rollout/coverage/launcher claims as historical; current entry points precede them.
- **Current ground truth:** API run-persona now handles only structural seams; shared judge/persona modules and UI/MCP drivers exist. Old pass counts and rollout steps are not independent current execution evidence.
- **Current written document:** `docs/api-test-harness-plan.md:50`.
- **Implementation/config/test/policy citations:** `scripts/api-harness/run-persona.mjs:12`; `scripts/ui-harness/package.json:11`; `scripts/mcp-harness/package.json:11`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-landing-context

- **Document:** `docs/index.md`
- **Result:** completed; **disposition:** retain; **representation:** screenshots and HTML prose.
- **Target:** `none`.
- **Action:** Retain concrete board/skills/memory affordances without duplicate explanatory diagrams.
- **Current ground truth:** Board derives Ready/Backlog and workflow-stage columns from persisted state. Memory compiler selects approved active boundaries rather than treating all proposals as policy.
- **Current written document:** `docs/index.md:189`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Runs/BoardProjectionService.cs:85`; `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:60`.
- **Exact residual:** No screenshot pixels or live UI were inspected; screenshot freshness is not certified. Their existing selection/path and adjacent concept claims are verified.

### shared-landing-live-proof

- **Document:** `docs/index.md`
- **Result:** completed; **disposition:** retain; **representation:** live React mount and fallback image.
- **Target:** `none`.
- **Action:** Retain WorkflowProof and existing fallback; do not revive orphan landing-product-feature.
- **Current ground truth:** Vue lazily imports shipped LandingWorkflowDemo through createLazyMounter; ClientOnly supplies workflow-run-graph fallback. This is a demonstration renderer, not a recorded live run.
- **Current written document:** `docs/index.md:53`.
- **Implementation/config/test/policy citations:** `docs/.vitepress/theme/components/WorkflowProof.vue:15`; `docs/.vitepress/theme/components/WorkflowProof.vue:3`.
- **Exact residual:** Parent's orphan cleanup remains separate. No renderer edits or screenshot freshness approval.

### shared-landing-mcp

- **Document:** `docs/index.md`
- **Result:** completed; **disposition:** retain; **representation:** illustrative HTML terminal and chat.
- **Target:** `none`.
- **Action:** Clarify that confirmation gates child dispatch, not all model activity; retain OAuth-first text.
- **Current ground truth:** MCP validates broker token before forwarding; Coordinator drafts before confirmation and invokes orchestrator only for confirmed outcome. Transcript is illustrative, not execution evidence.
- **Current written document:** `docs/index.md:157`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:15`; `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:149`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-landing-observability

- **Document:** `docs/index.md`
- **Result:** completed; **disposition:** retain; **representation:** screenshot and capability list.
- **Target:** `none`.
- **Action:** Retain current activity/model/AI-credit copy; do not resurrect old token pipeline.
- **Current ground truth:** AgentWeaverMetrics.TokenUsage is agentweaver.token.usage with nano_aiu unit, not an assertion of a legacy token_usage_records endpoint/table.
- **Current written document:** `docs/index.md:216`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Infrastructure/AgentWeaverMetrics.cs:25`.
- **Exact residual:** Screenshot freshness/live telemetry not certified; retained concept and current metric unit checked.

### shared-landing-outcome

- **Document:** `docs/index.md`
- **Result:** completed; **disposition:** retain; **representation:** four-step HTML and screenshot.
- **Target:** `none`.
- **Action:** Retain set/review/confirm-or-revise/review-result sequence.
- **Current ground truth:** CoordinatorWorkflowFactory explicitly loops revised draft feedback and only orchestrates after confirmed outcome; pending approval is not completed work.
- **Current written document:** `docs/index.md:83`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:136`; `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:149`.
- **Exact residual:** No screenshot visual approval or live scenario claim.

### shared-landing-team

- **Document:** `docs/index.md`
- **Result:** completed; **disposition:** retain; **representation:** team/casting screenshots and prose.
- **Target:** `none`.
- **Action:** Retain team review affordances rather than architecture cards.
- **Current ground truth:** Current software blueprint supplies the documented specialist roster; casting endpoints support charter inspection and member changes. Screenshots are illustrative UI evidence only.
- **Current written document:** `docs/index.md:105`.
- **Implementation/config/test/policy citations:** `packages/Agentweaver.Squad/Catalog/Resources/blueprints/blueprint_software_development.json:5`; `apps/Agentweaver.Api/Endpoints/TeamEndpoints.cs:106`.
- **Exact residual:** No screenshot pixels or deployed casting flow were reviewed; no visual freshness certification.

### shared-mcp-harness-layout

- **Document:** `docs/mcp-test-harness-plan.md`
- **Result:** completed; **disposition:** reuse; **representation:** shared-layout links plus MCP subtree.
- **Target:** `shared-harness-persona-layout`.
- **Action:** Remove duplicated persona/judge trees; preserve MCP transport/driver layout.
- **Current ground truth:** Live client, transcript normalizer and shared schema are separate modules; the persona generator's historical generate/ subdirectory is not current.
- **Current written document:** `docs/mcp-test-harness-plan.md:819`.
- **Implementation/config/test/policy citations:** `scripts/persona-briefs/index.mjs:58`; `scripts/mcp-harness/package.json:11`; `scripts/harness-judge/adapters/mcp.mjs:19`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-mcp-harness-loop

- **Document:** `docs/mcp-test-harness-plan.md`
- **Result:** completed; **disposition:** reuse; **representation:** shared-contract reference.
- **Target:** `shared-harness-feedback-loop`.
- **Action:** Make API-plan shared ownership explicit, keep protocol-specific prepare/finalize detail.
- **Current ground truth:** MCP run-persona uses shared judge machinery; live tool menu and recorded calls remain MCP evidence, not a separate grading schema.
- **Current written document:** `docs/mcp-test-harness-plan.md:5`.
- **Implementation/config/test/policy citations:** `scripts/harness-judge/core.mjs:117`; `scripts/harness-judge/verdict-schema.mjs:4`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-mcp-harness-safety

- **Document:** `docs/mcp-test-harness-plan.md`
- **Result:** completed; **disposition:** reuse; **representation:** current notice and protocol prose.
- **Target:** `shared-harness-network-policy`.
- **Action:** Supersede hostname/rollout auth claims; retain untrusted-description handling.
- **Current ground truth:** HTTP target must be exact /mcp without query, uses normal TLS and no redirects. Only server-validated broker JWT is acceptable remotely; stdio still needs downstream authority.
- **Current written document:** `docs/mcp-test-harness-plan.md:13`.
- **Implementation/config/test/policy citations:** `scripts/mcp-harness/mcp-client/transport-http.mjs:8`; `scripts/mcp-harness/mcp-client/transport-http.mjs:4`; `apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:80`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-mcp-harness-transport

- **Document:** `docs/mcp-test-harness-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** current prose and protocol examples.
- **Target:** `none`.
- **Action:** Mark run_task-not-shipped notes historical; distinguish discovery from compatibility checks.
- **Current ground truth:** checkCapabilities is a separate required-contract tripwire, not action space; run_task is a current McpServerTool supporting start/poll/gates/artifacts.
- **Current written document:** `docs/mcp-test-harness-plan.md:10`.
- **Implementation/config/test/policy citations:** `scripts/mcp-harness/lib/capabilities-contract.mjs:37`; `apps/Agentweaver.Mcp/Tools/RunTools.cs:137`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-mcp-harness-verdict

- **Document:** `docs/mcp-test-harness-plan.md`
- **Result:** completed; **disposition:** reuse; **representation:** shared-schema reference and field summary.
- **Target:** `shared-harness-verdict-contract`.
- **Action:** Remove malformed/incomplete local JSON sketch and low/high vocabulary.
- **Current ground truth:** Schema requires nine join strings including timestamp, both CANNOT_DETERMINE paths, object signals and nonempty rationale. Mild/severe are current labels; not_assessed score is null.
- **Current written document:** `docs/mcp-test-harness-plan.md:936`.
- **Implementation/config/test/policy citations:** `scripts/harness-judge/verdict-schema.mjs:16`; `scripts/harness-judge/verdict-schema.mjs:7`; `scripts/harness-judge/verdict-schema.mjs:70`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-mcp-oauth-flow

- **Document:** `docs/mcp-oauth.md`
- **Result:** completed; **disposition:** reuse; **representation:** protocol prose with shared-auth reference.
- **Target:** `auth-security-fig4`.
- **Action:** Link shared authentication overview instead of a second OAuth diagram.
- **Current ground truth:** API scheme selection allows broker only for PlatformOrMcp/AuthenticatedSelfOrMcp; MCP uses OpenIddict validation then issuer/audience/kid/RS256/subject/scope checks. Raw GitHub bearer and old denylist architecture are not current.
- **Current written document:** `docs/mcp-oauth.md:9`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:48`; `apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:72`; `apps/Agentweaver.Api/Auth/OAuth/OAuthRefreshReplayHandler.cs:34`.
- **Exact residual:** Parent owns promoted auth-security-fig4 consumer embeds/provenance. Prose reuse is enacted through shared overview link; no new OAuth graphic or lineage blocker.

### shared-mcp-session-and-keys

- **Document:** `docs/mcp-oauth.md`
- **Result:** completed; **disposition:** retain; **representation:** operational prose.
- **Target:** `none`.
- **Action:** Retain precise refresh recovery, lifetime and durable-key instructions.
- **Current ground truth:** Defaults are 8-hour access, fixed 30-day refresh family and 37-day replay retention. Production key loader fails without usable signing/encryption keys and selects newest two enabled/time-valid secret versions. Replay handler revokes authorization family.
- **Current written document:** `docs/mcp-oauth.md:29`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Auth/OAuth/OAuthServerConfiguration.cs:26`; `apps/Agentweaver.Api/Auth/OAuth/OAuthServerConfiguration.cs:28`; `apps/Agentweaver.Api/Auth/OAuth/OAuthServerConfiguration.cs:317`; `apps/Agentweaver.Api/Auth/OAuth/OAuthServerConfiguration.cs:361`; `apps/Agentweaver.Api/Auth/OAuth/OAuthRefreshReplayHandler.cs:93`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-promotion-board-reuse

- **Document:** `docs/design/285-prd-story-promotion.md`
- **Result:** completed; **disposition:** reuse; **representation:** state definitions, SQL predicate and shared-board link.
- **Target:** `canonical-board-lifecycle`.
- **Action:** Reuse board guide; correct missing unarchived-prerequisite condition and non-regression claim.
- **Current ground truth:** No Blocked column exists. Ready blocked eligibility is derived; both EF and SQLite require unarchived prerequisite with existing linked merged run. Archiving can make the edge unsatisfied.
- **Current written document:** `docs/design/285-prd-story-promotion.md:354`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Runs/BoardProjectionService.cs:85`; `apps/Agentweaver.Api/Infrastructure/Ef/EfBacklogTaskStore.cs:113`; `apps/Agentweaver.Api/Infrastructure/SqliteBacklogTaskStore.cs:131`; `apps/Agentweaver.Api/Backlog/BacklogTaskReadModelFactory.cs:52`.
- **Exact residual:** Parent validation handoff: focused dotnet test --no-build --no-restore returned exit 0 without output or a test count; tests/Agentweaver.Tests/bin does not exist. This is NOT a pass. Parent owns locked restore/build and focused execution; no dependencies were installed here.

### shared-promotion-persistence-contract

- **Document:** `docs/design/285-prd-story-promotion.md`
- **Result:** completed; **disposition:** retain; **representation:** SQL/DTO/reference prose.
- **Target:** `none`.
- **Action:** Retain technical contract; independently verify validation and idempotent transaction.
- **Current ground truth:** Service validates same-project top-level Coordinator parent, 1..50 stories, unique trimmed keys/titles, required bounded fields, known nonself dependency keys and acyclic graph before provider-specific transaction. Tests cover identical replay and changed-payload conflict.
- **Current written document:** `docs/design/285-prd-story-promotion.md:274`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Backlog/BacklogPromotionService.cs:69`; `apps/Agentweaver.Api/Backlog/BacklogPromotionService.cs:79`; `apps/Agentweaver.Api/Backlog/BacklogPromotionService.cs:148`; `tests/Agentweaver.Tests/Backlog/BacklogPromotionServiceTests.cs:14`.
- **Exact residual:** Parent validation handoff: focused dotnet test --no-build --no-restore returned exit 0 without output or a test count; tests/Agentweaver.Tests/bin does not exist. This is NOT a pass. Parent owns locked restore/build and focused execution; no dependencies were installed here.

### shared-public-agent-preview-and-context

- **Document:** `docs/public/agents/agentweaver.agent.md`
- **Result:** blocked; **disposition:** retain; **representation:** ordered playbooks and catalog table.
- **Target:** `none`.
- **Action:** Remove retired code-review from Software Development catalog snapshot; retain gate/ad-hoc preview distinction.
- **Current ground truth:** Blueprint JSON permits software-delivery/bug-fix. MCP start_preview requires a server already running and verified at the exact port. The build_test gate owns its canonical instruction.
- **Current written document:** `docs/public/agents/agentweaver.agent.md:32`.
- **Implementation/config/test/policy citations:** `packages/Agentweaver.Squad/Catalog/Resources/blueprints/blueprint_software_development.json:6`; `apps/Agentweaver.Mcp/Tools/RunTools.cs:305`; `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:190`.
- **Exact residual:** Generated-consumer handoff: the allowed docs/public/agents/agentweaver.agent.md prose is corrected, but scripts/gen-docs.mjs derives the whole downloadable file from .github/agents/agentweaver.agent.md and also writes apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md and apps/Agentweaver.Web/wwwroot/agents/agentweaver.agent.md. Those three paths are outside plan-shared document_paths. An authorized owner must apply the same prose correction to the source and regenerate/synchronize the mirrors; this worker did not edit them or run that generator. Required source change: remove code-review from Software Development snapshot.

### shared-public-agent-run-journey

- **Document:** `docs/public/agents/agentweaver.agent.md`
- **Result:** blocked; **disposition:** reuse; **representation:** ordered tool sequences and shared-journey link.
- **Target:** `canonical-coordinator-journey`.
- **Action:** Add missing outcome get/confirm-or-revise step to abbreviated manual sequence; link shared journey.
- **Current ground truth:** run_task is one-call supervision, not blind approval: it returns awaiting_review, awaiting_confirmation or timed_out. Confirmation precedes child dispatch.
- **Current written document:** `docs/public/agents/agentweaver.agent.md:135`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Mcp/Tools/RunTools.cs:484`; `apps/Agentweaver.Mcp/Tools/RunTools.cs:199`; `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:149`.
- **Exact residual:** Generated-consumer handoff: the allowed docs/public/agents/agentweaver.agent.md prose is corrected, but scripts/gen-docs.mjs derives the whole downloadable file from .github/agents/agentweaver.agent.md and also writes apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md and apps/Agentweaver.Web/wwwroot/agents/agentweaver.agent.md. Those three paths are outside plan-shared document_paths. An authorized owner must apply the same prose correction to the source and regenerate/synchronize the mirrors; this worker did not edit them or run that generator. Required source changes: explicit outcome gate in manual sequence and shared operator-journey link. Do not remove artifact discovery before run_get_file.

### shared-public-agent-tool-map

- **Document:** `docs/public/agents/agentweaver.agent.md`
- **Result:** completed; **disposition:** retain; **representation:** generated tool list.
- **Target:** `none`.
- **Action:** Retain generated map, independently count current tool attributes; do not hand-edit generated block.
- **Current ground truth:** Read-only regex enumeration of apps/Agentweaver.Mcp/Tools/*.cs found 107 attributes and 107 unique names. scripts/gen-docs.mjs owns map and downloadable mirrors; tool calls were not invoked.
- **Current written document:** `docs/public/agents/agentweaver.agent.md:47`.
- **Implementation/config/test/policy citations:** `scripts/gen-docs.mjs:101`; `apps/Agentweaver.Mcp/Tools/RunTools.cs:137`.
- **Exact residual:** Tool map itself needs no correction. Whole-file prose mirror handoff is tracked separately under shared-public-agent-preview-and-context and shared-public-agent-run-journey.

### shared-readme-brand

- **Document:** `README.md`
- **Result:** completed; **disposition:** retain; **representation:** brand image.
- **Target:** `none`.
- **Action:** Retain logo as brand asset, not architecture inventory.
- **Current ground truth:** README explicitly selects docs/public/agentweaver.png as Agentweaver logo; it carries no architectural topology assertion.
- **Current written document:** `README.md:2`.
- **Implementation/config/test/policy citations:** `README.md:5`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-readme-capabilities

- **Document:** `README.md`
- **Result:** completed; **disposition:** retain; **representation:** capability table and setup commands.
- **Target:** `none`.
- **Action:** Retain alpha warning, concise capabilities, setup/dev commands and specialist-guide links.
- **Current ground truth:** Root npm scripts map setup and dev to Azure CLI local development paths. Projects/teams/workflow/board/review/MCP rows summarize existing capabilities rather than another system diagram.
- **Current written document:** `README.md:27`.
- **Implementation/config/test/policy citations:** `package.json:8`; `package.json:6`; `apps/Agentweaver.Mcp/Tools/RunTools.cs:137`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-readme-system

- **Document:** `README.md`
- **Result:** blocked; **disposition:** reuse; **representation:** canonical-system reuse pending parent embed.
- **Target:** `email-architecture`.
- **Action:** Verify architecture prose; leave image swap to parent as explicitly assigned.
- **Current ground truth:** API references Data/runtime/Squad; gateway and named Entra/broker auth substantiate major roles. The current README still consumes docs/public/pitch-architecture.png; visual labels are not proof.
- **Current written document:** `README.md:15`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Agentweaver.Api.csproj:4`; `apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:59`; `k8s/base/gateway.yaml:25`.
- **Exact residual:** Parent consumer gap: README.md architecture image must change from docs/public/pitch-architecture.png to docs/diagrams/email-architecture.png with final alt/provenance derived from promoted canonical. This worker deliberately did not touch embeds or provenance. No lineage blocker.

### shared-release-identity-flow

- **Document:** `RELEASING.md`
- **Result:** completed; **disposition:** retain; **representation:** ASCII flow and command table.
- **Target:** `none`.
- **Action:** Retain single release-versus-deploy model; distinguish checkout identity from current deployment.
- **Current ground truth:** release-publish checks prepared mirrors and exact fetched origin/main, creates annotated tag/GitHub Release and no Azure deployment. deploy-from-release defaults ghcr and requires exact tag HEAD.
- **Current written document:** `RELEASING.md:6`.
- **Implementation/config/test/policy citations:** `scripts/azure/release-publish.mjs:98`; `scripts/azure/release-publish.mjs:198`; `scripts/azure/deploy-from-release.mjs:43`; `VERSION:1`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-release-promotion-and-images

- **Document:** `RELEASING.md`
- **Result:** completed; **disposition:** retain; **representation:** commands and tag table.
- **Target:** `none`.
- **Action:** Correct manual ref-dependent channel tags and false rebase-ancestry guarantee.
- **Current ground truth:** ghcr-plan resolves selected branch channel even for workflow_dispatch; arbitrary refs are commit-only. Docs/specs/Markdown-only pushes are skipped. Rebase rewrites commits, while release identity is still exact resulting main SHA independent of merge method.
- **Current written document:** `RELEASING.md:158`.
- **Implementation/config/test/policy citations:** `scripts/ci/ghcr-plan.mjs:65`; `scripts/ci/ghcr-plan.mjs:88`; `.github/workflows/publish-images.yml:44`; `scripts/azure/release-publish.mjs:98`.
- **Exact residual:** Checked-in workflow comments still say manual runs are sha-only, contrary to ghcr-plan code. Parent/pipeline owner may correct comments in .github/workflows/publish-images.yml; forbidden here. Actual tag behavior is now accurately documented. No live release/deployment certification.

### shared-root-excalidraw-retirement

- **Document:** `docs/aks-deployment.md`
- **Result:** blocked; **disposition:** reuse; **representation:** redirect/reference reuse with asset-removal handoff.
- **Target:** `canonical-aks-components`.
- **Action:** Retain guide redirect and canonical-aks-components ownership; no forbidden source deletion.
- **Current ground truth:** Current gateway is approuting-istio, Entra is product identity; legacy root Excalidraw labels cannot establish current auth/storage. Redirect avoids competing deployment explanation.
- **Current written document:** `docs/aks-deployment.md:7`.
- **Implementation/config/test/policy citations:** `k8s/base/gateway.yaml:25`; `apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:59`.
- **Exact residual:** Parent cleanup gap: retire docs/aks-architecture.excalidraw and docs/aks-architecture-block.excalidraw under reconciled catalog tombstones after consumer reconciliation; reuse guide-owned canonical-aks-components. Global inventory/tombstones and asset deletions are explicitly outside this task. No new AKS diagram.

### shared-tool-approval-lifecycle

- **Document:** `docs/tool-approval-sse-contract.md`
- **Result:** completed; **disposition:** retain; **representation:** inline lifecycle and event table.
- **Target:** `none`.
- **Action:** Retain required→pending heartbeat→resolved card semantics.
- **Current ground truth:** Runtime heartbeat interval is 20 seconds; durable gate records denied/expired resolution and emits resolved event. Pending heartbeat is nonterminal, not a new approval or quality success.
- **Current written document:** `docs/tool-approval-sse-contract.md:210`.
- **Implementation/config/test/policy citations:** `packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:256`; `apps/Agentweaver.Api/Runs/DurableToolApprovalGate.cs:68`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-tool-approval-routing

- **Document:** `docs/tool-approval-sse-contract.md`
- **Result:** completed; **disposition:** retain; **representation:** ordered protocol plus HTTP table.
- **Target:** `none`.
- **Action:** Correct current scoped-approval probe/provisional/persist/finalize-or-rollback protocol.
- **Current ground truth:** Scoped grant needs active target, pending context, reachable applied approval and exact grant-id proof before durable policy. Failure closes or waits out provisional lease; duplicate applied=false cannot persist. Some 503s are Problem Details without state, and inactive target can return 409.
- **Current written document:** `docs/tool-approval-sse-contract.md:162`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:2085`; `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:2127`; `apps/Agentweaver.Api/Sandbox/AgentHostApprovalHttpClient.cs:143`.
- **Exact residual:** Parent validation handoff: focused dotnet test --no-build --no-restore returned exit 0 without output or a test count; tests/Agentweaver.Tests/bin does not exist. This is NOT a pass. Parent owns locked restore/build and focused execution; no dependencies were installed here.

### shared-tool-approval-stall

- **Document:** `docs/tool-approval-sse-contract.md`
- **Result:** completed; **disposition:** retain; **representation:** prose.
- **Target:** `none`.
- **Action:** Retain approval wait versus stall explanation; update moved source citation.
- **Current ground truth:** Observer sets pending request on required, keeps it on pending heartbeat and clears on other events. Its per-event timeout defaults to five minutes; pending approval is legitimate waiting.
- **Current written document:** `docs/tool-approval-sse-contract.md:227`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1914`; `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1916`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-two-app-capabilities

- **Document:** `docs/design/two-github-app-production-contract.md`
- **Result:** completed; **disposition:** retain; **representation:** normative matrix plus actual-state qualification.
- **Target:** `none`.
- **Action:** Retain purpose-bound capability contract without claiming full implementation compliance.
- **Current ground truth:** Selector establishes Entra product sign-in, not GitHub sign-in. Credential provider resolves matching immutable snapshot/purpose. Current Kubernetes executor sends repositoryAccessToken; AgentHost retains it and RunCommandTool scopes credentials to parsed direct commands. Normative no-sandbox-repository-credential requirement is therefore not satisfied by current code.
- **Current written document:** `docs/design/two-github-app-production-contract.md:23`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:59`; `apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs:90`; `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:697`; `apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs:172`; `packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs:217`.
- **Exact residual:** Intentional product gap remains: no-token/no-direct-GitHub normative sandbox boundary is not fully implemented. The page already discloses this; no runtime correction is authorized.

### shared-two-app-cutover-contract

- **Document:** `docs/design/two-github-app-production-contract.md`
- **Result:** completed; **disposition:** retain; **representation:** normative prose and verification list.
- **Target:** `none`.
- **Action:** Retain forward-only/webhook/audit requirements with implementation-status boundary.
- **Current ground truth:** LegacyOAuthRetirementTests check removed device/status routes and old tables. That targeted contract is not evidence all webhook/audit/credential-boundary requirements have shipped. The page explicitly labels requirements normative and the compatibility lane historical.
- **Current written document:** `docs/design/two-github-app-production-contract.md:217`.
- **Implementation/config/test/policy citations:** `tests/Agentweaver.Tests/Auth/LegacyOAuthRetirementTests.cs:17`; `tests/Agentweaver.Tests/Auth/LegacyOAuthRetirementTests.cs:28`; `docs/design/two-github-app-production-contract.md:10`.
- **Exact residual:** Full production compliance and live callback/App settings are not certified. Preserve the known sandbox discrepancy; product and deployment remediation belong to their owners.

### shared-ui-harness-auth-and-rollout

- **Document:** `docs/ui-test-harness-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** current auth notice plus historical rollout.
- **Target:** `none`.
- **Action:** Correct design-only/browser assumptions; state Chrome capture, sessionStorage sidecar and no automated Entra login.
- **Current ground truth:** browser.mjs pins installed chrome outside hermetic tests and guards same-origin navigation; auth.mjs loads stored state and separately captures sessionStorage, rejecting missing/expired state.
- **Current written document:** `docs/ui-test-harness-plan.md:3`.
- **Implementation/config/test/policy citations:** `scripts/ui-harness/lib/browser.mjs:63`; `scripts/ui-harness/lib/browser.mjs:48`; `scripts/ui-harness/lib/auth.mjs:11`; `scripts/ui-harness/lib/auth.mjs:72`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-ui-harness-common-loop

- **Document:** `docs/ui-test-harness-plan.md`
- **Result:** completed; **disposition:** reuse; **representation:** shared-contract reference and UI responsibility prose.
- **Target:** `shared-harness-feedback-loop`.
- **Action:** Point common loop to API-plan owner, preserve UI-specific evidence responsibilities.
- **Current ground truth:** One schema covers api/ui/mcp; UI adapter and supplied browser evidence enter the shared core, not a separate pass/fail vocabulary or a fourth e2e architecture.
- **Current written document:** `docs/ui-test-harness-plan.md:5`.
- **Implementation/config/test/policy citations:** `scripts/harness-judge/verdict-schema.mjs:4`; `scripts/harness-judge/core.mjs:117`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-ui-harness-driver

- **Document:** `docs/ui-test-harness-plan.md`
- **Result:** completed; **disposition:** retain; **representation:** historical ASCII navigation with current-boundary notice.
- **Target:** `none`.
- **Action:** Retain compact sketch, explicitly distinguish supporting logs from authority to debug/self-certify.
- **Current ground truth:** Browser driver collects facts; shared core consumes redacted normalized turns and supplemental evidence. No-tool Judge cannot fetch logs, and screenshot capture does not equal visual approval.
- **Current written document:** `docs/ui-test-harness-plan.md:575`.
- **Implementation/config/test/policy citations:** `scripts/ui-harness/lib/browser.mjs:57`; `scripts/harness-judge/core.mjs:176`; `.github/agents/judge.agent.md:4`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-ui-harness-layout

- **Document:** `docs/ui-test-harness-plan.md`
- **Result:** completed; **disposition:** reuse; **representation:** shared-layout references plus UI subtree.
- **Target:** `shared-harness-persona-layout`.
- **Action:** Remove two duplicated shared trees; retain browser/evidence implementation pointers.
- **Current ground truth:** Shared personas/surfaces and judge adapters are single owners. UI driver has its own agent-driver-ui and lib directories, not a fork of persona/judge packages.
- **Current written document:** `docs/ui-test-harness-plan.md:169`.
- **Implementation/config/test/policy citations:** `scripts/persona-briefs/index.mjs:9`; `scripts/ui-harness/package.json:11`; `scripts/harness-judge/adapters/ui.mjs:16`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-ui-harness-verdict

- **Document:** `docs/ui-test-harness-plan.md`
- **Result:** completed; **disposition:** reuse; **representation:** shared-schema reference and field summary.
- **Target:** `shared-harness-verdict-contract`.
- **Action:** Remove copied invalid/underspecified JSON; use current schema link and exact required fields.
- **Current ground truth:** Mild/severe replace low/high, P0/P1 permit CANNOT_DETERMINE, all nine joins are required, signals are objects and rationale nonempty. A null score is mandatory for not_assessed.
- **Current written document:** `docs/ui-test-harness-plan.md:270`.
- **Implementation/config/test/policy citations:** `scripts/harness-judge/verdict-schema.mjs:5`; `scripts/harness-judge/verdict-schema.mjs:6`; `scripts/harness-judge/verdict-schema.mjs:16`; `scripts/harness-judge/verdict-schema.mjs:70`.
- **Exact residual:** None for retained-text scope. No diagram, screenshot, deployment, or whole-suite certification is implied.

### shared-workflow-binder-factory

- **Document:** `docs/workflow-binder.md`
- **Result:** completed; **disposition:** retain; **representation:** executor/edge tables and technical prose.
- **Target:** `none`.
- **Action:** Add build_test/open_pull_request/publish vocabulary, per-node resolution, legacy gate fallback; correct stale test names.
- **Current ground truth:** Classifier resolves by type except documented gate-kind fallback. BuildTest uses PeerReview; OpenPullRequest has deterministic wiring. Unsupported composites fail closed. Current tests assert six-stage default; illustrative edge tables are not exhaustive.
- **Current written document:** `docs/workflow-binder.md:34`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:76`; `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:51`; `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:97`; `apps/Agentweaver.Api/Workflows/NodeExecutorRegistry.cs:49`; `tests/Agentweaver.Tests/Workflows/RunWorkflowGraphBinderTests.cs:22`.
- **Exact residual:** Parent validation handoff: focused dotnet test --no-build --no-restore returned exit 0 without output or a test count; tests/Agentweaver.Tests/bin does not exist. This is NOT a pass. Parent owns locked restore/build and focused execution; no dependencies were installed here.

### shared-workflow-generation-journey

- **Document:** `docs/workflow-generation.md`
- **Result:** completed; **disposition:** reuse; **representation:** shared-authoring link plus server prose.
- **Target:** `canonical-workflow-authoring`.
- **Action:** Reuse guide authoring journey; add binder dry-run and built-in-edit copy validation.
- **Current ground truth:** Generated YAML is an unsaved draft. ParseCandidate runs schema load then ValidateBindable; first failure gets exactly one correction call. Explicit save remains separate.
- **Current written document:** `docs/workflow-generation.md:10`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:93`; `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:90`; `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:80`; `apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs:590`.
- **Exact residual:** Parent validation handoff: focused dotnet test --no-build --no-restore returned exit 0 without output or a test count; tests/Agentweaver.Tests/bin does not exist. This is NOT a pass. Parent owns locked restore/build and focused execution; no dependencies were installed here.

### shared-workflow-generation-prompt-contract

- **Document:** `docs/workflow-generation.md`
- **Result:** completed; **disposition:** retain; **representation:** prompt/endpoint prose and table.
- **Target:** `none`.
- **Action:** Correct provider-fixed claim, triggers vocabulary, supported nodes and few-shot selection.
- **Current ground truth:** Production generation resolves Copilot or BYOK through GenerationModelProviderExecutor; workflow_generation admission uses execution_key. Few shots prefer software_delivery/bug_fix, else up to three valid non-default workflows. Prompt forbids unsupported composites; build_test is runtime-owned. Blueprint fallback remains draft generation then apply.
- **Current written document:** `docs/workflow-generation.md:49`.
- **Implementation/config/test/policy citations:** `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:443`; `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:413`; `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:190`; `apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:47`.
- **Exact residual:** Parent validation handoff: focused dotnet test --no-build --no-restore returned exit 0 without output or a test count; tests/Agentweaver.Tests/bin does not exist. This is NOT a pass. Parent owns locked restore/build and focused execution; no dependencies were installed here.
