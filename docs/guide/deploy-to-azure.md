# Deploy Agentweaver to Azure

Use this how-to to prepare the Azure foundation, bootstrap PostgreSQL and
workload identities, install the reviewed Kubernetes resources, and verify
the workloads. The commands can change Azure and cluster state. Run them only
for a separately approved target.

Agentweaver source includes Bicep templates and a Kustomize installation.
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
- A clean checkout at the exact source commit approved for deployment.
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
Key Vault name, exact source, and Bicep inputs. The existing P0 path does not
enumerate existing resources or compare their properties after deployment. Use the full-foundation
template only for an approved initial deployment into an empty managed-resource
target. For an existing foundation cluster, the AKS-only template refreshes the three
workload-identity federated credentials and creates or updates the `kata` node
pool without PUTting AKS. Its what-if must not contain a managed-cluster
change. Review the what-if output before execution.
The top-level what-if does not show resources inside the nested workload-identity
federation and Kata node pool deployments. The preflight lists the three
federated identity credentials and the `kata` node pool that the nested
deployments will create or update. It does not query existing credentials or
node pools to distinguish creates from updates.

### Use a new separate nonproduction group

For a first foundation in a separate group, add `--new-foundation` and set
`--resource-group` to the exact new prefix. Use 6–12 lowercase letters and
digits, starting with a letter. The reviewed parameters must use the same
prefix, a Key Vault name starting with `<prefix>-`, and the same region for
the cluster and Monitor workspace. They must not select
existing custom DNS zones. The resource group must already exist, contain no
resources or deployment history, and have the required Agentweaver ownership
tags: `agentweaver:environment=v1-p0`, `agentweaver:managed-by=bicep`,
`agentweaver:owner`, and `agentweaver:cost-center`. The owner and cost-center
tags must match the parameters. Create and tag
the group only with separate approval. The installer does not create it.

The new path requires Azure CLI 2.75.0 or later and permission to list deleted
Log Analytics workspaces. The installer checks the empty group, deployment
history, and deleted workspace inventory twice. It requires
the derived AKS managed group `<prefix>-aks-nodes` to be absent, supplies that
name to AKS, and verifies the returned name. It rejects what-if results with
unresolved diagnostics, potential changes, non-create, or out-of-group changes. It compares two what-if
results before submission. These checks do not prove all provider effects:
the existing Monitor add-on step still runs after the deployment. Later
install steps accept the new group only when it is a registered target. See
[Use a registered separate target](#use-a-registered-separate-target).

Use one operator and stop competing automation for this target. The checks
are not atomic: another writer can change the target after a check. If a
submission times out or its outcome is uncertain, stop and reconcile the
resource group, deployment history, and AKS managed group manually. Do not
start another installation until reconciliation is complete. Azure CLI may
retry the same deployment request, but the installer does not rerun deployment
after an uncertain result.

### Use a registered separate target

The tracked registry `scripts/azure/lib/targets.mjs` lists each install
target. The target name is the resource group name and the `namePrefix`. Each
entry names the foundation parameter file and the AKS-only parameter file of
that target:

| Target | Foundation parameters | AKS-only parameters |
|---|---|---|
| `agentweaver` | `p0-integration.approved.json` | `p0-aks-redeploy.approved.json` |
| `awwus2np01` | `awwus2np01.approved.json` | `awwus2np01-aks-redeploy.approved.json` |

All files are in `infra/bicep/parameters/`. To add a target, add its entry and
both parameter files in one reviewed change. In the same change, add the name
to the `namePrefix` allowed values in `infra/bicep/p1-runtime-identities.bicep`
and `infra/bicep/p1-session-material-access.bicep`.

Set `--resource-group` to the target name. After the foundation exists, these
steps accept a registered target:

- the `plan.mjs` what-if;
- the `deploy.mjs` AKS-only redeployment, with `--parameters` set to the
  AKS-only file of the same target; and
- the `deploy.mjs` options for Identity PostgreSQL, identity routing, Broker
  state, and Broker runtime bootstrap.

An unregistered name fails before any Azure call. The AKS-only path rejects
the parameter file of another target.

In the manual `az aks` commands in step 4, replace `agentweaver` with the
target name and `agentweaver-aks` with `<target>-aks`.

These later steps also accept `--resource-group <target>`:

- `npm run db:maintenance:setup -- --resource-group <target> --execute`
  creates the Projects Config migration identity and its Kubernetes service
  account. For a separate target, it does not create the GitHub workflow
  identity, credentials, or role assignments.
- `node scripts\azure\bootstrap-p1-postgres.mjs --resource-group <target>`
  initializes the P1 PostgreSQL databases. With `--inspect` or
  `--apply-pending-migrations`, it inspects or continues them.
- `npm run db:maintenance -- --resource-group <target>` in every mode,
  including the full migration run.
- Identity Broker provider setup (`deploy.mjs --identity-broker-provider-mode`
  `render` or `activate`). See
  [Configure the optional GitHub providers](#configure-the-optional-github-providers).
- `npm run db:maintenance:projects-config -- --resource-group <target>`.
- `npm run azure:deploy-from-commit -- <sha> --resource-group <target>`.

Service deployment uses `deploy/k8s/targets/<target>.json`. This file lists
the exact text replacements (host names, client IDs, and the first
PlatformAdmin subject) that the deployment applies to the rendered
`agentweaver` manifests. Do not edit the `agentweaver` runtime configuration
files for another target. The deployment also reads the API server of the
selected Kubernetes context. It stops if the server is not the `aksApiServer`
of the target in the registry. For the `agentweaver` target, it stops if the
server belongs to another registered target.

The P1 PostgreSQL role names are the same on every target, for example
`agentweaver-id-projects-config`. The Azure managed identity names start with
the target name, for example `<target>-id-projects-config`.

These steps still accept only `agentweaver`: the Foundation Probe options and
`--align-existing-identity-broker-hostname`. `deploy.mjs` rejects them for
another target before any Azure call.

Both templates create or update the `kata` node pool for sandbox pods (AKS Pod
Sandboxing, Azure Linux, SSH disabled, 1-2 nodes of `kataVmSize`, default
`Standard_D4s_v5`). You do not
add the pool manually. If a pool named `kata` already exists with another OS
SKU, workload runtime, VM size, or subnet, the deployment fails; delete that
pool and run the deployment again. For the cost, see
[Kata node pool](https://github.com/sabbour/agentweaver/blob/v1/infra/bicep/README.md#kata-node-pool).

Both templates also end with the AKS Azure Monitor add-on enabled. The
installer enables the add-on with the `<prefix>-law` workspace, or adopts it
when it is already enabled. You cannot turn it off. The installer stops if the
add-on sends to another workspace or uses shared-key authentication. The only
exception is a cluster in a canary region (such as `eastus2euap`) with the
workspace in a production region. Azure Monitor does not support that layout,
so the installer skips the add-on with a warning, and container logs are not
collected. For the required permissions, the cost, and the canary-region
limit, see
[Azure Monitor add-on](https://github.com/sabbour/agentweaver/blob/v1/infra/bicep/README.md#azure-monitor-add-on).

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

Set `monitorLocation` to a region that supports Log Analytics and Application
Insights. It can match `location` when the foundation resources are also
available there. The full-foundation template creates its own workspace; it
does not reuse an existing workspace.

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

   You do not apply the workspace StorageClass manually. When
   `agentweaver-workspace-azurefile` is missing, `npm run deploy:kustomize`
   creates it from `deploy\k8s\cluster\workspace-storageclass.yaml`.
   Environment requires this class for workspace volumes. The AKS built-in
   `azurefile-csi` class does not have the required mount options. You cannot
   change the parameters or mount options of an existing StorageClass. If a
   class with this name already exists with other values, the deployment stops.
   Delete that class before any workspace volume uses it, then run the
   deployment again.

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
service runtime-identity template. Apply it with the guarded installer option
`--bootstrap-runtime-identities` and the AKS-only template and parameters for
your target. Do not apply the template with a direct `az deployment` command.
See
[Limited service runtime identities](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#limited-p1-runtime-identities)
for the command and its checks.

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
receipts. The reviewed PostgreSQL maintenance plan includes that migration and grants
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
Orchestrator migration 025 adds caller-specific notification dismissals. It
creates one new, empty table, `user_notification_dismissals`, and doesn't alter
or scan existing tables. The reviewed plan grants `SELECT, INSERT` on that
table.
Orchestrator migration 031 adds caller-specific promotion dismissals in the
new `user_notification_promotion_dismissals` table. It requires migrations
025 and 030. Its foreign key to `backlog_plan_promotions` takes a
`SHARE ROW EXCLUSIVE` lock on that table until commit. The reviewed plan
grants only `SELECT, INSERT` on the new table. Migration 031 and its grants
are source changes only; applying them requires separate approval.

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
service principal IDs come from the reviewed plan and are not refreshed from Azure.
The command then runs the Identity Broker and database initialization stages through one
temporary loopback tunnel. It requires a clean checkout at the reviewed
`origin/v1` source. The operator must already have the required AKS access and
PostgreSQL Entra administrator authority; the command creates no Azure role
assignments, access grants, or product workloads. Keep the bootstrap receipt and
verify the database and roles with read-only catalog queries before installing
the product bundle. The Broker runtime identity does not own the schema. If
the identity or database setup is not approved or complete, stop before
applying product workloads.

If the database is already initialized, don't rerun initialization. Apply later
reviewed migrations with the
[database maintenance runner](database-maintenance.md), which also keeps the
services on matching images. Services verify their schema version at startup,
so apply migrations before you install workloads that expect them.

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

The checked-in release/Kustomize composition still includes MCP compatibility
image, workload and ingress resources. Their presence does not make MCP a
supported product or acceptance surface. Supported client paths are
Gateway/API, browser and native .NET CLI. Outbound remote MCP connections are
separate.

1. Create the approved Identity Broker signing Secret and durable data
   protection key-ring volume.
2. Provide the operator-owned Gateway and MCP compatibility backend TLS
   Secrets, plus the public backend CA ConfigMap. Each certificate must match
   the service DNS name required by the manifests. Also provide
   `identity-broker-internal-tls` in namespace `agentweaver` as a
   `kubernetes.io/tls` Secret. Its certificate must match
   `identity-broker.agentweaver.svc.cluster.local` and chain to
   `service-backend-ca`. To issue these certificates, follow
   [Issue an internal service certificate](#issue-an-internal-service-certificate).
   AKS creates the separate public
   `identity-broker-tls` Secret from a managed `DefaultDomainCertificate`; do
   not create that Secret manually.
3. When using the AgentHost profile, provide `agenthost-tls` in namespace
   `agentweaver` as a `kubernetes.io/tls` Secret for the configured HTTPS
   endpoint. The deployment helper checks this Secret and every other required
   Secret before it changes the cluster. It reads only the Secret names and
   types. It does not create the Secrets or inspect their certificates and
   keys. Verify those values through the approved operator process.
4. Resolve all required runtime ConfigMap values before applying the product
   bundle. For an existing installation, use the
   [immutable ConfigMap update procedure](#replace-an-immutable-runtime-configmap).
   For a first install, stage the Broker map after both managed certificates
   are Available and before Kustomize deployment. Follow the
   [Broker ConfigMap bootstrap procedure](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#bootstrap-the-first-install-broker-configmap).
   The Projects & Config map includes only the approved privileged-setup
   issuer, tenant, and subject. Review the exact values in the
   [runtime configuration reference](../reference/runtime-configuration.md#projects-config-settings);
   do not add another subject or role.
5. Set the approved public Web origin and the reviewed Gateway namespace and
   security policy. The product namespace stays `restricted`. The current AKS
   managed Gateway pods cannot set the seccomp profile required by that
   policy, so routing acceptance needs a separately approved Gateway namespace
   using the `baseline` policy. Do not change the product namespace policy to
   work around this limitation.
6. Confirm each selected image is available in the chosen registry. The v1
   image workflow pushes to GHCR. For an ACR candidate, use the local build
   command in the [release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md#container-images).
   Add `--registry <acr-login-server>` to the deployment command when you use
   ACR.

Do not copy acceptance-only OAuth client IDs or loopback redirect URIs into a
production registration. Keep passwords, OAuth secrets, PFX passwords, tokens,
and private keys out of ConfigMaps and command-line arguments. For a fresh
install, complete the certificate-only routing stage below so AKS creates the
managed `identity-broker-tls` Secret before deploying the workloads.

### Issue an internal service certificate

Agentweaver does not issue or rotate internal service certificates. Use this
procedure for `identity-broker-internal-tls` and for each internal
`<service>-tls` Secret. The example uses the Identity Broker name
`identity-broker.agentweaver.svc.cluster.local`.

> [!IMPORTANT]
> An upgrade to a release that contains the Identity Broker internal TLS
> change needs `identity-broker-internal-tls`. Create it before you deploy.
> If the selected source commit also contains the required Secret check, the
> deployment helper stops before it changes the cluster when the Secret is
> missing. Older commits do not have this check.

You need the private key of the CA whose certificate is in ConfigMap
`service-backend-ca`. Keep this key in the approved operator store. Do not put
it in the cluster, in a ConfigMap, or in a log. If you cannot get this key,
stop. Do not make a second CA for one Secret, because the services trust only
the CA in `service-backend-ca`. To replace the CA, plan an outage, issue new
certificates for every internal Secret, replace the Secrets and the
ConfigMap, and restart the workloads.

1. Get the public CA certificate:

   ```bash
   kubectl get configmap service-backend-ca --namespace agentweaver \
     --output=jsonpath='{.data.ca\.crt}' > ca.crt
   ```

2. Make a P-256 key, and sign a server certificate for the service DNS name.
   Replace `ca.key` with the path to the CA key from the approved store:

   ```bash
   NAME=identity-broker.agentweaver.svc.cluster.local
   openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
     -keyout tls.key -out tls.csr -subj "/CN=$NAME"
   printf 'basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:%s\n' \
     "$NAME" > tls.ext
   openssl x509 -req -in tls.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
     -days 90 -sha256 -extfile tls.ext -out tls.crt
   ```

3. Verify the chain and the name:

   ```bash
   openssl verify -CAfile ca.crt tls.crt
   openssl x509 -in tls.crt -noout -checkhost "$NAME" -enddate
   ```

4. Create or update the Secret:

   ```bash
   kubectl create secret tls identity-broker-internal-tls \
     --cert=tls.crt --key=tls.key --namespace agentweaver \
     --dry-run=client --output=yaml | kubectl apply --filename=-
   ```

5. Delete `tls.key`, `tls.csr`, and `tls.ext` from the local disk.

To make sure that a service uses a replaced certificate, restart the
Deployment that mounts the Secret.

## 7. Run the deployment

Use the [release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md#deploy-workloads-locally)
for local execution, monitoring, recovery, and context verification. Complete
the one-time identity and PostgreSQL ownership prerequisites before running
the local command; it runs migrations and grants before applying workloads.

Before a migration or Kubernetes write, the local helper requires the live
`identity-broker-runtime-config-v3` ConfigMap to match the complete rendered
Broker map. It also checks that the Broker Deployment references this map. A
missing, malformed, or different map stops the command before writes. The
helper does not create, replace, or repair the map. For a first install, use
the [Broker ConfigMap bootstrap procedure](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#bootstrap-the-first-install-broker-configmap).

### First-install Broker ConfigMap order

For a fresh installation, follow this order:

1. Complete certificate-only routing from the
   [Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#application-routing-source-setting).
2. Create the exact Broker ConfigMap with the
   [ConfigMap-only installer stage](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#bootstrap-the-first-install-broker-configmap).
3. Run the Kustomize workload deployment.
4. After the product Services exist, run full identity routing.

Do not run `--bootstrap-identity-broker-runtime` after Kustomize deployment as
part of this first-install sequence. Kustomize intentionally removes four
entries from the Broker egress policy. The standalone runtime bootstrap checks
the untrimmed source policy hash and rejects the rendered policy. Kustomize
creates the Broker workload resources. The deployment command does not
provision Azure resources, certificates, runtime configuration, or external
permissions.

For routing-only maintenance on an existing platform, use the
[`--identity-routing-only` procedure](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#identity-routing-only-mode-for-an-existing-platform).
It keeps the source, target, namespace-policy, TLS, ownership, and
current-generation preflights but skips ARM what-if/create and nested FIC
deployment. `--execute` still writes Kubernetes routing resources and requires
separate authorization; it does not change Entra registration or runtime URLs.

## 8. Verify the installation

1. Confirm the expected namespace and workloads exist:

   ```powershell
   kubectl get deployments,services -n agentweaver
   kubectl get pods -n agentweaver
   ```

2. The deployment workflow already checks every rollout. To inspect them
   again, use the selected context:

   ```powershell
   kubectl --context <approved-kubernetes-context> --namespace agentweaver get deployments,services,pods
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

This procedure does not perform or claim a separate live login check with a
second account. Installation does not depend on that check.

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

### Bootstrap the first PlatformAdmin and configure runtime defaults

Run these steps after the Projects & Config schema is migrated and the
TenantAdmin is ready. A fresh installation has no platform runtime defaults
revision. Until one is written, run selection returns
`409 platform_runtime_unconfigured`.

The PlatformAdmin bootstrap uses the approved issuer, subject, and tenant in
`projects-config-runtime-config`. It does not accept identity arguments. It
uses the immutable Projects & Config image selected for the deployment,
verifies that the schema is already migrated, and runs one Job under the
database-maintenance lock.
The Job uses the approved identity configuration and the same short-lived
schema-owner database credential as the migration command. The runner removes
the Job and credential Secret and writes its receipt outside the checkout.

From a clean checkout of the fetched `origin/v1` tip, first check the plan:

```powershell
npm run db:maintenance:projects-config -- `
  --image ghcr.io/sabbour/agentweaver.projects.config@sha256:<digest> `
  --bootstrap-first-platform-admin
```

For execution, provide the password-free migration connection and a
short-lived PostgreSQL access token through the documented environment
variables. Read them from protected local files without printing their
contents:

```powershell
$env:AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION =
  Get-Content -Raw .\projects-config-migration-connection.txt
$env:AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN =
  Get-Content -Raw .\projects-config-access-token.txt
npm run db:maintenance:projects-config -- `
  --image ghcr.io/sabbour/agentweaver.projects.config@sha256:<digest> `
  --bootstrap-first-platform-admin --execute
```

The runner reports whether it created the PlatformAdmin, found the same
approved grant, or refused because another active PlatformAdmin exists. Stop
if it reports a conflict. The command verifies the migration history and does
not rerun migrations or apply runtime grants.

If the bootstrap Job fails unexpectedly, its receipt records the stable failure
outcome, exception type, an allowlisted configuration category when applicable,
and validated PostgreSQL metadata. It omits free-form exception messages. A
failed receipt does not prove that no grant committed. Before deciding
whether a retry is appropriate, run the repository-owned read-only inspection
from a clean checkout of the fetched `origin/v1` tip. Use the same publication
receipt and protected migration connection and access-token environment
variables shown above:

```powershell
npm run db:maintenance:projects-config -- `
  --image ghcr.io/sabbour/agentweaver.projects.config@sha256:<digest> `
  --inspect-first-platform-admin
```

Inspection is an executing read-only operation; do not add `--execute`. It
verifies the pinned published image, approved immutable identity configuration,
and migration history, then runs under the database-maintenance lock using the
same short-lived credential Secret and Job path. The database transaction is
`REPEATABLE READ` and explicitly `READ ONLY`. The command does not grant,
revoke, repair, or migrate data. The runner removes the Job and Secret and
writes a sanitized receipt outside the checkout.
For inspection, the runner disables optional GSS encryption negotiation in the
Job connection string; the required `SSL Mode=VerifyFull` still validates the
PostgreSQL server certificate and hostname. This avoids relying on an optional
native Kerberos library in the minimal service image.

Read the `platformAdminInspection` object in that receipt. `membership.state`
is `active` or `absent`; `activeAssignments` lists each active platform
assignment's ID, revision, state, resource type (`Platform`), and resource ID
(`default`) and role (`PlatformAdmin`). The membership and role-assignment audit
arrays include only matching `membership_granted` and `role_assigned` rows,
with resource type, resource ID, role when applicable, timestamp, and the
membership/assignment IDs they match (`membership_granted` has no resource type
or resource ID, both represented as `null`). An empty
audit array means no matching audit evidence was found. The
`otherActivePlatformAdminCount` field reports only a count and never exposes
other identities. If an assignment lacks its matching audit row, the approved
membership is absent, the inspection fails, or the result is otherwise
ambiguous, stop and investigate; do not retry. A successful readback is
evidence for a separate operator decision, not authorization to retry. Do not
replace this path with ad hoc database queries or `kubectl exec`.

After the bootstrap succeeds, write the reviewed defaults in
`deploy/k8s/runtime-configs/platform-runtime-defaults.json` through the public
Gateway API. The script opens the existing browser sign-in flow for invocation setup and requests
the `P1_SETUP_SCOPES`, including `api.read` and `projects.admin`. Review and
approve the scopes for the signed-in PlatformAdmin. Tokens remain in memory
and are never displayed. Pass the tenant ID from the harness manifest's
`environment.tenantId` field with `--tenant`; the script sends it as
`X-Agentweaver-Tenant` on every API request.

Run a comparison first:

```powershell
npm run platform:runtime-defaults -- `
  --base-url https://<gateway-host> `
  --issuer https://<identity-broker-host>/ `
  --audience https://<api-audience>/ `
  --tenant <tenant-id>
```

Exit code `2` means the current defaults differ from the reviewed file. To
write them, add `--execute`. If the defaults already match, `--execute` is a
no-op and reports the current revision. Otherwise, the script uses the current
platform revision as `expectedRevision`, then reads the configuration again
and checks it before reporting the new revision. For optional fields, the
comparison treats an omitted value as null. It compares all other values,
including any credential reference, and rejects redaction markers:

```powershell
npm run platform:runtime-defaults -- `
  --base-url https://<gateway-host> `
  --issuer https://<identity-broker-host>/ `
  --audience https://<api-audience>/ `
  --tenant <tenant-id> `
  --execute
```

If the write or readback fails, the write may already have created a revision.
Check the current revision and configuration before you retry. Stop if the
readback is unavailable or ambiguous. CLI display redaction does not verify a
credential reference.

Run selection is ready only after the script confirms a configured platform
revision and matching defaults readback. Before rerunning the acceptance harness, set
`fixturePlan.expectedPlatformRuntimeRevision` in its manifest to the verified
revision reported by the script. A stale expected revision still causes run
selection to return `409`.

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

## Configure the optional GitHub providers

Use the existing Azure installer to add GitHub Copilot and GitHub Repo App
startup settings to Identity Broker. This procedure does not create provider
registrations, import or rotate secrets, deploy a release, or make live model
calls.

Before you start, get the exact provider registration values and the
references to the secrets that are already managed. Do not put secret values
in the input file. You need Node.js 24 to render the plan.

To activate the plan, also confirm that you have an approved non-production
target with Identity Broker already installed, and a signed-in Azure CLI,
`kubectl`, and `kubelogin`. Your identity must be able to read the Broker's
source ConfigMap and Deployment, create a ConfigMap, and patch and observe the
Deployment.

The diagram shows the offline plan, manual registration, revision checks, and
activation recovery. The installer preserves existing `envFrom` sources. An
explicit `env` entry takes precedence over an `envFrom` value with the same
name; the installer does not read Secret data.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-provider-setup.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-identity-provider-setup.png'" alt="The installer renders a deterministic offline plan from non-secret input and local source, then prints exact URLs for manual provider registration. Activation checks the selected target, source drift, and expected ConfigMap and Deployment resource versions. It creates an immutable successor map before a revision-fenced patch changes only provider environment values, the explicit OAuth Secret reference, the active map reference, and a fingerprint. The installer reads back the active map and rollout state, keeps the old map, and reports readback after a partial failure. It never reads Secret data or automates provider registration." />
  </a>
  <figcaption>Provider setup uses an offline plan and a guarded, revision-fenced activation. Provider registration and secret management remain separate operator tasks.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-provider-setup.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-provider-setup.drawio'">Open editable draw.io source</a></p>

Create a JSON input file. This example has no secret values. Replace each
sample provider value with the matching registration value. Replace each
sample secret name and version with the exact name and version of an existing
Key Vault secret. The sample secret references show valid syntax; they do not
identify real secrets. `AppId` must be a positive JSON integer. The sample
value `12345` shows the required type; replace it with the registered App ID.

```json
{
  "IdentityBroker": {
    "WebOrigin": "https://<portal-host>",
    "CopilotConnection": {
      "ProjectsOwnerAddress": "https://<projects-owner>/",
      "ClientId": "<copilot-client-id>",
      "CallbackUri": "https://<portal-host>/auth/github/copilot-app/callback",
      "ClientSecretReference": {
        "Id": "copilot-client-secret",
        "Version": "0123456789abcdef0123456789abcdef"
      }
    },
    "GitHubRepoApp": {
      "OAuthClientId": "<repo-app-oauth-client-id>",
      "CallbackUri": "https://<portal-host>/auth/github/repo-app/callback",
      "AppId": 12345,
      "AppSlug": "<repo-app-slug>",
      "PrivateKeySecretId": "repo-app-private-key",
      "PrivateKeySecretVersion": "0123456789abcdef0123456789abcdef"
    }
  },
  "SecretBindings": {
    "IdentityBroker__GitHubRepoApp__OAuthClientSecret": {
      "SecretKeyRef": {
        "Name": "<existing-kubernetes-secret-name>",
        "Key": "<existing-secret-key>"
      }
    }
  }
}
```

`WebOrigin` must be the exact HTTPS origin, with no path, query, or fragment.
`ProjectsOwnerAddress` must be the HTTPS root address for the Projects owner.
It must not include a path other than `/`, a query, or a fragment. Each Key
Vault secret ID field takes a secret name, not a full Key Vault URL. A secret
name can contain 1 to 127 ASCII letters, digits, or hyphens. A secret version
can contain 1 to 256 ASCII letters, digits, periods, underscores, hyphens, or
colons.
The callback values must be exactly:

- Copilot OAuth Redirect URI:
  `https://<portal-host>/auth/github/copilot-app/callback`
- GitHub Repo App OAuth Redirect URI:
  `https://<portal-host>/auth/github/repo-app/callback`
- GitHub Repo App Setup URL:
  `https://<portal-host>/auth/github/repo-app/callback`

Register these values manually in the matching provider settings. The
installer prints the same checklist; it does not call GitHub or change provider
registrations. The Repo App OAuth client secret must already exist in the
named Kubernetes Secret and key. The input contains only that reference. The
Copilot client-secret and Repo App private-key fields are exact Key Vault
secret IDs and versions, not secret values.

The current Broker Deployment has an optional broad Secret `envFrom`. For this
source, include both `CopilotConnection` and `GitHubRepoApp`; omitting either
could leave its settings supplied by the existing Secret. The input parser
rejects an omitted section, unknown field, empty value, duplicate JSON key,
or partial section. Errors name a field only and do not echo values.

### Render and review the plan

Run this command from the repository root in PowerShell. Use a new output
path; the command does not overwrite an existing file:

```powershell
node scripts\azure\deploy.mjs `
  --identity-broker-provider-config .\provider-setup.json `
  --identity-broker-provider-mode render `
  --identity-broker-provider-output .\provider-setup-plan.json
```

For a registered separate target, add `--resource-group <target>`. The plan
then starts from the target values in `deploy/k8s/targets/<target>.json`.
Render takes no other Azure option.

Render reads the local v1 source and the non-secret JSON file. It makes no
network or Azure call, does not authenticate, does not open a browser, does not
read Secret data, and does not change cluster resources. Review the rendered
immutable ConfigMap data, explicit Secret reference, and registration
checklist. Correct the input and render to a different new output path if the
plan is wrong.

### Activate the reviewed plan

After you finish provider registration and review the plan, read the current
resource versions from the approved `<target>-aks` cluster (`agentweaver-aks`
for the default target). Confirm that your current `kubectl` context targets
that cluster before you use these values. These commands do not read Secret
data:

```powershell
kubectl config current-context
$SubscriptionId = '<approved-subscription-guid>'
$TenantId = '<approved-tenant-guid>'
$ConfigResourceVersion = kubectl get configmap identity-broker-runtime-config-v3 -n agentweaver -o jsonpath='{.metadata.resourceVersion}'
$DeploymentResourceVersion = kubectl get deployment identity-broker -n agentweaver -o jsonpath='{.metadata.resourceVersion}'
```

Replace the first two values with the approved subscription and tenant GUIDs.
The next two commands save the exact current resource versions. The installer
checks the selected target, source ConfigMap, Deployment, resource versions,
and required Kubernetes permissions before it writes. Confirm that both
resource-version values are non-empty before you activate.

```powershell
node scripts\azure\deploy.mjs `
  --identity-broker-provider-config .\provider-setup.json `
  --identity-broker-provider-mode activate `
  --resource-group <target> `
  --subscription $SubscriptionId `
  --allowed-subscription $SubscriptionId `
  --tenant $TenantId `
  --allowed-tenant $TenantId `
  --expected-identity-broker-config-resource-version $ConfigResourceVersion `
  --expected-identity-broker-deployment-resource-version $DeploymentResourceVersion
```

Do not add `--execute`. Provider activation rejects it. If the effective
settings and Secret reference are already active and the Deployment is ready,
the installer returns `changed: false` without a rollout or secret rotation.
Otherwise, it creates and reads back a new immutable ConfigMap before it
applies a Deployment patch that tests the current UID and resource version.
The patch changes only provider values, the active ConfigMap reference, and
the provider fingerprint. The old immutable ConfigMap remains in place.

On success, the result reports the active map and verified Deployment
generation. On an error with code
`identity_broker_provider_activation_partial`, read the reported active map
and Deployment state before you retry. If readback shows the old map is still
active, get fresh resource versions and review the plan again before retrying.
If the new map is active but the Deployment is not ready, resolve the rollout
failure first; a retry reports the current readback instead of returning a
false no-op. Stop if readback is unavailable or does not match either state.
Do not delete the old or unreferenced immutable map as recovery.

Later, the normal Kustomize release preflight validates and preserves the
active successor map, provider environment entries, SecretKeyRef, and
fingerprint. It keeps the checked-in v3 ConfigMap drift guard and stops before
workload writes if provider state is unknown or conflicts with source. It does
not read Secret data. See the [release deployment reference](../reference/releases.md#kubernetes-workload-deployment).

See the [runtime configuration reference](../reference/runtime-configuration.md#identity-broker-settings)
for the exact input fields and the [troubleshooting guide](./troubleshooting.md)
for the unconfigured Repo App status response.

## Replace an immutable runtime ConfigMap

Most checked-in runtime ConfigMaps, including `knowledge-runtime-config`, set
`immutable` to `true`. Kubernetes rejects changes to an immutable ConfigMap's
data. Editing its JSON source and applying the bundle does not update the live
ConfigMap. A Deployment also does not restart automatically when a referenced
ConfigMap changes.

For a first installation, resolve and review all settings before applying the
bundle. For an existing installation, use this manual maintenance procedure
only when the deployment tooling does not provide a guarded replacement for
the map. These steps stop dependent workloads and briefly remove the ConfigMap;
plan for the resulting interruption. Do not remove `immutable` to bypass the
replacement process.

1. Update the reviewed JSON source and render the complete bundle. Confirm the
   rendered ConfigMap has the expected name, namespace, data, and
   `immutable: true`.
2. Record the original replica count for each workload that references the
   map. Stop every such workload and wait until its Pods have terminated.
   Kubelet caches immutable ConfigMap data for about one minute by default;
   wait at least 70 seconds after the last Pod terminates before replacing the
   map.
3. Delete only the exact ConfigMap being replaced. For example:

   ```powershell
   kubectl delete configmap knowledge-runtime-config -n agentweaver
   ```

4. Recreate it from the rendered bundle:

   ```powershell
   kubectl apply -k deploy\k8s
   ```

5. Restore each workload to its recorded replica count and wait for its
   rollout. For example, replace `<original-count>` with the count recorded
   before stopping the `knowledge` Deployment:

   ```powershell
   kubectl scale deployment/knowledge --replicas=<original-count> -n agentweaver
   kubectl rollout status deployment/knowledge -n agentweaver
   ```

The local `deploy:kustomize` helper follows this stop/wait/cache-expiry
procedure for `projects-config-runtime-config` and the AgentHost
`environment-runtime-config` pair. For an Environment map change, it records
the current replica count on `deployment/environment`, stops that Deployment,
waits for its Pods to terminate and waits 70 seconds before replacing the
exact map. It uses the live map's UID and resource version as delete
preconditions and verifies the replacement data before it resumes the
Deployment. A change to only `agenthost-runtime-v1` does not stop Environment.

The helper checks the caller's exact map and Deployment permissions before
writing. It also checks the effective Agent Sandbox permissions of the
Environment ServiceAccount. Then, as the Environment ServiceAccount, it reads
the workspace StorageClass and the sandbox RuntimeClass named in
`environment-runtime-config`. It stops before writes if:

- the StorageClass has another provisioner or other mount options;
- the StorageClass is missing and is not the product class;
- the RuntimeClass is missing; or
- no Ready, schedulable node matches the RuntimeClass node selector; or
- DaemonSet `ama-logs` in `kube-system` (the AKS Azure Monitor add-on agent)
  is missing or not ready on every scheduled node. A missing `ama-logs` is
  accepted, with a warning, only when the node list is not empty and every
  node has a `topology.kubernetes.io/region` label that ends in `euap`.

The message for a missing RuntimeClass or node tells you to run the
infrastructure deployment, which creates the `kata` node pool. The message for
`ama-logs` tells you to run the infrastructure deployment, which enables the
add-on, and to wait until the agent is ready. When the product
StorageClass is missing, the helper creates it before the migrations. The
caller needs `list nodes` at cluster scope, `get daemonsets.apps/ama-logs` in
`kube-system`, and `create storageclasses` only when the class is missing.

After all rollouts, the helper runs an install smoke check. It creates a
1 GiB workspace PersistentVolumeClaim and a short-lived pod with the AgentHost
labels, so the AgentHost network policies apply. The pod uses the sandbox
RuntimeClass and the exact AgentHost image. It has no ServiceAccount token,
Secret, or settings. It checks that:

- `environment.agentweaver.svc.cluster.local` resolves, and a TCP connection
  to Environment on port 8443 opens through the AgentHost network policies; and
- a file on the workspace volume can be written, read, and linked, and is
  owned by UID 1000; and
- each BYOK model endpoint host in the applied AgentHost runtime configuration
  answers one HTTPS `GET <BaseUrl>/models` request that has no key. HTTP 401 or
  403 passes. Another HTTP status passes with a warning. No response (a DNS,
  connection, or TLS failure, or a 30-second timeout) fails the deployment and
  names the host. The helper does not read,
  mount, or pass the API key, and it drops any query string from the BaseUrl.

If the check fails or does not finish in 10 minutes, the deployment fails and
the message names the cause. The helper always deletes the pod and claim, and
deletes earlier check resources with the label
`agentweaver.io/install-smoke-check=true` before it starts. The caller needs
`create`, `get`, `list`, and `delete` on `pods` and `persistentvolumeclaims`,
and `get pods/log`, in namespace `agentweaver`. The helper checks these
permissions before it writes. The pod does not check `kubernetes.default.svc`,
because the AgentHost network policies block the Kubernetes API by design.

The proposed rules in
`deploy/k8s/proposed/environment-agent-sandbox-rbac.yaml` are not in the active
Kustomize bundle and are not applied by the helper. If Environment replacement,
apply, or rollout fails, its recovery annotation remains; rerun the same
deployment command to resume. If the ConfigMap is absent, the helper leaves
the Deployment stopped until the retry recreates it. Do not clear the
annotation manually. Other immutable ConfigMaps, including
`knowledge-runtime-config`, still require the manual procedure above.

Kubelet caches immutable ConfigMap data per node and does not watch it for
changes. If a pod that references the ConfigMap keeps running on a node while
you delete and recreate the ConfigMap under the same name, new pods on that
node can still receive the old values. A `rollout restart` does not clear this
cache. A short interval without the map is expected while workloads are
stopped. Do not use a broad delete.

`events-and-sessions-runtime-config` is mutable in the checked-in profile.
Apply its reviewed data with the bundle, then restart the Events & Sessions
Deployment so the process reads the new environment.

## Teardown boundary

This how-to does not define or verify a teardown procedure. Do not infer from
the deployment steps that deleting a resource group, cluster, or shared
resource is safe.

## Related information

- [Kubernetes operator guide](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md)
- [Runtime configuration reference](../reference/runtime-configuration.md)
- [Azure acceptance](./azure-acceptance.md)
- [Configure Knowledge memory](./configure-knowledge.md)
- [Configure Microsoft Entra Workload ID on AKS](https://learn.microsoft.com/azure/aks/workload-identity-deploy-cluster)
