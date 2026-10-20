# Validate the Azure environment

Use this article to distinguish local source checks from checks against an
approved Azure deployment. Bicep compilation and Kubernetes rendering validate
the checked-in configuration; neither proves that Azure resources or services
are deployed. Deployment and smoke acceptance are tracked separately in
[issue #1812](https://github.com/sabbour/agentweaver/issues/1812) and
[issue #1814](https://github.com/sabbour/agentweaver/issues/1814).

## Prerequisites

Before you run a live check, obtain approval for the target subscription,
tenant, source revision, and any resource changes. Install Azure CLI, Bicep,
`kubectl`, and Node.js 24. Use the guarded
exact-source procedure in the
[Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md);
do not replace its receipt and target checks with an ad hoc deployment command.
The separately approved
[session-material container repair](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#session-material-container-repair)
is limited to that container and its Events identity grant; it does not replace
the guarded foundation workflow or establish acceptance evidence.

The examples in this article use the requested environment identifiers.
`agentweaver-aks` is the Azure Kubernetes Service (AKS) cluster name:

| Resource | Example identifier |
| --- | --- |
| Resource group | `agentweaver` |
| AKS cluster | `agentweaver-aks` |
| Product namespace | `agentweaver` |
| Gateway namespace | `agentweaver-gateway` |
| Knowledge Memory resource ID | `agentweaver.knowledge` |
| Public portal host | `portal.<managed-domain>` |

These names identify the intended target for the examples. Confirm the
subscription and source-bound deployment receipt before using them.

## Run the offline source checks

Run these commands from the repository root:

1. Test the Azure deployment tooling:

   ```powershell
   npm run test:azure
   ```

2. Compile the Bicep entry point:

   ```powershell
   az bicep build --file infra\bicep\main.bicep --stdout
   ```

3. Render the product and acceptance Kubernetes configurations:

   ```powershell
   kubectl kustomize deploy\k8s\base
   kubectl kustomize deploy\k8s\migrations\identity-broker
   kubectl kustomize deploy\k8s\acceptance\foundation-probe
   ```

The commands must complete without validation errors. They do not contact
Azure, apply SQL, create identities, execute an operator bootstrap, or prove a
running service. Confirm the migration egress policy renders the approved
PostgreSQL FQDN and TCP 5432 rule without a `CHANGEME` host.

The Identity runtime-table compatibility tests use disposable PostgreSQL
principals. They check the source-defined grants for compare-and-swap updates
and append-only audit writes. They do not create Azure identities or apply
those grants to a deployed database. Passing these tests is not Foundation
Probe evidence or live SDK accounting acceptance.

## Deploy or update the approved target

Use the guarded exact-source command from the operator guide. The installer
binds the approved subscription, tenant, resource group, source SHA, source
tree, and input hash. The AKS API must be public:
`apiServerAccessProfile.enablePrivateCluster` must be `false`. A public DNS
name does not make a private AKS API public.

For an existing environment, use the guarded AKS-only path to refresh the three
existing workload-identity federation records. It reads the cluster issuer and
managed routing-domain state but does not PUT AKS or change its configuration.
It also does not redeploy PostgreSQL, Key Vault, Blob Storage, the VNet, private
DNS, or Monitor. Supply the approved infrastructure inputs only; the deployment
does not take an operator object ID for Kubernetes access.

The tooling never creates, requires, or verifies a human Azure Kubernetes
Service RBAC Cluster Admin assignment. Provision cluster connectivity and
Kubernetes API permissions outside the tooling. The cluster-dependent bootstrap
steps check the access they need and stop before writing resources when it is
missing. An AKS-only receipt is scoped to the federation refresh; it does not replace the
verified full-foundation resource receipt required for Foundation Probe
acceptance. The Probe image and infrastructure deployment have separate source
receipts.

The full-foundation template configures the system node pool to scale
automatically between two and three `Standard_D2s_v5` nodes, starting with two.
The AKS-only installer validates these bounds against the approved foundation
parameters but does not read or configure the pool. Its receipt has no submitted
node count and does not prove that the pool scaled. Available vCPU quota also
does not prove that the region has VM capacity.

The AKS API uses managed Microsoft Entra ID authentication and Azure RBAC,
with local accounts disabled. The deployment keeps PostgreSQL, Key Vault,
Blob Storage, VNet/private endpoints, and Monitor in their approved target
and region.

The foundation creates the private `platform-artifacts` and `session-material`
Blob containers. The Foundation Probe keeps its account-scoped Blob data role;
Events & Sessions receives Storage Blob Data Contributor on
`session-material` only. These definitions do not confirm live resource or
role-assignment state.

## Check the deployed environment

Run live checks only after confirming the current Azure subscription and
Kubernetes context:

1. Confirm the Azure subscription and cluster context. Read the actual AKS
   properties and verify that the API is public, managed Entra and Azure RBAC
   are enabled, and local accounts are disabled.

   ```powershell
   az account show --query "{subscription:name, tenant:tenantId}" --output table
   kubectl config current-context
   az aks show --resource-group agentweaver --name agentweaver-aks
   ```

   Expected result: the active subscription and Kubernetes context identify
   the approved target. The AKS properties show a public API, managed Entra
   authentication, Azure RBAC, and disabled local accounts.
2. Check workload rollout and health in both namespaces:

   ```powershell
   kubectl get deployments,services,pods -n agentweaver
   kubectl get deployments,services,pods -n agentweaver-gateway
   ```

   Confirm that the expected workloads are ready and that their image
   references match the approved deployment receipt. These observations
   confirm only the resources you queried.
   For public routing, verify that the managed Gateway has one programmed
   wildcard-domain HTTPS listener and that the Broker, Web, Gateway API, and
   MCP HTTPRoutes retain their exact hostnames, paths, and backends on it. A
   Portal root response alone does not prove that the Broker authorization
   route works.
3. Exercise one authorized Identity secret-redemption request and one denied
   request. Confirm that the authorized request follows the owner's success
   contract and the denied request is rejected. Do not log credentials or
   returned secret values.
4. Use a small generated fixture to test an exact-version Key Vault read, a
   Blob Storage round trip, and PostgreSQL behavior. Confirm that cleanup
   removes only data created by that fixture, and compare each response with
   the fixture's expected value.
5. Confirm the relevant telemetry when the deployed service integration
   requires it.

Record concise, sanitized results and the observed source and image versions.
Preserve the external data services and all resources outside the approved
fixture. Mark checks that you did not run as not run; do not infer a pass from
source tests or rendered manifests.

## Collect optional Foundation Probe evidence

The `foundation-probe` Job and
`verify-acceptance.mjs --collect-runtime-evidence` command provide supplemental
evidence. The verifier observes the Job, pod, image, workload identity,
native deployment receipt, and correlated Azure Monitor records. It does not
perform the Identity Broker grant-redemption smoke check and is not a separate
shipping gate.

The verifier compares the Probe image with its exact build source. It also
compares each native deployment receipt with its source, input hash, resource
IDs, and deployment scope. An AKS-only receipt cannot stand in for the
full-foundation receipt, and neither image evidence nor infrastructure
evidence replaces the other. The deployment preflight does not enumerate Azure resources or verify their
post-deployment properties.

See [Test Agentweaver](./testing) for the limits of source and integration
tests.

## Expected result

You have recorded each check as passed, failed, or not run against the
approved source and deployment receipt. Source checks and Foundation Probe
evidence remain distinct from live user-workflow acceptance.
