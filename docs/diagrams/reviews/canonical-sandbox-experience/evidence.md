# canonical-sandbox-experience: evidence and exclusive claim

Owner: assigned implementation agent; disposition: redesign (provider admission: new).
Scope: this name's review directory, canonical draw.io, PNG/hash, and retired legacy source only.
Global inventories and document pages are deliberately not modified.

Target page: `docs/deep-dive/sandbox-pod-execution.md`
Takeaway: A run gets isolated execution, visible progress and bounded tools—not an unrestricted host shell.
Orientation: A5 landscape, 827 × 583 units at 100 units/inch.

## Research provenance
The coordinator reports exactly three independent bounded GPT-6 Astra research threads completed before this assignment: components, flows, assurance. No additional research agents were launched.
Their supplied reconciled findings are used below and checked against local source. Original tool-output transcripts were supplied at temporary paths and were not accessed; no claim is made to preserve those full transcripts here.

## Reconciled assurance findings
- No GitHubLegacy or raw GitHub platform authentication. Endpoint metadata selects internal/run/broker/cookie; otherwise Entra. Broker eligibility includes AuthenticatedSelfOrMcp. Authentication precedes persisted resource authorization.
- MCP requires exact issuer and single audience, keyed RS256, lifetime, subject and mcp:invoke. Assistant uses a separate five-minute broker plus renewal. Idle is resumable; Completed is sealed.
- Current /configure delivers RepositoryAccessToken into AgentHost and tool options. Constrained direct git status / allowlisted gh get process-scoped credentials, not blanket shell injection. This does not meet the normative no-credential contract. No Key Vault-role AgentHost identity.
- Native shell denial, URL approval and custom reporting bypass are distinct paths. Remaining governance requires both AGT and direct containment.
- AKS base counts: API 2, frontend 2, MCP 1, worker 2; worker HPA 2–3. API/worker use Postgres and CSI; MCP has no CSI. Application and preview gateways are distinct.
- Preview provisioning is followed by exact HTTPS URL probing before ready, rollback on failure, and no API direct TCP readiness probe.
- Memory candidates converge into importance/recency joint sorting. Budgets cover selected memories, not the entire context. Output is explicitly untrusted JSON.
- Provider prepare binds signed five-minute operation/project/subject/provider key. Accept re-resolves; mismatch returns replacement context. Private run snapshots use database ownership plus secret storage; invocation guard and live capability fences are separate.

## Scope qualifications
- Authorization overview: persisted membership applies to resource checks; explicitly permitted trusted internal-service endpoints can bypass that lookup (ProjectAuthorization.cs:56-85). The diagram is not a claim that every internal call queries project membership.
- Provider invocation: operation matching means the supported operation/model-provider boundary and expected provider identity, not a claim that RunModelInvocationGuard literally compares operation-name strings. RunOrchestrator.cs:826-855 selects the accepted operation; RunModelInvocationGuard.cs:11-48 checks the durable boundary and live Copilot capability.
- API/store arrows describe the API's append/reload operation, not a claim that returned history travels from API into storage. Request/response exchanges are aggregated; no unshown reverse connector is implied to exist.
- Native networking router glyphs identify Gateway API routing, not Azure Application Gateway. Durable services is an explicitly labeled aggregation of Postgres and Azure Files, not a claim that Azure Files is a database.

## Node and symbol evidence
- n0 **Authorized run** — `native:uml`; apps/Agentweaver.Api/Endpoints/RunEndpoints.cs; docs/deep-dive/sandbox-pod-execution.md
- n1 **SandboxClaim** — `custom:agentweaver`; apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs; apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs
- n2 **AgentHost** — `native:kubernetes`; apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs
- n3 **Run experience** — `custom:agentweaver`; docs/deep-dive/sandbox.md:65-70; packages/Agentweaver.AgentRuntime/InMemoryToolApprovalGate.cs
- n4 **Isolated workspace** — `native:flowchart`; docs/deep-dive/sandbox.md:12-24; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs
- n5 **Preview or review** — `native:flowchart`; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-330; docs/deep-dive/sandbox-pod-execution.md

## Every connector's evidence map
- e0: n0 → n1: start. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs; docs/deep-dive/sandbox-pod-execution.md; apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs; apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs
- e1: n1 → n2: configure. Evidence: apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs; apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs; apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs
- e2: n2 → n4: tools. Evidence: apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs; docs/deep-dive/sandbox.md:12-24; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs
- e3: n2 → n3: events / approvals. Evidence: apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs; docs/deep-dive/sandbox.md:65-70; packages/Agentweaver.AgentRuntime/InMemoryToolApprovalGate.cs
- e4: n4 → n5: work artifacts. Evidence: docs/deep-dive/sandbox.md:12-24; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-330; docs/deep-dive/sandbox-pod-execution.md

## Visual sources and rights
Started from `docs/diagrams/drawio/fluent-template.drawio`; loaded all four JSON entries from `fluent-library.xml`. The custom card entry contains malformed embedded XML (an unescaped closing div), so it is used as a style reference, not imported as executable XML. The valid template supplies editable primitives. Removed the template's invisible metadata vertex and all template example content; resized to true A5.
Warm Fluent tones, Segoe UI, rounded 16-unit near-white cards, 5-unit semantic accents, tiered boundaries, metadata and badges derive from the committed design-system assets. Native draw.io bundled UML, Kubernetes, Azure, database and flowchart shapes remain editable. No downloaded logos or third-party raster assets.
Native symbols are bundled with the pinned draw.io Desktop 31.4.5 distribution (https://github.com/jgraph/drawio-desktop; Apache-2.0 application license); vendor marks remain their owners' marks and are used solely to identify the represented technology.

## Preconditions and operation notes
Shared dirty issue worktree was explicitly assigned. No git mutations, commits, document updates, global inventory writes, or pipeline/product edits are performed.
The skill was read directly from the worktree because it is not registered in this runtime's skill catalog.
An initial library-loading attempt decoded XML before JSON and failed; parsing the raw mxlibrary JSON first resolves it without modifying the shared library.
