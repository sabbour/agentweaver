# sandbox-pod-execution-fig7: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 5881 -> 19431 (3.30403x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Unknown fallback routing crosses the API approval card.
- Request/result arrows between the HTTP client and pod gate overlap, preventing a clean two-way trace.
- The durable gate's database-style circle does not communicate a gate decision.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1956-2084; apps/Agentweaver.Api/Sandbox/AgentHostApprovalHttpClient.cs:74-108; apps/Agentweaver.AgentHost/Program.cs:804-815,962-987`.
