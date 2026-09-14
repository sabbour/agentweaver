# distributed-execution-scaling-fig5: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 5440 -> 18198 (3.345221x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- The expires edge starts at A renews and ends at A stops renewing, reversing the causal order of stop then expiry.
- Lease ownership and renewal are not visibly connected; Pod icons do not identify worker processes precisely.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-93; apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:84-159,593-610; tests/Agentweaver.Tests/PostgresIntegration/PostgresStoreTests.cs:124-187`.
