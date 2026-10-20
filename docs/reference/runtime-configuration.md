# Runtime configuration reference

This reference describes the non-secret ConfigMap keys in
`deploy/k8s/runtime-configs/*.json` and the provider options that validate
them. It covers the v1 source contract; it does not turn a source candidate
into a deployed or selectable provider.

## Configuration rules

- The runtime ConfigMaps use environment-variable names with `__` between
  configuration sections. In C# configuration paths, `__` corresponds to
  `:`.
- The checked-in JSON files are source snapshots. Replace target-specific
  values only with approved values for the selected environment.
- Do not store passwords, OAuth client secrets, PFX passwords, tokens, or
  private keys in these JSON files. Supply them through the operator-managed
  Kubernetes Secrets named by the deployment.
- The PostgreSQL workload connections use Entra workload identity. They omit
  passwords and use `SSL Mode=VerifyFull`.
- A provider `ResourceId` is a stable label stored with the resource data. It
  must never change. `ResourceGeneration` must be at least `1`; increase it
  only after the resource is wiped or replaced.
- Unless a row gives a default, the setting is required by the owning host or
  provider. A required value has no development fallback.
- A grouped path such as `<Service>__Database__WorkloadIdentity__ClientId`
  represents that exact suffix under each service prefix named in its row.

The checked-in P0 runtime snapshots contain the verified non-secret managed
AKS hostnames, PostgreSQL endpoint and role names, workload-identity client
IDs, and Key Vault URI. PostgreSQL bootstrap and the P1 initializer have
succeeded and the database result has been independently verified. The nine
shared-service runtime snapshots no longer carry the
`agentweaver.io/runtime-config-status: incomplete` or
`agentweaver.io/runtime-config-incomplete-reason` annotations.

The deploy gate rejects shared-service runtime ConfigMaps marked `incomplete`.
Completion is represented by removing both annotations after resolving and
verifying the inputs, not by setting a new status value. Completed ConfigMaps
must contain no unresolved `CHANGEME-*` markers and must still satisfy the
normal installation and workload-identity checks. Removing the annotations
does not provision Secrets, deploy workloads, or prove live service readiness.

Identity Broker signing is a separate deploy-time prerequisite. The
`scripts/azure/lib/identity-broker-state.mjs` helper creates or validates the
`identity-broker-signing` Kubernetes Secret and the retained
`identity-broker-key-ring` PVC. Signing PFX/password material goes directly
into the Kubernetes Secret; this path does not require a Key Vault write.
Existing signing state is validated, not replaced or rotated. The Broker's
Key Vault settings remain required for secret redemption, not signing-state
persistence.

For end-to-end deployment, see
[Deploy Agentweaver to Azure](../guide/deploy-to-azure.md). For provider
selection, see [Configure providers](../guide/configure-providers.md).

## Update an immutable ConfigMap

Most checked-in runtime ConfigMaps, including `knowledge-runtime-config`, set
`immutable` to `true`. Kubernetes rejects changes to an immutable ConfigMap's
data. Editing its JSON source and applying the bundle does not update the live
ConfigMap. A Deployment also does not restart automatically when a referenced
ConfigMap changes.

For a first installation, resolve and review all settings before applying the
bundle. For an existing installation, replace an immutable ConfigMap during an
approved maintenance window:

1. Update the reviewed JSON source and render the complete bundle. Confirm the
   rendered ConfigMap has the expected name, namespace, data, and
   `immutable: true`.
2. Delete only the exact ConfigMap being replaced. For example:

   ```powershell
   kubectl delete configmap knowledge-runtime-config -n agentweaver
   ```

3. Recreate it from the rendered bundle:

   ```powershell
   kubectl apply -k deploy\k8s
   ```

4. Restart the affected Deployment and wait for its rollout:

   ```powershell
   kubectl rollout restart deployment/knowledge -n agentweaver
   kubectl rollout status deployment/knowledge -n agentweaver
   ```

Kubelet caches immutable ConfigMap data per node and does not watch it for
changes. If a pod that references the ConfigMap keeps running on a node while
you delete and recreate the ConfigMap under the same name, new pods on that
node can still receive the old values. A `rollout restart` does not clear this
cache. If the restarted pods still show old values, scale every workload that
references the ConfigMap to `0` and wait for its pods to terminate. Then scale
it back up. Before you rely on the new value, check it inside the new pod with
`kubectl exec deployment/<name> -n agentweaver -- printenv` or by reading the
mounted file.

Deleting the ConfigMap creates a short interval when a replacement pod cannot
start. Keep existing pods running, apply the bundle promptly, and verify the
new ConfigMap before restarting the Deployment. Do not use a broad delete or
remove the `immutable` setting to bypass the replacement process.

### Guarded pre-acceptance ConfigMap patch upgrades

The source-bound `release:deploy-bootstrap` flow has a narrowly scoped
replacement path for `agenthost-runtime-v1` and
`projects-config-runtime-config`. AgentHost's immutable `appsettings.json`
includes the receipt-verified image digest and measured image size. The
Projects & Config map contains the approved privileged-setup identity values.
Both maps can change between patch releases.

The command compares each live map's data with the source-rendered map. When an
existing immutable map differs, `--dry-run` reports its name. With `--execute`,
the command validates the rest of the rendered bundle, checks namespace-scoped
ConfigMap create and name-scoped delete permissions for each changed map,
force-replaces only those maps from the source-rendered objects, and then
applies the bundle. Unchanged data follows the normal apply path.

This exception is limited to `agenthost-runtime-v1` and
`projects-config-runtime-config`. It does not replace
`environment-cilium-config`, `projects-config-provider-catalog`, or any other
ConfigMap. Other immutable runtime maps still require the reviewed maintenance
procedure above.

`events-and-sessions-runtime-config` is mutable in the checked-in profile.
Apply its reviewed data with the bundle, then restart the Events & Sessions
Deployment so the process reads the new environment.

## PostgreSQL connection and workload identity

These settings appear in the service runtime maps. Use the prefix shown in the
first column to form the complete key.

| Prefix or key | Contract | Default and how to obtain it |
| --- | --- | --- |
| `ConnectionStrings__IdentityBroker`, `ConnectionStrings__Environment`, `ConnectionStrings__EventsAndSessions`, `ConnectionStrings__Knowledge`, `ConnectionStrings__Orchestrator`, `ConnectionStrings__ProjectsConfig` | Password-free PostgreSQL connection string. Set the correct server, database, and Entra role for the owning service; require `SSL Mode=VerifyFull`. The runtime role name must match that service's managed identity name. | No default. Obtain the server FQDN with `az postgres flexible-server show --resource-group <resource-group> --name <server> --query fullyQualifiedDomainName --output tsv`. Use the database and role values from the approved database bootstrap; do not infer the role from the Entra administrator listing. |
| `<Service>__Database__WorkloadIdentity__TenantId` | Entra tenant GUID for the database workload identity. Present for `Environment`, `EventsAndSessions`, `Knowledge`, `Orchestrator`, and `ProjectsConfig`. | No default. Read the selected tenant with `az account show --query tenantId --output tsv`. |
| `<Service>__Database__WorkloadIdentity__ClientId` | Client ID of the user-assigned managed identity federated to that service's Kubernetes ServiceAccount. Present for the same five services. | No default. Read it with `az identity show --resource-group <resource-group> --name agentweaver-id-<service> --query clientId --output tsv`. Confirm that the federated credential subject names the exact namespace and ServiceAccount. |
| `<Service>__Database__WorkloadIdentity__TokenFilePath` | Absolute path to the projected workload-identity token file in the pod. Present for the same five services. | No default. Use the path configured by the workload-identity projection in the checked-in ServiceAccount and Deployment profile. Do not invent a path. |

The `IdentityBroker` database connection is configured at
`ConnectionStrings__IdentityBroker`. Its separate migration connection is
operator-supplied to the migration Job and is not a runtime ConfigMap setting.
The runtime identity does not own the schema or run migrations.

### Identity Broker migration history and grants

At startup, the Broker calls EF Core `GetPendingMigrationsAsync`. EF reads
`identity_broker.__ef_migrations_history` to compare applied migrations with
the executable. The migration role, `agentweaver-id-identity-broker-migration`
in the checked-in profile, owns the schema and history table. The runtime
role, `agentweaver-id-identity-broker`, requires schema `USAGE` and table
`SELECT`. It must not receive history writes or schema creation.

The separate `--migrate` command requires
`IdentityBroker__Migration__RuntimeRole` in addition to its migration connection
and workload-identity settings. The checked-in migration Job supplies the
runtime role explicitly. After migrations, the command applies the canonical
[runtime grant definition](https://github.com/sabbour/agentweaver/blob/v1/infra/bicep/postgres-identity-runtime-grants.sql)
as the migration owner and verifies the exact table permissions. Repeating
the command applies missing grants even when no migration is pending.

Initial database bootstrap grants schema access only. If migrations have not
run, bootstrap reports `IDENTITY_POSTGRES_RUNTIME_GRANTS_PENDING`; that result
does not prove runtime readiness. After migration, read-only bootstrap
verification checks the full explicit table set, including history `SELECT`
and the absence of history writes. Missing grants fail verification without
repairing them.

For an existing database that fails with SQLSTATE `42501` on this history
table, obtain approval before changing permissions. The narrow repair in the
`agentweaver` database on `agentweaver-pg.postgres.database.azure.com` is:

```sql
GRANT SELECT ON TABLE identity_broker.__ef_migrations_history
TO "agentweaver-id-identity-broker";
```

Run it through the approved private database connection as the table owner,
`agentweaver-id-identity-broker-migration`, or an approved administrator with
grant authority on that table. This lets the runtime check applied migrations;
it does not let the runtime change history or run migrations.

Merging source does not change live grants. After separate approval, running
the migration Job with an image built from this fix applies the canonical
grants without a manual `GRANT`. Rerunning an older migration image does not.
The guarded operator bootstrap can also apply the canonical grant file after
migration; it changes all listed table grants, not just history access.

## Identity Broker settings

The `identity-broker-runtime-config.json` map supplies the issuer, upstream
OpenID Connect provider, client registrations, data-protection path, and
Key Vault redemption settings. It also supplies the optional browser origin
used for direct Broker calls. Keep client secrets and signing material in
Secrets.

| Setting | Contract |
| --- | --- |
| `IdentityBroker__Issuer` | Required Broker issuer. Use the exact externally reachable HTTPS issuer used by the Gateway and clients. |
| `IdentityBroker__ExternalProvider__Authority` | Required HTTPS authority for the single upstream OpenID Connect provider. Obtain it from that provider's application-registration instructions. |
| `IdentityBroker__ExternalProvider__ClientId` | Required client ID registered with the upstream provider. |
| `IdentityBroker__ExternalProvider__ClientSecret` | Optional. Store it in `identity-broker-client-secrets` only when the upstream client is confidential. A public client uses authorization-code flow with PKCE and has no secret. |
| `IdentityBroker__ExternalProvider__MetadataAddress` | Optional discovery-metadata override. Production normally derives metadata from `Authority`; use an override only when the approved provider requires it. |
| `IdentityBroker__WebOrigin` | Optional exact HTTPS origin for direct Broker browser calls. It must not contain credentials, a path, a query, or a fragment. It is required when GitHub Repo App or Remote MCP OAuth is configured. |
| `IdentityBroker__Clients__<index>__ClientId` | Required ID of an explicitly seeded Broker OAuth client. The Broker does not enable dynamic client registration. |
| `IdentityBroker__Clients__<index>__DisplayName` | Required operator-facing client name. |
| `IdentityBroker__Clients__<index>__Type` | Required enum: `Public` or `Confidential`. PKCE with S256 is required for both types. |
| `IdentityBroker__Clients__<index>__RedirectUris__<item>` | One or more exact redirect URIs registered for that client. The Broker does not accept a caller-selected redirect. |
| `IdentityBroker__Clients__<index>__Scopes__<item>` | One or more explicitly allowed scopes. |
| `IdentityBroker__Clients__<index>__Resources__<item>` | One or more token audiences approved for that client. |
| `IdentityBroker__DataProtectionKeyPath` | Required path for the durable ASP.NET Data Protection key ring. Mount the retained key-ring volume at this path. |
| `IdentityBroker__SecretRedemption__Audience` | Required HTTPS audience for the secret-redemption endpoint. |
| `IdentityBroker__SecretRedemption__VaultHost` | Required exact public Key Vault host used by the Kubernetes egress policy. Set it to the lowercase host from the vault URI derived from `keyVaultName` in the reviewed Bicep parameter file. |
| `IdentityBroker__SecretRedemption__VaultUri` | Required URI of the Key Vault secrets backend. Set it from the same reviewed `keyVaultName`; obtain the deployed value with `az keyvault show --resource-group <resource-group> --name <key-vault-name-from-reviewed-parameters> --query properties.vaultUri --output tsv`. The host must match `IdentityBroker__SecretRedemption__VaultHost`. |
| `IdentityBroker__SecretRedemption__WorkloadIdentityTenantId` | Required Entra tenant GUID for the Broker's Key Vault identity. |
| `IdentityBroker__SecretRedemption__WorkloadIdentityClientId` | Required client ID of the Broker's user-assigned managed identity. |
| `IdentityBroker__SecretRedemption__WorkloadIdentityTokenFilePath` | Required absolute path to the projected Broker workload-identity token file. |

The following `IdentityBrokerOptions` settings are required by the host but
come from the operator-managed Secret or an optional feature configuration,
not from the checked-in non-secret runtime map:

| Configuration path | Contract |
| --- | --- |
| `IdentityBroker:Signing:PfxPath` | Required path to the signing PFX mounted from `identity-broker-signing`. |
| `IdentityBroker:Signing:PfxPassword` | Required PFX password from the same Kubernetes Secret. Never put it in a ConfigMap. |
| `IdentityBroker:RunBinding:*` | Optional Projects run-binding owner address, audience, and certificate/trust-bundle SHA-256 pins. |
| `IdentityBroker:RemoteMcpOAuth:*` | Optional remote OAuth allowlist. When enabled, it requires Projects and Environment owner roots and at least one provider. Each provider requires an issuer, client ID, redirect URI, at least one approved resource and scope, and at least two approved OAuth endpoints. The Broker rejects values outside this allowlist. |
| `IdentityBroker:GitHubRepoApp:*` | Optional GitHub OAuth and App settings. Requires OAuth client ID and secret, HTTPS callback URI, positive App ID, App slug, and exact Key Vault private-key secret ID and version. Store the OAuth secret in the Broker client Secret. |

`IdentityBroker:Clients` must contain at least one client. Each client requires
a nonempty redirect URI list, scope list, and resource list. A confidential
client's secret belongs in `identity-broker-client-secrets`; a public client
omits it. The Broker reconciles only these declared clients at startup.

## Gateway and MCP settings

| Setting | Contract |
| --- | --- |
| `Identity__Issuer` | Required absolute HTTPS Broker issuer. A path is allowed when it is part of the issuer. |
| `Identity__Audience` | Required absolute HTTPS audience. Do not include credentials, a query, or a fragment. |
| `Gateway__Owners__Projects` | Required HTTPS service root for Projects & Config. |
| `Gateway__Owners__Orchestrator` | Required HTTPS service root for Orchestrator. |
| `Gateway__Owners__Knowledge` | Required HTTPS service root for Knowledge. |
| `Gateway__Owners__Events` | Required HTTPS service root for Events & Sessions. |
| `Gateway__Owners__IdentityBrokerAddress` | Required HTTPS service root for Identity Broker. |
| `Gateway__WebOrigin` | Required exact HTTPS Web origin. It must not contain a path, wildcard, credentials, query, or fragment. |
| `Gateway__OwnerRequestTimeoutSeconds` | Optional Gateway setting. Integer from `1` through `120`; default is `15`. |
| `Gateway__BaseAddress` | Required HTTPS Gateway service root for the MCP host. |

Gateway and MCP reject owner/root addresses with credentials, queries, or
fragments. Owner addresses identify fixed services; callers cannot override
them.

The `mcp-runtime-config.json` map contains `Identity__Issuer`,
`Identity__Audience`, and `Gateway__BaseAddress`. Its Gateway request deadline
is fixed at 10 seconds in the MCP host; it is not a runtime setting.

## Knowledge settings

The `knowledge-runtime-config.json` map contains these settings:

| Setting | Contract |
| --- | --- |
| `ConnectionStrings__Knowledge` | Required password-free connection to the Knowledge PostgreSQL database. Set `Username` to the Entra PostgreSQL role whose name matches the Knowledge managed identity. Require `SSL Mode=VerifyFull`. |
| `Knowledge__Database__WorkloadIdentity__TenantId` | Required Entra tenant GUID for the Knowledge database identity. |
| `Knowledge__Database__WorkloadIdentity__ClientId` | Required client ID for the Knowledge database identity. |
| `Knowledge__Database__WorkloadIdentity__TokenFilePath` | Required absolute path to the projected token file. |
| `Identity__Issuer` | Required HTTPS Broker issuer used to validate service tokens. |
| `Identity__Audience` | Required nonempty service-token audience. |
| `ProjectsConfig__BaseAddress` | Required HTTPS Projects & Config service root. |
| `Knowledge__Events__BaseAddress` | Required HTTPS Events & Sessions service root. |
| `Knowledge__Events__Audience` | Required audience used when Knowledge calls Events. |
| `Knowledge__Provider__ResourceId` | Required stable label for the Memory store. It must never change. Use `agentweaver.knowledge` for the documented deployment. |
| `Knowledge__Provider__DatabaseName` | Required database name; it must match the live PostgreSQL connection. |
| `Knowledge__Provider__ResourceGeneration` | Required integer at least `1`. Increase only after the Memory store is wiped or replaced. |
| `Knowledge__Provider__Schema` | PostgreSQL schema. Default is `knowledge`; must be lowercase, at most 63 characters, and not `public`, `pg_catalog`, `information_schema`, or a name beginning with `pg_`. |
| `Knowledge__Provider__OptionsRevision` | Provider options revision. Default is `native-postgres-memory-v1`. |
| `Knowledge__Provider__OptionsSchemaVersion` | Provider options schema version. Default is `1`; other versions are rejected. |

The runtime options also support `Knowledge:Context:MaximumCandidates`
(default `1000`, range `1`-`10,000`), `Knowledge:Context:MaximumItems`
(default `20`, range `1`-`100`), and `Knowledge:Context:MaximumTokens`
(default `4000`, range `1`-`16,000`). These optional limits are not present in
the checked-in Knowledge ConfigMap.

### Optional Memory provider options

These settings are read only when their configuration section exists. They do
not register a provider or change the default catalog.

| Section and settings | Contract |
| --- | --- |
| `Knowledge:CosmosProvider:Endpoint` | Required root HTTPS endpoint when Cosmos is configured. No credentials, path beyond `/`, query, or fragment. |
| `Knowledge:CosmosProvider:DatabaseId`, `ContainerId` | Required resource names of at most 255 characters; start with an ASCII letter or digit and contain only ASCII letters, digits, `-`, or `_`. |
| `Knowledge:CosmosProvider:ResourceId`, `ResourceGeneration`, `OptionsRevision` | Required stable resource token (maximum 256 characters), positive generation, and revision token (maximum 128 characters). The resource ID must never change; change the generation only after wipe or replacement. |
| `Knowledge:CosmosProvider:OptionsSchemaVersion` | Defaults to and must equal `1`. The existing container must also have partition key `/projectId`, the required composite index, and the non-expiring default TTL. |
| `Knowledge:RedisProvider:Endpoint` | Required `rediss://` URI with an explicit port. No credentials, path beyond `/`, query, or fragment. TLS is always enabled. |
| `Knowledge:RedisProvider:ResourceId`, `ResourceGeneration`, `OptionsRevision` | Required resource token, positive generation, and options revision. Tokens are at most 256 characters; revision is at most 128. |
| `Knowledge:RedisProvider:OptionsSchemaVersion` | Defaults to and must equal `1`. |
| `Knowledge:RedisProvider:Database` | Redis database index from `0` through `15`; default is `0`. |
| `Knowledge:RedisProvider:KeyPrefix` | Defaults to `agentweaver:knowledge`; maximum 128 characters, using ASCII letters, digits, `.`, `_`, `-`, or `:`. |
| `Knowledge:RedisProvider:UserName`, `Password` | Optional ACL credentials. Supply both or neither. Username is at most 256 allowed token characters. Password is 1-4096 characters and cannot contain control characters. Keep the password in a Secret. |

The Redis adapter also checks that the server is a standalone primary with
AOF set to `always`, no eviction, no replicas or cluster, and non-expiring
data. Neither optional provider provisions its database or container.

## Projects & Config settings

The `projects-config-runtime-config.json` map contains:

| Setting | Contract |
| --- | --- |
| `ConnectionStrings__ProjectsConfig` | Required password-free PostgreSQL connection with the Projects runtime Entra role and `SSL Mode=VerifyFull`. |
| `ProjectsConfig__Database__WorkloadIdentity__TenantId` | Required Entra tenant GUID. |
| `ProjectsConfig__Database__WorkloadIdentity__ClientId` | Required user-assigned managed identity client ID federated to the Projects ServiceAccount. |
| `ProjectsConfig__Database__WorkloadIdentity__TokenFilePath` | Required absolute projected-token path. |
| `ProjectsConfig__Authentication__Authority` | Required absolute HTTPS Broker authority. |
| `ProjectsConfig__Authentication__Audience` | Required token audience for Projects & Config. |
| `ProjectsConfig__PrivilegedSetup__ApprovedIssuer` | Exact issuer approved for the privileged project-setup command: `https://agentweaver.6ad4aaa13923a30001c83518.eastus2euap.aksapp.io/`. |
| `ProjectsConfig__PrivilegedSetup__ApprovedTenantId` | Exact approved Entra tenant: `72f988bf-86f1-41af-91ab-2d7cd011db47`. |
| `ProjectsConfig__PrivilegedSetup__ApprovedSubject` | Exact approved caller subject: `518f1ce8-8eb0-4f5f-a719-f742b8ed9ed0`. This is the approved user's Identity Broker user ID, which the Broker issues as the token `sub` claim. It is not the Entra object ID. Do not add another subject or role. |

The following settings are supplied only to the explicit migration or first
TenantAdmin command. They are not part of the runtime ConfigMap:

| Setting | Contract |
| --- | --- |
| `ConnectionStrings__ProjectsConfigMigration` | Separate password-free PostgreSQL connection whose Entra role owns the Projects & Config schema and migration history. Require `SSL Mode=VerifyFull`. |
| `ProjectsConfig__Migration__AccessToken` | Optional short-lived PostgreSQL Entra access token. The explicit command supplies it as the connection password. Store it in a short-lived Secret or environment variable. Do not put it in arguments or logs. |
| `ProjectsConfig__Migration__WorkloadIdentity__TenantId`, `ClientId`, `TokenFilePath` | Alternative explicit workload identity for migration. Configure these settings or `AccessToken`, not both. |

The privileged project-setup command has a separate authority database
contract. The runtime connection and identity are not substitutes. The
runtime principal must not write authority tables or read setup-operation
receipts.

| Setting | Contract |
| --- | --- |
| `ConnectionStrings__ProjectsConfigAuthority` | Required by `--privileged-project-setup`. Use a password-free connection to the same Projects & Config database, with a separately approved authority principal and `SSL Mode=VerifyFull`. |
| `ProjectsConfig__AuthorityDatabase__WorkloadIdentity__TenantId`, `ClientId`, `TokenFilePath` | Required explicit workload identity for that authority principal. Use GUID tenant/client IDs and an absolute projected-token path. The approved identity must have the database grants needed for setup and cleanup. |

These authority settings are not supplied by the standard runtime ConfigMap.
To validate them, supply the optional, all-or-none
`privilegedSetupConfiguration` object to
`deploy\k8s\profiles\projects-config\render.mjs`. The example input includes
empty values that you must replace with approved values, or remove the
whole object for runtime-only validation. The validator returns the
combined `data` map; it does not generate or apply Kubernetes resources.
It rejects the runtime role or client ID, migration-role names, foreign
database targets, and password-bearing connections. The authority client
must be separately approved and federated to the ServiceAccount; validation
does not establish that its federation or database grants exist.

The current P1 runner executes
`--privileged-project-setup` in the attested Projects Config pod, so that
process must receive the approved authority configuration before setup can
work. Adding it requires a separately reviewed configuration, workload
identity, and deployment decision. Do not grant authority writes to the
runtime principal, reuse migration credentials, or change Broker trust as
a workaround. Missing authority configuration fails before the command
opens a database connection; it does not establish the historical state
of an invocation. See [Diagnose an indeterminate P1 project setup](../guide/troubleshooting.md#diagnose-an-indeterminate-p1-project-setup)
for the read-only reconciliation steps.

The provider catalog is supplied separately with the
`ProjectsConfig__ProviderCatalog__` prefix. See
[Configure providers](../guide/configure-providers.md) for the active catalog
and its selection limits. The Knowledge Deployment imports the same immutable
`projects-config-provider-catalog` ConfigMap under that prefix; it is not part
of `knowledge-runtime-config`.

## Platform component report settings

`GET /api/platform/components` works without these settings, but Projects then
reports every area that has a backend owner as `unknown` with reason
`ownerUnavailable`. The current runtime maps don't include these settings.
Adding them is a separate live configuration change. For the request flow, see
[Platform components](../guide/platform-components.md).

Projects & Config reads these optional settings:

| Setting | Contract |
| --- | --- |
| `PlatformComponents__ServiceToken__TokenFile` | Absolute path of a projected service-account token whose audience is the private platform components audience. Projects reads the file on each owner call. |
| `PlatformComponents__Owners__<owner>__BaseAddress` | HTTPS origin, with no path, of an owner service. `<owner>` is `eventsAndSessions`, `environment`, `knowledge`, `orchestrator`, or `identityBroker`. Projects trusts only the service backend CA mounted at `/var/run/service-backend-ca/ca.crt`. |

Each owner service reads these optional settings. Set all three or none. When
none is set, the owner doesn't map `GET /internal/platform/components`.

| Setting | Contract |
| --- | --- |
| `PlatformComponents__ServiceAuthentication__Authority` | HTTPS issuer of the Projects service token. The owner reads its signing keys from OpenID Connect discovery. |
| `PlatformComponents__ServiceAuthentication__Audience` | Private absolute HTTPS audience. It must differ from the owner's public token audience. |
| `PlatformComponents__ServiceAuthentication__CallerSubject` | Exact `sub` claim of the Projects workload, for example `system:serviceaccount:<namespace>:<service-account>`. |

## Orchestrator settings

The `orchestrator-runtime-config.json` map contains:

| Setting | Contract |
| --- | --- |
| `ConnectionStrings__Orchestrator` | Required password-free PostgreSQL connection using the Orchestrator runtime role and `SSL Mode=VerifyFull`. |
| `Orchestrator__Database__WorkloadIdentity__TenantId`, `ClientId`, `TokenFilePath` | Required tenant GUID, federated managed identity client ID, and absolute projected-token path. |
| `Orchestrator__Schema` | Required schema name from the approved database setup. |
| `Identity__Issuer`, `Identity__Audience` | Required Broker HTTPS issuer and token audience. |
| `ProjectsConfig__AuthorizationContext__OwnerBaseAddress`, `Audience` | Required Projects owner HTTPS service root and authorization-context audience. |
| `EventsAndSessions__Authorization__OwnerBaseAddress`, `Audience` | Required Events owner HTTPS service root and authorization audience. |
| `Identity__SecretRedemption__OwnerBaseAddress`, `Audience` | Required Identity Broker HTTPS service root and secret-redemption audience. |
| `AgentGovernance__Policy__ResourceId` | Required stable identifier for the platform AGT Policy binding. |
| `AgentGovernance__Policy__ResourceGeneration` | Required positive resource generation. Increase only after replacing the policy resource. |
| `AgentGovernance__Policy__OptionsRevision` | Required revision matching the provider catalog registration. |
| `AgentGovernance__Policy__PlatformPolicyDocuments__<index>` | Required platform policy document(s). The platform policy defaults to deny, uses only supported actions, and must validate before the service starts. |

The policy document is YAML for the `agt.dotnet-yaml` adapter. The provider is
platform-singleton; project configuration cannot replace it. A policy
evaluation is not itself an action grant.

## Events & Sessions settings

The `events-and-sessions-runtime-config.json` map contains:

| Setting | Contract |
| --- | --- |
| `ConnectionStrings__EventsAndSessions` | Required password-free PostgreSQL connection using the Events runtime role and `SSL Mode=VerifyFull`. |
| `EventsAndSessions__Database__WorkloadIdentity__TenantId` | Required Entra tenant GUID. |
| `EventsAndSessions__Database__WorkloadIdentity__ClientId` | Required federated managed identity client ID. |
| `EventsAndSessions__Database__WorkloadIdentity__TokenFilePath` | Required absolute projected-token path. |
| `Identity__Issuer`, `Identity__Audience` | Required Broker HTTPS issuer and service-token audience. |
| `EventsAndSessions__Provider__DatabaseName` | Required Sessions database name. Must agree with the live connection. |
| `EventsAndSessions__Provider__Schema` | Sessions schema name. Defaults to `events_sessions`; it must be a valid lowercase PostgreSQL schema name. |
| `EventsAndSessions__Provider__OptionsSchemaVersion` | Sessions options contract version. Defaults to `1`; other versions are rejected. |
| `EventsAndSessions__Provider__OptionsRevision` | Sessions provider revision matching the catalog registration. Defaults to `native-postgres-v1`. |
| `EventsAndSessions__Provider__ResourceId` | Required stable Sessions resource label. |
| `EventsAndSessions__Provider__ResourceGeneration` | Required positive generation. Change it only after wiping or replacing the store. |
| `EventsAndSessions__Provider__PollIntervalMilliseconds` | Event subscription polling interval in milliseconds. Default is `250`; allowed range is `50`-`30,000`. |
| `EventsAndSessions__Provider__ReferenceRetentionDays` | Object-reference retention in days. Default is `365`; allowed range is `1`-`3650`. |
| `EventsAndSessions__Messaging__OptionsSchemaVersion` | Messaging contract version. Defaults to `1`; other versions are rejected. |
| `EventsAndSessions__Messaging__OptionsRevision` | Messaging revision matching the platform-singleton catalog registration. Defaults to `native-postgres-messaging-v1`. |
| `EventsAndSessions__Messaging__ClaimLeaseSeconds` | Addressed-message claim lease in seconds. Default is `120`; allowed range is `10`-`3600`. |
| `EventsAndSessions__Messaging__MaximumMessageLifetimeDays` | Maximum message lifetime in days. Default is `1`; allowed range is `1`-`7`. |
| `EventsAndSessions__Cost__Copilot__OptionsSchemaVersion` | Required version `2` for the published Copilot rate-card contract. |
| `EventsAndSessions__Cost__Copilot__OptionsRevision` | Required Copilot Cost options revision matching the catalog. |
| `EventsAndSessions__Cost__Copilot__ResourceId` | Required stable Cost resource label. |
| `EventsAndSessions__Cost__Copilot__ResourceGeneration` | Required positive resource generation. |
| `EventsAndSessions__Cost__Copilot__RateCard__Json` | Required versioned rate-card JSON. It must match the provider-published Copilot source and use USD per-million-token rates. Do not enter estimates or legacy model multipliers. |
| `AgentHost__ModelBindingsRevision` | Required revision for the exact model-binding map. It must be 1-256 ASCII letters, digits, `.`, `_`, `-`, or `:`. Change it when the map changes. |
| `AgentHost__ModelBindings__<reference>__ModelId` | Exact model ID returned by the supported Copilot SDK model catalog. The reference key and model ID must each be 1-256 ASCII letters, digits, `.`, `_`, `-`, or `:`. Current reference keys include `reasoning`, `coding`, `gpt-6.1-sol`, and `gpt-6-luna`. |
| `AgentHost__ModelBindings__<reference>__SourceMode` | Source mode enum: `HostedCopilot` or `Byok`. The current checked-in hosted profile uses `HostedCopilot`; BYOK also requires its provider and credential configuration. |
| `AgentHost__ModelBindings__<reference>__Enabled` | Optional Boolean. Defaults to `true`; a disabled binding is not an available model choice. |
| `AgentHost__ModelBindings__<reference>__Provider` | Optional for `HostedCopilot` and must be omitted. Required for `Byok`; its `Type` is `azure`, `openai`, or `anthropic`, its `BaseUrl` is HTTPS without credentials, query, or fragment, and `WireApi` is `responses` (default) or `completions`. `AzureApiVersion` is optional only for Azure. Optional header names contain ASCII letters, digits, and hyphens; header values cannot contain control characters. |
| `AgentHost__ModelBindings__<reference>__PromptCapacityTokens` | Optional positive integer; no upper bound is imposed by the binding validator. |
| `AgentHost__ModelBindings__<reference>__SupportedReasoningEfforts` | Optional list, empty by default. Values are unique entries from `low`, `medium`, `high`, and `xhigh`. |
| `EventsAndSessions__SessionMaterial__ContainerUri` | Optional HTTPS URI of the approved private `session-material` container. Required when remote tool result TLS is configured. The Events & Sessions identity needs Storage Blob Data Contributor on this container only. |
| `EventsAndSessions__Knowledge__BaseAddress` | HTTPS Knowledge service root. |
| `ProjectsConfig__BaseAddress` | HTTPS Projects & Config service root. |
| `ProjectsConfig__AuthorizationContext__OwnerBaseAddress`, `Audience` | Projects authorization-context owner root and audience. |
| `EventsAndSessions__OrchestratorOwner__OwnerBaseAddress`, `Audience` | Orchestrator owner root and audience used by Events. |

Native Sessions and Messaging providers validate their option schema,
revision, resource identity, and numeric bounds at startup. Cost rates are
snapshotted into immutable bindings; changing a rate card does not rewrite
existing run bindings. Missing Copilot rates remain `Unpriced`.

## Environment settings

The `environment-runtime-config.json` map contains:

| Setting | Contract |
| --- | --- |
| `ConnectionStrings__Environment` | Required password-free PostgreSQL connection using the Environment runtime role and `SSL Mode=VerifyFull`. |
| `Environment__Database__WorkloadIdentity__TenantId`, `ClientId`, `TokenFilePath` | Required tenant GUID, federated identity client ID, and absolute projected-token path. |
| `Environment__Authentication__Authority`, `Audience` | Required HTTPS Broker authority and token audience. |
| `ProjectsConfig__BaseAddress` | Required HTTPS Projects & Config service root. |
| `Environment__RuntimeBootstrap__OrchestratorOwnerAddress`, `BrokerOwnerAddress`, `EventsOwnerAddress` | Required HTTPS service roots for the owners used by runtime bootstrap. |
| `Environment__Sandbox__AgentSandbox__OptionsSchemaVersion` | Must match Agent Sandbox options schema version `2`. |
| `Environment__Sandbox__AgentSandbox__Namespace` | Valid Kubernetes namespace and the namespace used by the selected provider. The documented example is `agentweaver`. |
| `Environment__Sandbox__AgentSandbox__WorkspaceStorageProviderId` | Must match the registered Workspace Storage provider, currently `azure-files-csi`. |
| `Environment__Sandbox__AgentSandbox__RuntimeClassName`, `ExpectedRuntimeHandler` | Must identify the installed Sandbox RuntimeClass and its expected handler. Obtain both from the cluster's approved RuntimeClass configuration. |
| `Environment__Sandbox__AgentSandbox__CpuRequest` | Positive whole or decimal CPU quantity whose integer part starts with `1`-`9`, or positive integer millicores ending in `m`; for example, `1`, `2.5`, or `250m`. It must fit the namespace quota and approved workload profile. |
| `Environment__Sandbox__AgentSandbox__MemoryRequest` | Positive integer Kubernetes memory quantity using `Ki`, `Mi`, `Gi`, or `Ti`, such as `512Mi`. It must fit the namespace quota and approved workload profile. |
| `Environment__Sandbox__AgentSandbox__ReconciliationTimeoutSeconds` | Integer from `1` through `900`. |
| `Environment__Sandbox__AgentSandbox__PollIntervalMilliseconds` | Integer from `1` through `5000`. |
| `Environment__Sandbox__AgentSandbox__StartupBudgets__ScheduledSeconds`, `ImageReadySeconds`, `StartedSeconds`, `ConfiguredSeconds`, `ReadySeconds`, `TotalSeconds` | Each value is required with no valid default and must be positive. The total is a separate overall deadline; the validator does not require it to equal the sum of phase values. |
| `Environment__Storage__AzureFiles__OptionsSchemaVersion` | Must match Azure Files options schema version `1`. |
| `Environment__Storage__AzureFiles__Namespace` | Valid Kubernetes DNS label matching the target namespace. |
| `Environment__Storage__AzureFiles__StorageClassName` | Valid Kubernetes DNS label for the installed Azure Files StorageClass. |
| `Environment__Storage__AzureFiles__MaximumCapacityGiB` | Integer of at least `1`; caps the requested workspace volume. |
| `Environment__Storage__AzureFiles__ProvisioningTimeoutSeconds` | Integer from `1` through `900`. |
| `Environment__Storage__AzureFiles__PollIntervalMilliseconds` | Integer from `1` through `5000`. |

The Environment Deployment supplies additional required provider options that
are not stored in the two runtime ConfigMaps:

| Configuration path | Contract and value source |
| --- | --- |
| `Environment:Sandbox:AgentSandbox:OptionsRevision` | Required 1-128 character token using ASCII letters, digits, `.`, `_`, `-`, or `:`. It must match the catalog registration. |
| `Environment:Sandbox:AgentSandbox:ContainerImage` | Required OCI image pinned to a SHA-256 digest. Use the exact AgentHost image from the approved publication receipt. |
| `Environment:Sandbox:AgentSandbox:ContainerImagePlatform` | Required value `linux/amd64`, matching the published image metadata. |
| `Environment:Sandbox:AgentSandbox:ContainerImageCompressedPullBytes` | Required positive compressed image size. The reader supplies `0` when omitted, which fails validation; obtain the byte count from the approved image metadata. |
| `Environment:Sandbox:AgentSandbox:AgentHost:ConfigurationMapName`, `TlsSecretName` | Optional AgentHost profile. When present, both values are required Kubernetes DNS subdomains; use the reviewed ConfigMap and TLS Secret names. |
| `Environment:NetworkPolicy:Cilium:AdapterVersion` | Required parseable version with no default. Match the adapter version in the provider registration. |
| `Environment:NetworkPolicy:Cilium:OptionsRevision` | Required nonempty, non-whitespace revision with no default. Match the catalog registration. |
| `Environment:Storage:AzureFiles:OptionsRevision` | Required 1-128 character token using ASCII letters, digits, `.`, `_`, `-`, or `:`. Match the catalog registration. |

The `environment-cilium-config.json` ConfigMap has one data key,
`appsettings.Production.json`. That value is nested JSON for
`Environment:NetworkPolicy:Cilium`; do not flatten it into environment
variables. `Namespace` is a Kubernetes DNS label and must match the product
namespace (`agentweaver` in this guide). `OptionsSchemaVersion` must equal `1`.
The Environment Deployment supplies `AdapterVersion` as a parseable version
and `OptionsRevision` as a nonempty, non-whitespace value; neither has a
default.

The nested `EndpointSelectors` map must match each service namespace and
workload label in the checked-in Cilium policies. Each selector key is
`<namespace>/<service>`, and each selector must include the exact
`k8s:io.kubernetes.pod.namespace` label for that namespace plus at least one
other nonempty, non-wildcard label. Labels such as
`agentweaver.io/workload` are JSON properties because `/` and `:` are not
valid environment-variable key separators. The JSON configuration provider
uses colons as path separators, so Environment reconstructs colon-prefixed
Cilium labels from their configuration path before validation. Keep the
`k8s:` prefix and namespace value unchanged. Do not replace exact selectors
with a broad namespace or wildcard selector. The checked-in service set
covers `agenthost`, `environment`, `events-and-sessions`, `gateway`,
`identity-broker`, `knowledge`, `mcp`, `orchestrator`, and `projects-config`.

This correction does not change the immutable `environment-cilium-config`
data. For an existing installation, the Knowledge fix changes only its
Deployment and uses the existing Knowledge image. Reapply the reviewed bundle
through the [guarded deployment procedure](../guide/deploy-to-azure.md) to roll
out the catalog reference. The Environment fix changes service code,
so wait for a new Environment image before reapplying the bundle to that
Deployment. The v1.0.1 Environment image does not contain the corrected
configuration reader. Keep the Cilium ConfigMap unchanged; do not delete or
recreate it.

Agent Sandbox also validates the image and measured image metadata supplied
by the provider profile: the OCI image must be pinned by SHA-256 digest, use
`linux/amd64`, and have a positive compressed pull size. CPU and memory values
must be valid Kubernetes quantities. The provider does not create the
RuntimeClass, Azure Files CSI driver, or the underlying Azure resources.

## Web settings

The `web-runtime-config.json` map contains:

| Setting | Contract |
| --- | --- |
| `VITE_GATEWAY_URL` | Optional Gateway base path or URL. The client defaults to `/api/v1`; the checked-in profile explicitly sets that same-origin path, which the managed portal routes to Gateway. Use a different value only when it matches the reviewed route configuration. |
| `VITE_IDENTITY_BROKER_URL` | Required HTTPS Broker URL used for OAuth. |
| `VITE_IDENTITY_BROKER_ISSUER` | Optional when the Broker URL is a valid HTTPS issuer. The client derives the issuer from that URL; when set, it must exactly match the issuer configured in Gateway and Broker. The checked-in profile sets it explicitly. |
| `VITE_OAUTH_CLIENT_ID` | Required ID of the explicitly registered public Web client. Do not reuse an acceptance-only client in production. |
| `VITE_OAUTH_SCOPES` | Required space-separated scopes registered for that client. |

These values are public application configuration, not secrets. The OAuth
client's exact redirect URI must match the deployed Web origin.

## Provider option classes not fully represented in runtime maps

This section records the source-level contracts that apply when a provider
section is configured, even when the default ConfigMaps do not contain that
section.

| Options class | Contract |
| --- | --- |
| `NativePostgresMemoryOptions` | Requires `ResourceId`, `DatabaseName`, and `ResourceGeneration >= 1`. Resource and database tokens are 1-256 ASCII letters, digits, `.`, `_`, `-`, or `:`; the Memory `ResourceId` must never change. Schema defaults to `knowledge`; it must start with a lowercase letter, contain only lowercase letters, digits, or `_`, and be at most 63 characters. `public`, `pg_catalog`, `information_schema`, and names beginning with `pg_` are rejected. Revision defaults to `native-postgres-memory-v1` and uses the same token rule. Options schema version defaults to and must equal `1`. |
| `KnowledgeRuntimeOptions` | Requires HTTPS Identity, Projects, and Events addresses, nonempty audiences, and valid Memory options. Context candidates default to `1000` (range `1`-`10,000`), items to `20` (range `1`-`100`), and tokens to `4000` (range `1`-`16,000`). |
| `CosmosMemoryOptions` | Optional section `Knowledge:CosmosProvider`. Endpoint must be a root HTTPS URI. Database and container names must start with an ASCII letter or digit, contain only ASCII letters, digits, `-`, or `_`, and be at most 255 characters. Resource ID is a 1-256 character token and must never change; generation must be positive; revision is a 1-128 character token. Options schema version defaults to and must equal `1`. Adapter negotiation checks the existing `/projectId` partition key, composite index, and non-expiring default TTL. |
| `RedisMemoryOptions` | Optional section `Knowledge:RedisProvider`. Endpoint must be a root `rediss` URI with an explicit port. Resource ID is a 1-256 character token and must never change; generation must be positive; revision is a 1-128 character token; options schema version defaults to and must equal `1`. Database is `0`-`15` (default `0`); key prefix defaults to `agentweaver:knowledge` and allows at most 128 ASCII letters, digits, `.`, `_`, `-`, or `:`. ACL username/password are both present or both absent; usernames are at most 256 token characters, and passwords are 1-4096 characters with no control characters. The password is never formatted into diagnostics. |
| `PostgresSessionsProviderOptions` | Requires resource and database tokens of 1-256 ASCII letters, digits, `.`, `_`, `-`, or `:` and `ResourceGeneration >= 1`. Schema defaults to `events_sessions`; it must start with a lowercase letter, contain only lowercase letters, digits, or `_`, and be at most 63 characters. It rejects `public`, `pg_catalog`, `information_schema`, and names beginning with `pg_`. Revision defaults to `native-postgres-v1` and must be a 1-128 character token. Options schema version defaults to and must equal `1`. Polling defaults to 250 ms (range `50`-`30,000`); reference retention defaults to 365 days (range `1`-`3,650`). |
| `NativePostgresMessagingProviderOptions` | Revision defaults to `native-postgres-messaging-v1`; it must be nonempty, at most 128 characters, and contain no whitespace or control characters. Options schema version defaults to and must equal `1`. Claim lease defaults to 120 seconds (range `10`-`3,600`); maximum message lifetime defaults to 1 day (range `1`-`7`). |
| `CiliumEgressProviderOptions` | Requires a DNS-label namespace, a parseable adapter version, nonempty options revision, and options schema version `1`. Each configured endpoint selector uses an exact `namespace/name`, has at least one non-wildcard label, and sets `k8s:io.kubernetes.pod.namespace` to the namespace in that key. |
| `GatewayOptions` | Requires HTTPS issuer, audience, five fixed HTTPS owner roots, and exact HTTPS `Gateway:WebOrigin`. `Gateway:OwnerRequestTimeoutSeconds` defaults to `15` and must be `1`-`120`. |
| `McpServiceOptions` | Requires an absolute HTTPS Identity issuer and audience; paths are allowed. Gateway address must be an HTTPS service root without credentials, query, or fragment. Gateway request timeout is fixed at 10 seconds. |
| `RuntimeModelBinding` | `ModelId` is required and must be 1-256 ASCII letters, digits, `.`, `_`, `-`, or `:`. `SourceMode` is `HostedCopilot` or `Byok`; `Enabled` defaults to `true`. Hosted Copilot must omit `Provider`; BYOK requires provider type `azure`, `openai`, or `anthropic` and an absolute HTTPS `BaseUrl` without credentials, query, or fragment. `WireApi` defaults to `responses` and allows `responses` or `completions`. `AzureApiVersion` is valid only for Azure and cannot be blank or contain control characters. `Headers` defaults to an empty map; each header name contains only ASCII letters, digits, and `-`, and values cannot contain control characters. `PromptCapacityTokens`, when set, must be positive. `SupportedReasoningEfforts` defaults to an empty list; entries must be unique values from `low`, `medium`, `high`, or `xhigh`. |
| `AgentSandboxOptions` | All positional options are required; schema version must equal `2`. Revision is a 1-128 character token using ASCII letters, digits, `.`, `_`, `-`, or `:`. Namespace, RuntimeClass, and expected handler are Kubernetes DNS subdomains. Workspace Storage provider ID is 1-128 ASCII letters, digits, `.`, `_`, or `-`. Image reference is at most 512 characters and must end in `@sha256:` plus 64 lowercase hexadecimal digits; platform must be `linux/amd64`; compressed pull size must be positive. CPU accepts a positive integer with a nonzero first digit, a positive decimal with such an integer part, or positive integer millicores ending in `m`. Memory accepts positive integer `Ki`, `Mi`, `Gi`, or `Ti` quantities. Reconciliation timeout is `1`-`900` seconds; poll interval is `1`-`5000` ms; every startup phase budget and the total budget must be positive. Optional nested AgentHost and BuildTest profiles are validated when present. |
| `AgentSandboxAgentHostProfile` | Optional profile. Its ConfigMap and TLS Secret names must be valid Kubernetes DNS subdomains. |
| `SandboxBuildTestAcceptedExecutionOptions` | Optional accepted BuildTest profile. It requires a profile ID of at most 128 valid identifier characters; pinned `linux/amd64` command and collector images; 1-32 distinct absolute executable paths; CPU quantities in the Agent Sandbox format; positive integer `Ki`, `Mi`, `Gi`, or `Ti` memory and ephemeral-storage limits; timeout `1`-`900` seconds; log output from `1`-`1,048,576` bytes; file output from `0`-`67,108,864` bytes; and the exact `offline` egress profile. The collector uses `dotnet` with `Agentweaver.AgentHost.dll`, a supported mode (`--build-test-output-collector-v1` or `--build-test-workspace-collector-v2`), and container name `buildtest-collector`. See [Environment Sandbox testing](../guide/environment-sandbox-testing.md) for the use case. |
| `AzureFilesCsiOptions` | All options are required; schema version must equal `1`. Revision is a 1-128 character token using ASCII letters, digits, `.`, `_`, `-`, or `:`. Namespace and StorageClass are Kubernetes DNS labels. Capacity is at least `1` GiB with no provider-level upper bound; provisioning timeout is `1`-`900` seconds; poll interval is `1`-`5000` ms. |
| `CopilotCostProviderOptions` | Requires 1-256 character resource token, positive generation, 1-128 character options revision, schema version `2`, and an operator-supplied JSON card of at most 262,144 UTF-8 bytes with 1-256 models. The card must use schema version `1`, the exact published Copilot source URL and meter/unit identifiers, USD, per-million-token rates, positive credits-per-currency-unit, and a positive nano-AIU scale equal to `1,000,000,000`. Each model has required default-context rates and may include long-context rates; legacy model multipliers are rejected. The parser computes the SHA-256 hash from the exact JSON text. |
| `AzureCostProviderOptions` | Optional candidate; when configured, all fields and its rate card are required. Schema version is `1`; `PricingModel` is `StandardTokens` or `ProvisionedThroughput`. Resource ID and options revision use the provider token syntax (maximum 256 and 128 characters). Rate-card ID must start with `azure-retail-prices.standard.` or `azure-retail-prices.ptu.`, version is a 1-64 character token, meter source must match the Azure Cost provider, and currency is three uppercase ASCII letters. `NanoUnitsPerUnit` must be positive and equals `1,000,000` for token pricing. Standard token pricing requires positive model-scoped `ModelMultipliers` entries for both input and output rates for every model; each key combines the exact model ID and category with one vertical-bar separator. Provisioned throughput cannot contain token rates and remains unpriced without trusted usage-share evidence. |

## Obtain common Azure values

Run Azure CLI queries only after selecting the intended subscription and
resource group. Replace each placeholder with the reviewed target name.

| Value | Command |
| --- | --- |
| Current tenant ID | `az account show --query tenantId --output tsv` |
| PostgreSQL server FQDN | `az postgres flexible-server show --resource-group <resource-group> --name <server> --query fullyQualifiedDomainName --output tsv` |
| Managed identity client ID | `az identity show --resource-group <resource-group> --name agentweaver-id-<service> --query clientId --output tsv` |
| Key Vault URI | `az keyvault show --resource-group <resource-group> --name <approved-key-vault-name> --query properties.vaultUri --output tsv` |
| AKS OIDC issuer | `az aks show --resource-group <resource-group> --name agentweaver-aks --query oidcIssuerProfile.issuerUrl --output tsv` |
| AKS-managed public domain | `az aks show --resource-group <resource-group> --name agentweaver-aks --query ingressProfile.webAppRouting.defaultDomain.domainName --output tsv` |

The identity's client ID is not its principal object ID. Use the field required
by the specific Azure or PostgreSQL operation. Never put either identifier in
a secret field.

## Find values that Azure CLI does not return

Use the approved deployment inputs and owner configuration for values that
describe Agentweaver services or provider contracts. Do not guess an address,
audience, identifier, or revision from a resource name.

| Setting or value type | How to obtain it |
| --- | --- |
| PostgreSQL connection string | Combine the server FQDN from the Azure CLI query above with the approved database name and runtime Entra role. The role name must match the service's managed identity name. Use port `5432` and `SSL Mode=VerifyFull`; omit the password. |
| `*TenantId` and `*ClientId` | Use the selected tenant and the user-assigned identity's client ID from the CLI queries above. For PostgreSQL, confirm the federated credential is scoped to the exact namespace and ServiceAccount in the applied workload manifest. |
| `*TokenFilePath` | Copy the absolute path from the workload-identity projected volume and mount in the service's Deployment and ServiceAccount configuration. The path is not an Azure resource property. |
| Issuers and service audiences | Use the Identity Broker issuer and the exact audience registered for each service client in the approved identity configuration. Keep issuer and audience values consistent across the calling service and its owner. |
| `*BaseAddress`, `*OwnerAddress`, `*WebOrigin`, and redirect URIs | Use the reviewed DNS and TLS routing plan for the target. Service roots are internal owner addresses; Web origin and OAuth redirect URIs are exact public HTTPS values. The Kustomize base does not create public routing. |
| Provider `ResourceId`, `ResourceGeneration`, `OptionsRevision`, and `OptionsSchemaVersion` | Use the provider catalog and the owning resource's lifecycle record. Keep IDs stable, use generation `1` for a new resource, and change generation only after the resource is wiped or replaced. Use the options schema version required by the selected adapter; do not invent a newer version. |
| RuntimeClass, handler, StorageClass, and namespace | Read the installed cluster objects and the approved workload profile. For example, inspect `kubectl get runtimeclass -o yaml` and `kubectl get storageclass`; confirm names against the deployment manifests and namespace quota. |
| Model IDs | Select the exact ID returned by the supported Copilot SDK model catalog. Do not substitute a display name or alias. |
| Copilot `RateCard:Json` | Use the versioned published Copilot rate-card snapshot accepted by the provider. Preserve its exact JSON fields; the runtime hashes the supplied text. |
| Platform policy document | Use the reviewed source-controlled AGT YAML policy. Validate it against the platform adapter before deployment; the runtime's default policy is deny. |
| Secrets and private keys | Create the operator-managed Kubernetes Secret from the approved upstream provider or certificate workflow. The ConfigMaps contain only secret references or file paths, never the secret values. |

The checked-in ConfigMaps are examples of the required key shape, not a source
of target-specific IDs or production credentials. Record the approved values
with the deployment inputs before applying the bundle.
