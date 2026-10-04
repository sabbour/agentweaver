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

The acceptance-only Job exercises the projected workload identity, exact Key Vault version, owned Blob object, PostgreSQL transaction, and telemetry export. The Job's receipt records resource-operation results; it does not attest to the pod UID, pulled image, or process exit.

The separate read-only consumer is enabled explicitly with `--collect-runtime-evidence` on `scripts/azure/verify-acceptance.mjs`. It observes the completed Job and its owned pod, verifies the expected registry manifest against the Job image and pod image ID, checks the exact target and identity projection, and validates the native receipt. Only after completion does it query Azure Monitor for fresh, matching `AppDependencies` or `AppRequests` rows correlated to the source SHA, Git tree, nonce, trace, and span. Configuration observations remain separate from runtime proof, and missing or mismatched evidence leaves acceptance blocked.

The probe uses its own workload identity and egress overlay. It does not test Identity broker OAuth or broker grant redemption.

The consumer tests use fake transports and generated local fixtures. They do not read Azure, Kubernetes, or a registry, and they do not deploy or establish live acceptance.

The operator CLI blocks acceptance when deployment evidence is missing. Read [Testing](./testing) for the current evidence boundary.
