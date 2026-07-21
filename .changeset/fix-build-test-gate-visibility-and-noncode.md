---
"agentweaver": patch
---

Fix Build & Test gate coordinator behavior (#386, #387). The gate now renders as a `planned` node in
the run tree from the start — the `GET /api/runs/{id}/graph` endpoint and the topology-shape
`coordinator.graph` emissions resolve the actual assembly gates from the selected workflow instead of
falling back to the RAI + Human Review defaults that omitted `build_test` until execution reached it.
The coordinator also drops the platform Build & Test gate for non-code-producing work plans (all
subtasks are planning-phase deliverables such as research, PRDs, or design docs) so those runs no
longer loop indefinitely at a gate that has no code to build or test.
