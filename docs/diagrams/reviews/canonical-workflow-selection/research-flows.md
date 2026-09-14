# Completed independent flow thread — relayed findings

Provenance: parent reports a separate completed GPT-6 Astra direction/flow thread. This handoff preserves the reported conclusions and reconciles every relationship against current code. Default is merge → create/reuse PR → Scribe, not a proved git push. Explicit/conversational choices precede singleton handling; malformed/unknown choices permit two model attempts, exceptions fall back immediately. Runtime fallback prefers default/standard, then a non-code-review candidate, finally the first candidate. Outer coordinator fallback is the project default.

- `available → explicit`: unconditional — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `explicit → explicit-result`: available — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `explicit → conversation`: absent / invalid — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `conversation → explicit-result`: available — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `conversation → count`: absent / invalid — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `count → silent`: 0 or 1 — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `count → model`: 2+ — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `model → validate`: response — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `model → fallback`: exception — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `validate → selected`: accepted — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `validate → model`: retry once — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `validate → fallback`: 2 unusable — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `fallback → selected`: emit choice — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `available → outer`: outer catch — `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
