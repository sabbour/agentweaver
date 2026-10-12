---
"Agentweaver.Projects.Config": patch
---

Add an explicitly selected Projects runtime deployment profile for the existing
1.x target. Require the Projects runtime identity, passwordless PostgreSQL,
HTTPS authentication, a pinned product image, and an existing operator-reviewed
provider catalog ConfigMap with its source commit.

Rendering does not create the catalog, grant database rights, apply migrations,
publish an image, or deploy the service.
