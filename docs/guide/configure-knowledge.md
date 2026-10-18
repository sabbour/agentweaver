# Configure Knowledge memory

Knowledge stores project- and agent-scoped Memory and Decision records. It
keeps revisions and composes authorized context for a run. Knowledge is not
the Sessions journal: Sessions stores run events and session history.

The default v1 catalog selects native PostgreSQL Memory. Cosmos and Redis
options exist in source, but the checked-in catalog does not register those
providers. Adding their settings to a ConfigMap does not switch the provider.
The Knowledge Deployment also imports the immutable
`projects-config-provider-catalog` ConfigMap under the
`ProjectsConfig__ProviderCatalog__` prefix. This catalog is separate from
`knowledge-runtime-config` and is required for Knowledge startup.

## Before you begin

You need:

- A PostgreSQL Flexible Server and database that the Knowledge workload can
  reach over its approved private route.
- A federated managed identity for the Knowledge ServiceAccount.
- An Entra PostgreSQL role whose name exactly matches the Knowledge managed
  identity name.
- The approved Projects & Config and Events service addresses and token
  audiences.

The runtime connection is password-free. The managed identity requests an
Entra access token and PostgreSQL validates it as the matching database role.
For example, if the Knowledge managed identity is `agentweaver-id-knowledge`,
use `agentweaver-id-knowledge` as the PostgreSQL `Username` and create the
Entra database role with that same name. An Entra administrator listing does
not prove that this runtime role exists or has the required grants.

## Configure native PostgreSQL Memory

1. **Create the database role and grants.** Use the approved database
   bootstrap and grant process. Create the role for the Knowledge managed
   identity and apply the Knowledge schema grants before starting the service.
2. **Set the connection and identity values.** In
   `deploy/k8s/runtime-configs/knowledge-runtime-config.json`, set the
   password-free `ConnectionStrings__Knowledge`, tenant ID, client ID, and
   projected token-file path. Set the identity issuer and audience and the
   Projects & Config and Events HTTPS service roots.
3. **Set the provider identity.** Configure:

   | Setting | Example or contract |
   | --- | --- |
   | `Knowledge__Provider__ResourceId` | `agentweaver.knowledge` |
   | `Knowledge__Provider__DatabaseName` | The PostgreSQL database name, for example `agentweaver`. |
   | `Knowledge__Provider__ResourceGeneration` | `1` for a new store. Must be at least `1`. |
   | `Knowledge__Provider__Schema` | Defaults to `knowledge`; use a lowercase PostgreSQL schema name that is not reserved. |
   | `Knowledge__Provider__OptionsRevision` | Defaults to `native-postgres-memory-v1`. |
   | `Knowledge__Provider__OptionsSchemaVersion` | Defaults to and must equal `1`. |

4. **Validate the database identity.** The connection string must use the
   Knowledge managed identity's PostgreSQL role as `Username`. Do not put a
   password in the connection string. TLS uses `SSL Mode=VerifyFull` and the
   PostgreSQL server name as the certificate host.
5. **Render and apply the configuration.** For a first installation, set the
   values before applying the bundle. The Knowledge ConfigMap is immutable, so
   Kubernetes rejects an in-place data change; reapplying the bundle or
   restarting the workload alone does not load updated values. For an existing
   installation, follow the
   [immutable ConfigMap update procedure](../reference/runtime-configuration.md#update-an-immutable-configmap)
   before restarting Knowledge. See
   [deploy Agentweaver to Azure](./deploy-to-azure.md) for the installation
   sequence.

## Keep the store identity stable

`Knowledge:Provider:ResourceId` is a stable label stored with Memory data. It
must never change. For the documented deployment profile, use
`agentweaver.knowledge`. Do not replace it with a database server resource ID
or provider adapter ID.

`ResourceGeneration` identifies one incarnation of the store. It must be at
least `1`. Increase it only when you wipe or replace the store. Do not increase
it for a deployment, restart, options revision, or schema migration. A changed
resource identity or generation is a different store binding and does not
silently inherit a run's previous provider pin.

## Understand optional source candidates

The source contains optional Cosmos and Redis Memory adapters. Neither is
registered in the checked-in provider catalog, so the default deployment
cannot select them.

| Candidate | Required contract when registered |
| --- | --- |
| `cosmos.memory` | Root HTTPS account endpoint; database and container names; stable resource ID; generation of at least `1`; options revision; schema version `1`. The existing container must use the `/projectId` partition key, the required search composite index, and the non-expiring default TTL. The adapter does not create the database or container. |
| `redis.memory` | `rediss://` endpoint with an explicit port; stable resource ID; generation of at least `1`; options revision; schema version `1`; database index `0` through `15` (default `0`); key prefix (default `agentweaver:knowledge`); and either both ACL username and password or neither. The service must be a compatible standalone primary with AOF enabled, no eviction, no replicas or cluster, and non-expiring data. |

The Cosmos and Redis option readers are optional. They return no provider
options when their configuration section is absent. They do not add the
provider to the catalog or switch Knowledge away from PostgreSQL.

## Verify the result

1. Render the root installation with `kubectl kustomize deploy\k8s`.
2. Confirm the Knowledge runtime ConfigMap contains the approved non-secret
   values and that the database username matches the runtime identity role.
3. After applying the reviewed bundle, check the Knowledge Deployment:

   ```powershell
   kubectl rollout status deployment/knowledge -n agentweaver
   kubectl logs deployment/knowledge -n agentweaver --tail=100
   ```

4. Send an authorized Knowledge request through the configured Gateway and
   confirm the response belongs to the requested project and agent.

Do not include bearer tokens, database tokens, passwords, or secret values in
logs or support reports. Startup and a successful request do not prove that
another provider is registered or that the database can be recovered.

## Related information

- [Knowledge and Memory architecture](../architecture/knowledge-memory.md)
- [Provider configuration](./configure-providers.md)
- [Runtime configuration reference](../reference/runtime-configuration.md)
- [Manage Microsoft Entra roles for PostgreSQL Flexible Server](https://learn.microsoft.com/azure/postgresql/security/security-manage-entra-users)
- [Connect to PostgreSQL Flexible Server with a managed identity](https://learn.microsoft.com/azure/postgresql/security/security-connect-with-managed-identity)
