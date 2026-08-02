---
"agentweaver": patch
---

Fixed public-access Postgres (`--postgres-access-mode public`) deployments where the generated Kubernetes egress policies still allowlisted the private delegated-subnet CIDR, so API and worker pods could never reach the Flexible Server (`Npgsql.NpgsqlException: Failed to connect ... TimeoutException`) even with the Azure-side firewall and public network access configured correctly. Public mode now emits FQDN-based `CiliumNetworkPolicy` objects (`allow-api-postgres-egress-fqdn` / `allow-worker-postgres-egress-fqdn`) that allow port 5432 to `<PG_SERVER_NAME>.postgres.database.azure.com` via Cilium `toFQDNs`, which stays correct when Azure changes the server's public IP. Private mode keeps the existing ipBlock `NetworkPolicy` objects unchanged.
