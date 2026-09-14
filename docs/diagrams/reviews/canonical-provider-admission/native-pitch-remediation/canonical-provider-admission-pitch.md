# canonical-provider-admission: native Fluent responsibility pitch

Audience: Agentweaver documentation readers. Takeaway: Signed admission context freezes execution choice; live capability checks remain separate.

Target page: `docs/deep-dive/auth-security.md`. Stable image: `docs/diagrams/canonical-provider-admission.png`.

One editable, uncompressed A5 landscape page: 827 × 583 logical units, pageScale=1, 100 logical units/inch. Export scale does not define the page size. The PNG is content-cropped by the official border-16 / scale-2 recipe; the print preview is an inspection aid, not a claim about PNG physical metadata.

The initial pitch has two distinct responsibility cards, visible native symbols, warm paper, near-white surfaces, Segoe UI title/subtitle text, Consolas metadata, 16-unit rounded cards, shadows, 5-unit accents and colored pill hierarchy. It is a compact source-backed model rather than empty generic boxes. The downward arrow is a high-level responsibility handoff, expanded into the exact component relationships during pass 1.

Started from the repository Fluent template; checked fluent-library.xml and design-system.json. The completed original three independent GPT-6 Astra research threads were supplied and read; no further or nested agents were launched:
- `docs/diagrams/reviews/canonical-provider-admission/research-thread-01.md`
- `docs/diagrams/reviews/canonical-provider-admission/research-thread-02.md`
- `docs/diagrams/reviews/canonical-provider-admission/research-thread-03.md`

Current implementation/configuration/tests and written docs outrank legacy visuals. Retained evidence and reconciled exceptions are in `../content-model.json` and `../evidence.md`; the new native inventory below supersedes the old networking-router mapping.

Native symbols come from the pinned draw.io Desktop 31.4.5 bundled libraries: mxgraph.kubernetes pod/deploy/svc; built-in flowchart process/decision, UML actor and database cylinder; img/lib/azure2/containers/Kubernetes_Services.svg and img/lib/azure2/identity/Azure_Active_Directory.svg where shown. Azure icons remain unmodified vendor assets used to document Azure services, subject to Microsoft's Azure architecture-icon terms; draw.io library/stencil distribution follows jgraph/drawio licensing and retained third-party notices. No trademark ownership or endorsement is claimed. References: https://learn.microsoft.com/azure/architecture/icons/ and https://github.com/jgraph/drawio. The AKS symbol identifies the Azure AKS environment hosting App Routing. The Gateway kind is Kubernetes Gateway API with gatewayClassName=approuting-istio, NOT an Azure Application Gateway resource. Product-specific concepts retain custom:agentweaver framing.

## Pitch symbol inventory

```json
[
  {
    "id": "s0",
    "classification": "native:flowchart",
    "shape": "rhombus",
    "image": null
  },
  {
    "id": "s1",
    "classification": "native:database",
    "shape": "cylinder",
    "image": null
  }
]
```

## PNG inspection and handoff

Opened the pitch contact sheet at print scale and the actual pitch PNG enlarged before pass 1. Both native symbols rendered; card titles, metadata, pills and the downward connector are visible and unclipped. Pass 1 owns the complete detailed redesign; later passes only correct defects.

## Source-backed node evidence

- Prepare context: `apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:205-207,256-290`
- Accept request: `apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293-358`
- Accepted plan: `apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:121-183; apps/Agentweaver.Api/Auth/EffectiveModelProviderResolver.cs`
- Run snapshot: `apps/Agentweaver.Api/Auth/RunModelProviderSnapshotStore.cs:45-77,83-155; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:796-825`
- Invocation guard: `apps/Agentweaver.Api/Auth/RunModelInvocationGuard.cs:11-48; apps/Agentweaver.Api/Runs/RunOrchestrator.cs:826-855`
- Capability fences: `apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:35-123; apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs`
