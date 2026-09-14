# canonical-sandbox-pod-evolution: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 5475 -> 18196 (3.32347x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Before/Now rows lack explicit enclosing worker and pod boundaries.
- The command connector label consumes nearly the whole inter-card gutter.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `apps/Agentweaver.Api/Sandbox/RemoteWorkflowAgentFactory.cs:9-65; apps/Agentweaver.Api/Program.cs:1043-1074; docs/deep-dive/sandbox-pod-execution.md`.
