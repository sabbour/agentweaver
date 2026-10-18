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

Use the guarded installer for an approved initial deployment into an empty
managed-resource target. The installer accepts only tracked, reviewed
parameter files under `infra/bicep/parameters/`. Do not put passwords, client
secrets, tokens, or private keys in a parameter file.

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
command. It provisions the Azure foundation. The installer checks the allowed
target, exact source, resource ownership, and Bicep inputs. The
full-foundation template is limited to an empty target. Use the tracked
AKS-only template and its matching reviewed parameter file for the later
PostgreSQL bootstrap; do not rerun the full-foundation template.

The infrastructure installer does not create Kubernetes namespaces or grant
human AKS permissions. It also does not apply product manifests, create the
Broker's application state, run database migrations, install Gateway routes,
or create operator-owned Secrets.

Azure Network Manager generates the NSG name. For a populated target, the
guard accepts it only when the exact `aks`, `postgres`, and `private-endpoints`
subnets reference the same NSG, it has no network-interface associations, and
its eight reviewed inbound rules match.

The reviewed parameter file supplies the Key Vault name through `keyVaultName`.
Key Vault is the Identity Broker's secrets backend; its name and
`IdentityBroker__SecretRedemption__VaultUri` are deployment configuration.
The installer derives the vault resource ID and exact egress host from that
parameter. Set the runtime URI to the URI returned for that same vault.

## 4. Connect to AKS and prepare the namespace

The AKS template enables Microsoft Entra authentication with Azure RBAC and
disables local accounts. Before you run Kubernetes commands, an administrator
must assign your identity the `Azure Kubernetes Service Cluster User Role` so
you can retrieve credentials. Your identity also needs an Azure Kubernetes
Service RBAC assignment that permits namespace creation and the approved
Kubernetes operations, including the temporary resources used by the
PostgreSQL bootstrap. The installer does not grant these permissions. See
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
Create the required service databases and matching Entra roles, apply each
reviewed migration and schema grant, and confirm that every required owner
address and audience is approved. The Broker runtime identity does not own
the schema. If the separate identity and database setup is not approved or
complete, stop before applying the product workloads.

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
2. Provide the Identity Broker, Gateway, and MCP TLS Secrets and the public
   backend CA ConfigMap. Each certificate must match the service DNS name
   required by the manifests.
3. Resolve all required runtime ConfigMap values before applying the product
   bundle. For an existing installation, use the
   [immutable ConfigMap update procedure](../reference/runtime-configuration.md#update-an-immutable-configmap).
4. Set the approved public Web origin and the reviewed Gateway namespace and
   security policy. The product namespace stays `restricted`. The current AKS
   managed Gateway pods cannot set the seccomp profile required by that
   policy, so routing acceptance needs a separately approved Gateway namespace
   using the `baseline` policy. Do not change the product namespace policy to
   work around this limitation.
5. Pin product images to the digests in the reviewed publication receipt.

Do not copy acceptance-only OAuth client IDs or loopback redirect URIs into a
production registration. Keep passwords, OAuth secrets, PFX passwords, tokens,
and private keys out of ConfigMaps and command-line arguments.

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

After the required configuration and operator-owned resources pass the
release preflight, apply the complete product bundle:

```powershell
kubectl apply -k deploy\k8s
```

The root Kustomization includes the Portal Web HTTPRoute and Cilium ingress
policies. The Web route depends on the `portal` listener in the separately
managed Gateway; the root bundle does not create that Gateway, listener,
certificate, or public DNS. The root also does not apply the Identity Broker
migration Job or acceptance Foundation Probe. Apply the reviewed migration
overlay separately after its database identity and connection are ready:

```powershell
kubectl kustomize deploy\k8s\migrations\identity-broker
kubectl apply -k deploy\k8s\migrations\identity-broker
```

If public Portal, Gateway API, or MCP routes are in scope, configure them only
after the product Services exist. Use the separate, default-off
`--bootstrap-identity-routing` operation in the
[Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#application-routing-source-setting).
It applies the reviewed AKS-managed Gateway, certificates, HTTPRoutes, backend
TLS policies, and narrow ingress policies, then verifies their current
conditions. The installer does not guess `<managed-domain>`, change the Broker
application, or create service TLS certificates. An optional callback append
requires a separate explicit flag and the exact route-derived URI.

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
