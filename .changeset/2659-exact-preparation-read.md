---
"Agentweaver.Projects.Config": patch
---

Add a default-off private `GET /internal/policy-source/platform/preparations/{preparationId}` read endpoint for the Policy worker peer using distinct `PreparationWorkerPins` and the `environment-platform-egress-writer.agentweaver.svc.cluster.local` SAN.
