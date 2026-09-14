# Completed independent flow thread — relayed findings

Provenance: parent reports a separate completed GPT-6 Astra direction/flow thread. This handoff preserves the reported conclusions and reconciles every relationship against current code. Default is merge → create/reuse PR → Scribe, not a proved git push. Explicit/conversational choices precede singleton handling; malformed/unknown choices permit two model attempts, exceptions fall back immediately. Runtime fallback prefers default/standard, then a non-code-review candidate, finally the first candidate. Outer coordinator fallback is the project default.

- `agent → rai`: unconditional — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:120–121`.
- `rai → agent`: revise — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:124–126`.
- `rai → terminal-safety-failed`: safety-failed — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:127–129`.
- `rai → scribe`: no-changes — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:130–132`.
- `rai → review`: review — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:133–135`.
- `review → merge`: approved — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:138–140`.
- `review → agent`: request-changes — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:141–143`.
- `review → terminal-declined`: declined — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:144–146`.
- `merge → push-pr`: merged — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:149–151`.
- `merge → review`: blocked — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:152–154`.
- `push-pr → scribe`: unconditional — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:157–158`.
- `scribe → done`: unconditional — `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:159–160`.
