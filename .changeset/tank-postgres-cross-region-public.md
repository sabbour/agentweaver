---
"agentweaver": minor
---

Added `azure:provision-infra` support for provisioning PostgreSQL Flexible Server in a different Azure region from the AKS cluster. The installer now exposes `--postgres-location` / `PG_LOCATION` and `--postgres-access-mode` / `PG_ACCESS_MODE`, fails closed when a cross-region server is requested without switching to public access, and can create the Azure-services-only firewall rule needed for public-access Flexible Server deployments.
