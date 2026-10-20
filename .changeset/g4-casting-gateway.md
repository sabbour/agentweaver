---
"Agentweaver.Gateway": minor
"Agentweaver.Web": minor
"Agentweaver.Projects.Config": patch
---

Expose the existing Projects casting proposal, transfer, template, and role
endpoints through the Gateway with matching OpenAPI schemas and web contract
types. Keep revision checks and authorization in Projects, and match the
Gateway and owner casting import body limits at 1 MiB.
Buffer every explicitly bounded Gateway route before contacting its owner,
including 3 MiB skill-content requests. Reject oversized bodies with 413
`request_body_too_large`, including requests without `Content-Length`.
Document owner authorization denials (403) for casting writes.
