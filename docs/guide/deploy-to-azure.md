# Tutorial: deploy Agentweaver to Azure

In this tutorial, you prepare the Azure foundation, bootstrap PostgreSQL and
workload identities, install the reviewed Kubernetes resources, and verify
the workloads. The commands can change Azure and cluster state. Run them only
for a separately approved target.

Agentweaver 1.x source includes Bicep templates and a Kustomize installation.
The repository does not contain a deployable production parameter file, TLS
certificates, credentials, or an approval for a live target. Replace every
placeholder with a reviewed value before running a write command.

## Before you begin

You need:

- An Azure subscription and tenant selected for the deployment.
- Authorization to provision the exact resource group, AKS cluster, private
  endpoints, and managed identities in the reviewed Bicep parameters.
- Azure CLI, the Bicep extension, `kubectl`, and `kubelogin` ([AKS
  authentication guidance](https://learn.microsoft.com/azure/aks/kubelogin-authentication)),
  plus Node.js 24, npm, and the .NET 10 SDK.
- A clean checkout at the exact v1 source commit approved for deployment.
- A reviewed, Git-tracked Bicep parameter file under
  `infra/bicep/parameters/`. The checked-in examples contain placeholders and
  cannot run.
- The required operator-owned Secrets, TLS certificates, and Gateway values.

The example resource names in this tutorial are:

| Resource | Example name |
| --- | --- |
| Resource group | `agentweaver` |
| AKS cluster | `agentweaver-aks` |
| PostgreSQL server | `agentweaver-pg` |
| Key Vault | Value of `keyVaultName` in the reviewed Bicep parameter file |
| Blob Storage account | `agentweaverblob` |
| Virtual network | `agentweaver-vnet` |
| Log Analytics workspace | `agentweaver-law` |
| Application Insights | `agentweaver-appi` |
| Azure Monitor Private Link Scope | `agentweaver-ampls` |
| Workload identity | `agentweaver-id-<service>` |
| Product namespace | `agentweaver` |
| Gateway namespace | `agentweaver-gateway` |
| Knowledge Memory resource ID | `agentweaver.knowledge` |

These are examples, not target-discovery logic. Replace names only through
reviewed deployment inputs. Do not guess dynamic managed identity client IDs.
Use `<managed-domain>` as the placeholder for the AKS-managed public domain;
the deployment process reads the assigned domain instead of constructing it.

## 1. Select and verify the Azure target

Run Azure commands only after confirming the subscription and tenant:

```powershell
az login
az account set --subscription <subscription-id>
az account show --query "{name:name,subscriptionId:id,tenantId:tenantId}" --output table
```

The displayed subscription and tenant must match the approved deployment
record. Do not rely on whichever account Azure CLI selected previously.

## 2. Validate the source and infrastructure

From the repository root, run the offline checks:

```powershell
npm run test:azure
az bicep build --file infra\bicep\main.bicep --stdout
kubectl kustomize deploy\k8s
```

These commands validate source and render manifests. They do not contact
Azure, install resources, or prove that the cluster is ready.

## 3. Provision the Azure foundation

Use the installer for an approved full-foundation deployment. The installer
accepts only tracked, reviewed parameter files under
`infra/bicep/parameters/`. Do not put passwords, client secrets, tokens, or
private keys in a parameter file.

```powershell
node scripts\azure\deploy.mjs `
  --resource-group agentweaver `
  --parameters infra\bicep\parameters\<approved-full-foundation-parameters>.json `
  --subscription <subscription-id> `
  --allowed-subscription <subscription-id> `
  --tenant <tenant-id> `
  --allowed-tenant <tenant-id>
```

Without `--execute`, `deploy.mjs` prints an offline summary and makes no Azure
call. After the target and effects are approved, add `--execute` to the same
command. It provisions the Azure foundation. The preflight checks the allowed
subscription and tenant, enabled account, dedicated resource group, configured
Key Vault name, exact source, and Bicep inputs. It does not enumerate existing
resources or compare their properties after deployment. Use the full-foundation
template only for an approved initial deployment into an empty managed-resource
target. For an existing P0 cluster, the AKS-only template refreshes the three
workload-identity federated credentials without PUTting AKS; its what-if must
not contain a managed-cluster change. Review the what-if output before execution.
The top-level what-if does not show resources inside the nested workload-identity
federation deployment. The preflight lists the three federated identity
credentials that the nested deployment will create or update. It does not query
existing credentials to distinguish creates from updates.

The infrastructure installer does not create Kubernetes namespaces or grant
human AKS permissions. It never creates, requires, or verifies a human
`Azure Kubernetes Service RBAC Cluster Admin` assignment. Cluster connectivity
and Kubernetes API permissions are provisioned outside this tooling. The
installer also does not apply product manifests, create the Broker's application
state, run database migrations, install Gateway routes, or create operator-owned
Secrets.

The reviewed parameter file supplies the Key Vault name through `keyVaultName`.
Key Vault is the Identity Broker's secrets backend; its name and
`IdentityBroker__SecretRedemption__VaultUri` are deployment configuration.
The deployment uses that configured name and returns the vault URI for runtime
configuration.

## 4. Connect to AKS and prepare the namespace

The full-foundation template enables Kubernetes RBAC and Microsoft Entra
authentication with Azure RBAC, and disables local accounts. The AKS-only
template reads the existing cluster and does not change those settings or its
AKS-managed Application Routing domain. Before you run Kubernetes commands, an administrator
must provision authorization for your identity to retrieve user credentials,
such as the `Azure Kubernetes Service Cluster User Role`. This Azure role does
not grant Kubernetes API permissions. Your identity also needs API connectivity
and the Kubernetes permissions required by each operation, including namespace
creation and the temporary resources used by the PostgreSQL bootstrap. Provision
that access outside this tooling. The tooling checks required permissions
before it applies resources and stops with an actionable error when access is
missing. It never grants a person AKS Cluster Admin or another replacement
role. See
[Microsoft Entra authorization for AKS](https://learn.microsoft.com/azure/aks/entra-id-authorization).

Run these commands from an operator machine that can reach the AKS API server.
The selected Azure subscription must still be the approved subscription from
step 1.

1. Retrieve the user kubeconfig for the approved cluster:

   ```powershell
   az aks get-credentials `
     --resource-group agentweaver `
     --name agentweaver-aks `
     --subscription <subscription-id>
   ```

2. Configure the kubeconfig to use the signed-in Azure CLI identity:

   ```powershell
   kubelogin convert-kubeconfig -l azurecli
   ```

3. Verify the active context and API server before writing to Kubernetes:

   ```powershell
   kubectl config current-context
   kubectl config view --minify --output 'jsonpath={.clusters[0].cluster.server}'
   az aks show `
     --resource-group agentweaver `
     --name agentweaver-aks `
     --subscription <subscription-id> `
     --query "{name:name, fqdn:fqdn}" `
     --output yaml
   ```

   Confirm the active context is the one created by `az aks get-credentials`.
   Compare the API-server URL host with the `fqdn` value returned by Azure
   CLI. Stop if the host does not identify `agentweaver-aks`. The AKS
   API-server host is separate from `<managed-domain>`, which is used for
   application routes.

4. Create only the product namespace:

   ```powershell
   kubectl apply --dry-run=client --filename deploy\k8s\base\namespace.yaml
   kubectl apply --filename deploy\k8s\base\namespace.yaml
   kubectl get namespace agentweaver --show-labels
   ```

   Confirm that the namespace exists and has the restricted Pod Security
   labels from the manifest. This step does not apply product workloads.

5. Bootstrap the Identity Broker PostgreSQL roles with the reviewed AKS-only
   template and parameter file:

   ```powershell
   node scripts\azure\deploy.mjs `
     --resource-group agentweaver `
     --template infra\bicep\aks-redeploy.bicep `
     --parameters infra\bicep\parameters\<approved-aks-only-parameters>.json `
     --subscription <subscription-id> `
     --allowed-subscription <subscription-id> `
     --tenant <tenant-id> `
     --allowed-tenant <tenant-id> `
     --bootstrap-identity-postgres
   ```

   The command prints an offline summary unless you add `--execute`. Review
   the target and what-if, then add `--execute` only for the separately
   approved AKS-only deployment and PostgreSQL bootstrap. The bootstrap uses a
   temporary user-authenticated kubeconfig, a run-owned proxy Pod and
   ConfigMap, and a loopback-only port-forward. It removes these temporary
   resources and local files after it finishes.

## 5. Complete the remaining workload identity setup

The guarded PostgreSQL bootstrap in step 4 creates only the Identity Broker
runtime and migration roles. It does not create the Projects & Config,
Environment, Knowledge, Events & Sessions, or Orchestrator runtime identities
and their ServiceAccount federations. Those five identities use the separate
P1 runtime-identity template. The current guarded installer does not deploy
that template, and the repository does not provide a guarded command for it.
Do not apply it with a direct `az deployment` command. Before you install the
product bundle, require a separately approved process to provision these
identities and federations. If that process is not available, stop here. See
[Limited P1 runtime identities](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#limited-p1-runtime-identities).
Do not apply that template with an unguarded command.

Before you install the product bundle, confirm that the five identities are
provisioned and federated to the exact `agentweaver` namespace and service
ServiceAccounts. Read each client ID from Azure CLI as described in the
[runtime configuration reference](../reference/runtime-configuration.md).
The Events & Sessions identity needs Storage Blob Data Contributor on the
private `session-material` container only. If an existing target is missing
that container or grant, use the separately approved
[focused repair procedure](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#session-material-container-repair);
do not redeploy the full foundation to repair these resources.
Orchestrator migration 023 adds interactive run requests and cancellation
receipts. The reviewed P1 plan includes that migration and grants
`SELECT, INSERT, UPDATE` on `project_run_requests` and `SELECT, INSERT` on
`project_run_cancellations`. Migration 023 changes the execution-state CHECK
constraint on `accepted_runs`, which scans the whole table. The ALTER takes an
`ACCESS EXCLUSIVE` lock, so reads and writes to `accepted_runs` block until
the transaction commits. It doesn't alter `sessions` or build indexes on existing
tables. Review table size and plan a write pause.
Orchestrator migration 024 adds private Chief of Staff conversations and
Priorities. It creates four new, empty tables and doesn't alter or scan
existing tables. The reviewed plan grants `SELECT, INSERT` on
`chief_of_staff_conversations`, `SELECT, INSERT, UPDATE` on
`chief_of_staff_priority_lists` and `chief_of_staff_priorities`, and
`SELECT, INSERT, DELETE` on `chief_of_staff_priority_links`.

Then review and run the source-bound PostgreSQL initializer:

```powershell
node scripts\azure\bootstrap-p1-postgres.mjs `
  --core-merge-sha <reviewed-core-merge-sha>
node scripts\azure\bootstrap-p1-postgres.mjs `
  --core-merge-sha <reviewed-core-merge-sha> `
  --execute
```

The first command prints the fixed target and planned roles without contacting
Azure. The execution command checks the selected account and dedicated resource
group, validates the approved PostgreSQL Entra administrator inputs, and reads
the two Identity Broker principal IDs needed for database bootstrap. The five
P1 principal IDs come from the reviewed plan and are not refreshed from Azure.
The command then runs the Identity Broker and P1 database stages through one
temporary loopback tunnel. It requires a clean checkout at the reviewed
`origin/v1` source. The operator must already have the required AKS access and
PostgreSQL Entra administrator authority; the command creates no Azure role
assignments, access grants, or product workloads. Keep the P1 receipt and
verify the database and roles with read-only catalog queries before installing
the product bundle. The Broker runtime identity does not own the schema. If
the identity or database setup is not approved or complete, stop before
applying product workloads.

For each database workload, verify that:

- Its user-assigned identity is federated to the exact Kubernetes
  ServiceAccount in namespace `agentweaver`.
- The password-free PostgreSQL connection uses the managed identity name as
  the Entra database role.
- The runtime identity has only the required schema grants.
- Key Vault access is limited to the Identity Broker runtime identity. The
  migration identity has no Azure resource role.

See the [runtime configuration reference](../reference/runtime-configuration.md)
for identity, connection, audience, and provider settings.

## 6. Prepare operator-owned resources

Before applying the product bundle, create or verify the resources that the
Kustomize base intentionally does not own:

1. Create the approved Identity Broker signing Secret and durable data
   protection key-ring volume.
2. Provide the operator-owned Gateway and MCP backend TLS Secrets and the
   public backend CA ConfigMap. Each certificate must match the service DNS
   name required by the manifests. AKS creates `identity-broker-tls` from a
   managed `DefaultDomainCertificate`; do not create that Secret manually.
3. Resolve all required runtime ConfigMap values before applying the product
   bundle. For an existing installation, use the
   [immutable ConfigMap update procedure](../reference/runtime-configuration.md#update-an-immutable-configmap).
   The Projects & Config map includes only the approved privileged-setup
   issuer, tenant, and subject. Review the exact values in the
   [runtime configuration reference](../reference/runtime-configuration.md#projects-config-settings);
   do not add another subject or role.
4. Set the approved public Web origin and the reviewed Gateway namespace and
   security policy. The product namespace stays `restricted`. The current AKS
   managed Gateway pods cannot set the seccomp profile required by that
   policy, so routing acceptance needs a separately approved Gateway namespace
   using the `baseline` policy. Do not change the product namespace policy to
   work around this limitation.
5. Pin product images to the digests in the reviewed publication receipt.

Do not copy acceptance-only OAuth client IDs or loopback redirect URIs into a
production registration. Keep passwords, OAuth secrets, PFX passwords, tokens,
and private keys out of ConfigMaps and command-line arguments. For a fresh
install, the certificate-only routing stage in step 7 creates the managed
`identity-broker-tls` Secret before the release preflight.

## 7. Render and install the Kustomize bundle

Render the complete bundle and inspect it before applying:

```powershell
kubectl kustomize deploy\k8s
```

This command renders the product Deployments, Services, ServiceAccounts,
runtime ConfigMaps, provider catalog, namespace controls, and network policies.

Before applying, confirm that the active context and API-server host still
match the approved cluster verified in step 4.

After you verify the target, run client-side validation:

```powershell
kubectl apply --dry-run=client -k deploy\k8s
```

This command validates the rendered objects locally; it does not prove that
the cluster will accept them.

For a fresh install, use this order after the target, database, workload
identities, operator-owned TLS resources, and runtime values are ready:

1. Create the managed certificates before the release bootstrap. Use the
   certificate-only form of the guarded routing installer from the
   [Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#application-routing-source-setting).
   It waits for both `DefaultDomainCertificate` resources to become Available
   and for AKS to create `identity-broker-tls`. It does not create routes and
   does not require product Services. Use the returned managed hostname to
   confirm that the approved runtime configuration uses the same issuer and
   Web origin.

2. Apply the Identity Broker migration overlay after its migration identity,
   connection ConfigMap, and approved immutable Broker image are ready. Wait
   for the Job to complete, then verify the runtime database grants. Confirm
   the rendered migration egress policy names
   `agentweaver-pg.postgres.database.azure.com` for DNS and TCP 5432; update
   its reviewed Kustomize patch first if the approved database host differs.
   The Broker runtime checks its schema at startup and does not run migrations.

```powershell
kubectl kustomize deploy\k8s\migrations\identity-broker
kubectl apply -k deploy\k8s\migrations\identity-broker
kubectl wait --for=condition=complete job/identity-broker-migration `
  --namespace agentweaver --timeout=10m
```

3. Run the source-bound release bootstrap with `--execute`. It verifies the
   publication receipt, image digests, target, complete runtime ConfigMaps,
   managed Broker certificate, operator-owned resources, and rendered bundle
   before it applies the root Kustomize bundle. The preflight accepts either
   both valid managed certificates with no Gateway, route, or BackendTLSPolicy,
   or the exact `web-application` route installed by the root bundle with no
   Gateway or BackendTLSPolicy. In that retry state, the route must target
   Gateway `identity-broker` in namespace `agentweaver-gateway` and select
   its shared `https` listener. If the parent reference specifies a port, it must
   be 443. The route must use the managed Portal hostname and route `/` to
   the Web Service on port 8080. The preflight inventories all routes and
   rejects extra or mismatched routing resources. Do not apply the root
   bundle directly with `kubectl`; the release command is the guarded
   workload apply. For patch upgrades, it compares the live immutable
   `agenthost-runtime-v1` and `projects-config-runtime-config` data with the
   rendered ConfigMaps. If either differs, the command preflights the rest of
   the bundle, checks namespace-scoped create and name-scoped delete
   permissions, replaces only the changed map from the source-rendered object,
   and then applies the bundle. Unchanged data follows the normal apply path.
   A read-only dry-run reports which exact replacement is needed. This path
   never replaces `environment-cilium-config`,
   `projects-config-provider-catalog`, or any other ConfigMap. See
   [runtime configuration](../reference/runtime-configuration.md#guarded-pre-acceptance-configmap-patch-upgrades)
   for the scope and behavior.

   ```powershell
   npm run release:deploy-bootstrap -- v1.0.6 `
     --manifest releases/foundation.json `
    --publication artifacts/release/publish/publication.json `
     --subscription-id <subscription-id> `
     --tenant-id <tenant-id> `
     --resource-group agentweaver `
     --cluster agentweaver-aks `
     --namespace agentweaver `
     --node-min 2 `
     --node-max 3 `
     --provider-catalog-configmap projects-config-provider-catalog `
     --provider-catalog-source-commit b002620bd8cb53a971b6d5b082d6ab43dbf11fb7 `
     --execute
   ```

   With `--execute`, the guarded deployment command installs AgentSandbox
   before applying the product bundle. It uses the upstream v1.0.7 release
   manifest and verifies its SHA-256
   (`136c10c9347cb5a6740b351444cae82f404e883c3f520272257ba785f2af0da8`),
   applies it idempotently, waits for all four CRDs and the controller rollout,
   and confirms the controller image is
   `registry.k8s.io/agent-sandbox/agent-sandbox-controller:v1.0.7`. The asset
   is pinned to source commit `807cc37107bd7c012115afac9fa9c3b77b8365c2`.
   Before writing, the installer validates the full manifest locally,
   server-validates cluster-scoped resources, and checks the required
   get/create/patch permissions for controller resources in
   `agent-sandbox-system`. This also supports a fresh install where that
   namespace does not exist yet.
   This procedure installs the upstream controller directly; it does not
   enable the AKS AgentSandbox extension, which remains a future option.

   Registry token, manifest, and image-config reads, and the pinned upstream
   manifest download, retry network `TypeError` failures and HTTP 429/5xx up
   to four attempts with 1, 2, and 4 second waits. Authorization failures,
   other statuses, and digest/content mismatches do not retry. Exhaustion
   reports the credential-free request URL and underlying network cause.
   If apply completed before verification failed, preserve the checkpoint
   and exact inputs. After connectivity recovers, use the same separately
   authorized command with `--resume --execute`: apply is skipped only for
   the matching completed stage, and all live verification still runs.
   See the [release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md#local-v1-release-deployment)
   for retry scope, diagnostics, and checkpoint recovery.

   The [v1.0.6 GitHub Release and tag](https://github.com/sabbour/agentweaver/releases/tag/v1.0.6)
   are already published. Do not create another release or tag after the
   bootstrap-pins pull request merges. Use the publication receipt for source
   `472601710cf1cf22d98d9cbe7492893768ce1218` and source tree
   `bf63850ebb07d325bb0d0b34892d6e6cad04a240`. Verify the publication SHA-256
   `df3609ed0d05da5c3bd2679752e6ed049c8bdf0cf731631e7a5db88cc04d5b05` and
   provenance SHA-256
   `b68f0cbfa8571136af1a13ca887d52c3761f57bf96634f802a183a9cb171b402`.
   The receipt measures AgentHost version `1.0.3` on `linux/amd64` with image
   config digest
   `sha256:167048aa1c859809f28fae913c5fc871d7dea4b87a932bde1c9fe9e46659a5a8`
   and compressed pull size `321486521` bytes. Use the reviewed
   provider-catalog ConfigMap and source commit shown in the command; do not
   rebuild or substitute image tags. See
   [local v1 release deployment](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md#v106-pre-acceptance-bootstrap)
   for all image digests and the full admission contract.

4. After the release bootstrap succeeds and the product Services exist, run
   the full, default-off `--bootstrap-identity-routing` operation without
   `--identity-routing-certificates-only`. It checks the exact private HTTPS
   MCP Service before any route write, then applies and verifies the Gateway,
   routes, backend TLS policies, and narrow ingress policies. The installer
   does not guess `<managed-domain>`, change the Broker application, or create
   service TLS certificates. An optional callback append requires a separate
   explicit flag and the exact route-derived URI.

Use the guarded release tooling for a published-image rollout. The
`release:deploy-bootstrap` path is for its explicitly admitted pre-acceptance
bootstrap; `release:deploy` is the separate release-acceptance deployment
path. Both require a source-bound publication and explicit `--execute` for
cluster writes. See [local v1 release deployment](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md#local-v1-release-deployment)
and the [Kubernetes operator guide](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md).

## 8. Verify the installation

1. Confirm the expected namespace and workloads exist:

   ```powershell
   kubectl get deployments,services -n agentweaver
   kubectl get pods -n agentweaver
   ```

2. Check each required Deployment rollout:

   ```powershell
   kubectl rollout status deployment/identity-broker -n agentweaver
   kubectl rollout status deployment/projects-config -n agentweaver
   kubectl rollout status deployment/knowledge -n agentweaver
   kubectl rollout status deployment/events-and-sessions -n agentweaver
   kubectl rollout status deployment/orchestrator -n agentweaver
   kubectl rollout status deployment/environment -n agentweaver
   kubectl rollout status deployment/gateway -n agentweaver
   kubectl rollout status deployment/mcp -n agentweaver
   kubectl rollout status deployment/web -n agentweaver
   ```

### Bootstrap the first Projects & Config TenantAdmin

Run this command once after the Projects & Config Deployment is ready and
the approved user has signed in through the Identity Broker once. Until the
command succeeds, the Projects page returns `403`. The database is private.
Run the command in a one-shot Job inside the cluster network. Use the exact
Projects & Config image digest already approved for this deployment.

The command uses the issuer, subject, and tenant in
`projects-config-runtime-config`. It does not accept identity arguments.
The approved subject is the `sub` claim in tokens issued by the Identity
Broker. The Broker replaces the Entra identity with its own local user ID, so
this value is not the user's Entra object ID. The Broker creates the user
record on first sign-in. After that sign-in, read the ID from the Broker
database. Use the Identity Broker migration/schema-owner role, or another role
with `SELECT` on `identity_broker.broker_users`. The Projects & Config
migration role cannot read the `identity_broker` schema. `Email` is optional and
can be empty, so confirm the row by display name and creation time:

```sql
SELECT "Id", "Email", "DisplayName", "CreatedAt"
FROM identity_broker.broker_users
WHERE "Disabled" = false
ORDER BY "CreatedAt";
```

Set that ID as `ProjectsConfig__PrivilegedSetup__ApprovedSubject` before
running the command.
Provide the separate Projects & Config migration/schema-owner connection and a short-lived
PostgreSQL Entra access token through a Kubernetes Secret. Do not put the
token in a command argument or log.

In PowerShell, write the access token to a protected local file without
printing it:

```powershell
az account get-access-token --resource-type oss-rdbms --query accessToken --output tsv |
  Set-Content -NoNewline .\projects-config-access-token.txt
```

Put the password-free migration connection string in
`projects-config-migration-connection.txt`. Its Entra role must own the
Projects & Config schema and migration history. Create a short-lived Secret
from these files:

```powershell
kubectl create secret generic projects-config-first-admin `
  --namespace agentweaver `
  --from-file=migration-connection=.\projects-config-migration-connection.txt `
  --from-file=access-token=.\projects-config-access-token.txt
```

Create a Job with the approved Projects & Config image. Copy the pod labels
selected by the existing Projects & Config network policy so the Job can reach
the private PostgreSQL server. Disable service-account token mounting. The
network policy allows DNS lookups only for exact approved names, so set the
pod's DNS `ndots` option to `1`. Otherwise the resolver can try cluster
search-domain names first and fail before it reaches the PostgreSQL name:

```yaml
dnsConfig:
  options:
    - name: ndots
      value: "1"
```

Set the container command and environment to the following. The namespace
LimitRange requires at least `25m` CPU and `32Mi` memory for every container,
including init or helper containers. A container that requests less is
rejected, and the Job never creates a pod.

```yaml
command:
  - dotnet
  - Agentweaver.Projects.Config.dll
  - --bootstrap-first-tenant-admin
env:
  - name: ProjectsConfig__Authentication__Authority
    valueFrom:
      configMapKeyRef:
        name: projects-config-runtime-config
        key: ProjectsConfig__Authentication__Authority
  - name: ProjectsConfig__PrivilegedSetup__ApprovedIssuer
    valueFrom:
      configMapKeyRef:
        name: projects-config-runtime-config
        key: ProjectsConfig__PrivilegedSetup__ApprovedIssuer
  - name: ProjectsConfig__PrivilegedSetup__ApprovedSubject
    valueFrom:
      configMapKeyRef:
        name: projects-config-runtime-config
        key: ProjectsConfig__PrivilegedSetup__ApprovedSubject
  - name: ProjectsConfig__PrivilegedSetup__ApprovedTenantId
    valueFrom:
      configMapKeyRef:
        name: projects-config-runtime-config
        key: ProjectsConfig__PrivilegedSetup__ApprovedTenantId
  - name: ConnectionStrings__ProjectsConfigMigration
    valueFrom:
      secretKeyRef:
        name: projects-config-first-admin
        key: migration-connection
  - name: ProjectsConfig__Migration__AccessToken
    valueFrom:
      secretKeyRef:
        name: projects-config-first-admin
        key: access-token
```

Run the Job and read its result:

```powershell
kubectl wait --for=condition=complete job/projects-config-first-admin `
  --namespace agentweaver --timeout=10m
kubectl logs job/projects-config-first-admin --namespace agentweaver
```

The command reports whether it created the grant or found the same active
grant. A different active TenantAdmin stops the command without changes.
After the Job succeeds, remove the Job and Secret and delete the local token
file. Sign in as the approved user and load the Projects page. The page should
open without `403`. The runtime database role stays read-only on authority
tables until an operator applies the separately approved role-assignment
grants. See [Runtime authority writes](../reference/runtime-configuration.md#runtime-authority-writes).

3. Check readiness, the deployed image digests, and the exact ServiceAccount
   on each workload. Review only the affected pod's logs if a rollout fails.
   If you enabled public routing, verify the Gateway and route conditions too:

   ```powershell
   kubectl get gateways,httproutes -A
   ```

   Confirm the Gateway is programmed and each required route is accepted with
   resolved backend references. A routing receipt alone does not prove
   end-to-end HTTPS readiness.
4. Exercise the approved Identity Broker health endpoints over trusted HTTPS.
   Then make one authorized request through Gateway and one denied request.
   The owner service must enforce current authorization for both.
5. Run the required practical checks in
   [Azure acceptance](./azure-acceptance.md). Record the exact source and
   image versions with sanitized results.

## Expected result

The cluster contains the reviewed resources in namespace `agentweaver`, the
required workloads reach readiness, and authorized requests reach their
owning service. If you enabled public routing, the Gateway and required routes
are programmed for the approved hostnames derived from `<managed-domain>`. A
rendered manifest, healthy pod, routing receipt, or `202 Accepted` response
does not prove a complete user workflow, a successful asynchronous effect, or
release acceptance.

## Related information

- [Kubernetes operator guide](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md)
- [Runtime configuration reference](../reference/runtime-configuration.md)
- [Azure acceptance](./azure-acceptance.md)
- [Configure Knowledge memory](./configure-knowledge.md)
- [Configure Microsoft Entra Workload ID on AKS](https://learn.microsoft.com/azure/aks/workload-identity-deploy-cluster)
