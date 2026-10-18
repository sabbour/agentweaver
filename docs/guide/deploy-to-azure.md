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
- Azure CLI, the Bicep extension, `kubectl`, Node.js 24, npm, and the .NET 10
  SDK.
- A clean checkout at the exact v1 source commit approved for deployment.
- A locally managed Bicep parameter file and the required operator-owned
  Secrets, TLS certificates, and Gateway values.

The example resource names in this tutorial are:

| Resource | Example name |
| --- | --- |
| Resource group | `agentweaver` |
| AKS cluster | `agentweaver-aks` |
| PostgreSQL server | `agentweaver-pg` |
| Key Vault | `agentweaver-kv` |
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
managed-resource target. Keep the parameter file outside source control when
it contains target-specific values.

```powershell
node scripts\azure\deploy.mjs `
  --resource-group agentweaver `
  --parameters infra\bicep\parameters\<approved-parameters>.json `
  --subscription <subscription-id> `
  --allowed-subscription <subscription-id> `
  --tenant <tenant-id> `
  --allowed-tenant <tenant-id> `
  --operator-object-id <operator-user-object-guid>
```

Without `--execute`, the command performs its offline summary or read-only
what-if path. After the target and effects are approved, add `--execute` to
request the deployment. The installer still checks the allowed target, exact
source, resource ownership, and Bicep inputs. Do not use the initial deployment
template for an existing foundation; use the dedicated AKS-only template and
parameter file when the reviewed operation is limited to AKS.

The deployment helper uses the signed-in user's Entra access and bootstraps
only the `agentweaver` namespace. It does not apply the product manifests,
create the Broker's application state, run database migrations, install
Gateway routes, or create operator-owned Secrets.

## 4. Bootstrap PostgreSQL and workload identities

Create the runtime and migration PostgreSQL roles through the guarded
`--bootstrap-identity-postgres` operation. It requires `--execute` and the
separate default-off bootstrap option. Use the same exact resource-group,
parameter, subscription, tenant, and operator arguments as the approved
deployment command:

```powershell
node scripts\azure\deploy.mjs `
  --resource-group agentweaver `
  --parameters infra\bicep\parameters\<approved-parameters>.json `
  --subscription <subscription-id> `
  --allowed-subscription <subscription-id> `
  --tenant <tenant-id> `
  --allowed-tenant <tenant-id> `
  --operator-object-id <operator-user-object-guid> `
  --bootstrap-identity-postgres `
  --execute
```

The PostgreSQL bootstrap creates only the approved databases, Entra roles, and
schema prerequisites. It does not run migrations or grant runtime schema
permissions. Apply each service's reviewed migration and grants separately.
Identity Broker uses a dedicated migration identity; its runtime identity does
not own the schema.

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

## 5. Prepare operator-owned resources

Before applying the product bundle, create or verify the resources that the
Kustomize base intentionally does not own:

1. Create the approved Identity Broker signing Secret and durable data
   protection key-ring volume.
2. Provide the Identity Broker, Gateway, and MCP TLS Secrets and the public
   backend CA ConfigMap. Each certificate must match the service DNS name
   required by the manifests.
3. Resolve all required runtime ConfigMap values. Do not apply a
   workload-required ConfigMap while it is marked incomplete.
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

## 6. Render and install the Kustomize bundle

Render the complete bundle and inspect it before applying:

```powershell
kubectl kustomize deploy\k8s
kubectl apply --dry-run=client -k deploy\k8s
```

The first command renders the product Deployments, Services, ServiceAccounts,
runtime ConfigMaps, provider catalog, namespace controls, and network policies.
The second command performs client-side validation only.

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

## 7. Verify the installation

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
