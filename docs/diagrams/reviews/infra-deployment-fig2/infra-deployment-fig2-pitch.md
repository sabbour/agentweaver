# infra-deployment-fig2: pitch inspection

Takeaway: API and worker consume vault secrets; AgentHost receives brokered configuration.

Audience: operators and contributors reading the execution deep dives.
One editable A5 landscape page. Exported with draw.io Desktop 31.4.5.
Opened enlarged and in the saved A5-scale contact sheets.
Sparse generic cards are a sketch, not an approved native-notation composition.
Missing hierarchy, relationship detail and process boundaries require redesign.

Evidence: `scripts/azure/steps/15-setup-identity.mjs:245-438; k8s/base/secret-provider-class.yaml:16-61; apps/Agentweaver.Api/Program.cs:231-250; k8s/base/mcp-deployment.yaml:45-83`.

Fluent palette/template/library: repository-owned docs/diagrams/drawio assets.
Native symbol proposals and source-backed content: content-models.json and the
three research reports in the canonical-pod-process-boundaries review directory.
