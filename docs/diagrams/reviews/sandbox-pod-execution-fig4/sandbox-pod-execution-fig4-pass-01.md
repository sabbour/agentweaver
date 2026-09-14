# sandbox-pod-execution-fig4: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 6297 -> 25361 (4.027473x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Read-only/writable and equal/changed-tree branches are mostly disconnected.
- The missing branch connectors hide the materialization-to-publication lifecycle even though card text describes it.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:98-148,285-378,524-550; apps/Agentweaver.Api/Git/WorktreeManager.cs:521-672; tests/Agentweaver.Tests/AgentHost/PodLocalWorkspaceManagerTests.cs:224-256`.
