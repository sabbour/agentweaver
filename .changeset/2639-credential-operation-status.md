---
"Agentweaver.Abstractions": patch
"Agentweaver.Projects.Config": patch
"Agentweaver.Identity.Broker": patch
"Agentweaver.Gateway": patch
---

Add read-only scoped BYOK credential operation status routes (`GET /api/v1/platform/byok-providers/{providerId}/credential/operations/{operationId}` and `GET /api/v1/projects/{projectId}/byok-providers/{providerId}/credential/operations/{operationId}`) backed by a read-only Projects & Config operation lookup (`POST /internal/byok/credential-publications/v2/read-operation`), requiring Broker completion before reporting terminal `published` or `disabled` status and omitting secret versions, credential revisions, and configuration hashes.
