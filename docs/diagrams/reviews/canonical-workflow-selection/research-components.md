# Completed independent component thread — relayed findings

Provenance: parent reports this separate GPT-6 Astra thread completed before authoring. This is a preserved handoff summary with direct source reconciliation, not a verbatim raw transcript. Catalog YAML defines the authored nodes; do not invent merge, PR or Scribe stages. DefaultWorkflowTemplate is separate and explicitly includes those stages. Evaluation run/collect are prompts, not fan-out/fan-in. Selection is trigger-agnostic.

- `available`: Load candidates — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:281–296`; native:flowchart.
- `explicit`: Explicit override? — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:299–329`; native:flowchart.
- `conversation`: Conversational choice? — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:333–349`; native:flowchart.
- `count`: Candidate count — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:359–375`; native:flowchart.
- `model`: Ask selection model — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:378–387`; native:flowchart.
- `validate`: Usable candidate? — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:378–387; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`; native:flowchart.
- `selected`: Selected workflow — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:387–389`; native:flowchart.
- `explicit-result`: Explicit choice — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:311–321`; native:flowchart.
- `silent`: Silent choice — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:362–374`; native:flowchart.
- `fallback`: Model fallback — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:378–387; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`; native:flowchart.
- `outer`: Outer fallback — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:391–405`; native:flowchart.
