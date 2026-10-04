# Azure acceptance

The v1 Bicep and Kubernetes files define a dedicated Azure environment. The files have not been applied to a subscription.

The local checks compile source and test guardrails. They do not prove Azure access, role assignments, token exchange, private networking, or service deployment.

## Offline checks

Run these commands from the repository root:

```powershell
npm run test:azure
az bicep build --file infra\bicep\main.bicep --stdout
kubectl kustomize deploy\k8s\base
kubectl kustomize deploy\k8s\acceptance\foundation-probe
```

These commands do not require Azure credentials or a cluster connection.

## Deployment boundary

Deployment requires separate approval for the dedicated target, subscription, source commit, and cost. Allow-list values and `--execute` are technical guards, not approval.

The source tree must match the admitted `origin/v1` commit. The reviewed parameters must bind the tenant, owner, cost center, and resource prefix.

`npm run azure:deploy` does not exist. The supported deployment command is `node scripts/azure/deploy.mjs`. Without `--execute`, it does not call Azure. With `--execute`, it runs a guarded live what-if before deployment.

Use the exact argument contract in the [Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md). The checked-in parameter examples contain placeholders and are not deploy-ready.

## Foundation Probe

The acceptance-only Job checks the source receipt, projected workload identity, exact Key Vault version, owned Blob object, PostgreSQL transaction, and Azure Monitor trace.

The probe uses its own workload identity and egress overlay. It does not test Identity broker OAuth or broker grant redemption.

The test suite uses fakes and disposable local PostgreSQL. No Azure account or cloud fixture is part of the local test.

The operator CLI blocks acceptance when deployment evidence is missing. Read [Testing](./testing) for the current evidence boundary.
