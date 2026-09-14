# distributed-execution-scaling-fig3: pitch inspection

Takeaway: Separate public traffic and orchestration responsibility without claiming hard role isolation.

Audience: operators and contributors reading the execution deep dives.
One editable A5 landscape page. Exported with draw.io Desktop 31.4.5.
Opened enlarged and in the saved A5-scale contact sheets.
Sparse generic cards are a sketch, not an approved native-notation composition.
Missing hierarchy, relationship detail and process boundaries require redesign.

Evidence: `k8s/base/worker-hpa.yaml:62-83; apps/Agentweaver.Api/Program.cs:521-528,1043-1086; apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:45-121`.

Fluent palette/template/library: repository-owned docs/diagrams/drawio assets.
Native symbol proposals and source-backed content: content-models.json and the
three research reports in the canonical-pod-process-boundaries review directory.
