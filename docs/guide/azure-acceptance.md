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
Legacy tag-count, inherited NRMS policy-origin, provider-generated resource
inventory, and historical receipt audits are not required acceptance steps.

See [Testing](./testing) for what source tests can and cannot prove.
