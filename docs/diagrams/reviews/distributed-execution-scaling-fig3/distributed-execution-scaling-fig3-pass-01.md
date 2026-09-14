# distributed-execution-scaling-fig3: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 5442 -> 18611 (3.419882x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Worker persistence crosses AgentHost, visually suggesting pod-to-database ownership.
- HPA and Deployment use Pod icons; the PostgreSQL icon reads as a circle rather than a database.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `k8s/base/worker-hpa.yaml:62-83; apps/Agentweaver.Api/Program.cs:521-528,1043-1086; apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:45-121`.
