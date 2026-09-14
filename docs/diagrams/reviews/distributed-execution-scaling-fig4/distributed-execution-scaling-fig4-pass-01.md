# distributed-execution-scaling-fig4: incomplete first upgrade

Added Fluent accents, native-symbol candidates, title/subtitle/metadata hierarchy,
pill badges and source-backed relationship detail. Pinned Desktop export succeeded.

Meaningful XML: 6643 -> 26913 (4.051332x; required 9x).
Metric: visible-semantic-canonical-xml-v1. The growth gate FAILED.
No baseline was replaced and no invisible/padding growth workaround was used.

Enlarged PNG opened: yes. A5-scale contact-sheet PNG opened: yes.
Recorded first-upgrade findings:

- Database acknowledgement crosses RecordNext and EF append.
- Replica poll routes cross browser cards and share a rail, visually suggesting direct browser database access.
- Person icons conflate software watchers with human actors.

These findings are not a complete final arrow trace.

Passes 2-4 were not performed: pass 1 must qualify before correction-only passes.
No final pass, final arrow trace, approved native-asset inventory or promotion.

Evidence: `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:183-234; apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:145-169,224-288,409-419; tests/Agentweaver.Tests/PostgresIntegration/RunEventStreamPostgresTests.cs:14-75`.
