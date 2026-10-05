# Azure acceptance

The v1 Bicep and Kubernetes sources define the dedicated P0 environment. They
do not prove deployment or runtime behavior. Deployment and practical smoke
acceptance are tracked separately in [#1812](https://github.com/sabbour/agentweaver/issues/1812)
and [#1814](https://github.com/sabbour/agentweaver/issues/1814).

## Offline checks

Run these commands from the repository root:

```powershell
npm run test:azure
az bicep build --file infra\bicep\main.bicep --stdout
kubectl kustomize deploy\k8s\base
kubectl kustomize deploy\k8s\acceptance\foundation-probe
```

They validate source and local rendering only; they do not contact Azure or
prove a running service.

## Deployment

Use the guarded exact-source command in the [Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md).
It binds the approved subscription, tenant, resource group, and source
SHA/tree/input hash. A public API endpoint is required:
`apiServerAccessProfile.enablePrivateCluster` must be `false`. A publicly
resolvable FQDN on a private cluster does not satisfy this requirement.

For an existing P0 foundation, use the guarded AKS-only update path. It updates
the cluster and three existing workload-identity federation records without
redeploying PostgreSQL, Key Vault, Blob, VNet, private DNS, or Monitor. Supply
the approved operator's Entra object ID explicitly. The deployment creates or
reuses a permanent Azure Kubernetes Service RBAC Cluster Admin assignment at
the exact AKS resource scope; it does not grant broader Azure scope or remove
that assignment. Its scoped receipt is not a full-foundation receipt and
requires a separate verified full-foundation resource receipt for Probe acceptance.
The Probe image source and the native infrastructure source remain separate.
Neither receipt can substitute for the other.

The AKS API uses managed Entra authentication and Azure RBAC, with local
accounts disabled. PostgreSQL, Key Vault, Blob, VNet/private endpoints, and
Monitor retain their approved target and region placement.

## Required P0 smoke checks

Use standard Azure CLI/SDK and `kubectl` observations for a short runtime
check:

1. Read the actual AKS properties and confirm public API, managed Entra, Azure
   RBAC, and disabled local accounts.
2. Confirm normal workload rollout, service health, and deployed image/version.
3. Exercise one real authorized Identity secret-redemption request and one
   denied request. Do not log credentials or returned secret values.
4. Use one small generated fixture for exact-version Key Vault read, Blob
   round trip, and PostgreSQL behavior; confirm cleanup removes only data
   owned by that fixture.
5. Confirm relevant telemetry where the service integration requires it.

Record only concise, sanitized results and identify the actual source/image
versions. Preserve the external P0 data services and all resources outside the
approved fixture. Do not treat unrun checks as passed.

## Optional Foundation Probe evidence

The `foundation-probe` Job and `verify-acceptance.mjs --collect-runtime-evidence`
remain available as supplemental evidence collection. The verifier observes
the Job, pod, image, workload identity, native receipt, and correlated
Azure Monitor records; it does not run the Identity broker grant-redemption
smoke test. This extended collector is not an additional P0 shipping gate.
The verifier compares the image with its exact build source.
It compares native deployment receipts with their own source, input hash,
resource IDs, and deployment scope.
An AKS-only receipt never becomes a full-foundation receipt.
The corrected initial Probe `0.0.0` requires this explicit separation in its
target and native receipt. The previous image and publication evidence remain
available by digest. Only the existing tag receives an explicitly approved one-time replacement.
Legacy tag-count, inherited NRMS policy-origin, provider-generated resource
inventory, and historical receipt audits are not required acceptance steps.

See [Testing](./testing) for what source tests can and cannot prove.
