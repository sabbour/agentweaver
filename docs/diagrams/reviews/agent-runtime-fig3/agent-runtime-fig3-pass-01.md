# agent-runtime-fig3: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 7034 -> 26907 (3.825277x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Workflow-to-leaf routing crosses the Prepare context and Reserve run cards.
- Validate/apply-to-commit routing crosses Prepared writeback and obscures relationship ownership.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs:183-245; apps/Agentweaver.Api/Git/WorktreeManager.cs:512-672; apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:326-368`.
