# distributed-execution-scaling-fig4: pitch inspection

Takeaway: Cross-replica delivery polls shared rows; local notifications are not a distributed bus.

Audience: operators and contributors reading the execution deep dives.
One editable A5 landscape page. Exported with draw.io Desktop 31.4.5.
Opened enlarged and in the saved A5-scale contact sheets.
Sparse generic cards are a sketch, not an approved native-notation composition.
Missing hierarchy, relationship detail and process boundaries require redesign.

Evidence: `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:183-234; apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:145-169,224-288,409-419; tests/Agentweaver.Tests/PostgresIntegration/RunEventStreamPostgresTests.cs:14-75`.

Fluent palette/template/library: repository-owned docs/diagrams/drawio assets.
Native symbol proposals and source-backed content: content-models.json and the
three research reports in the canonical-pod-process-boundaries review directory.
