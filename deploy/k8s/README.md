# Deploy Agentweaver to Kubernetes

This directory contains the v1 Kustomize source for Agentweaver workloads.
The root bundle defines nine service Deployments and Services, ServiceAccounts,
runtime ConfigMaps, the provider catalog, namespace controls, network policies,
and the Portal Web HTTPRoute. The route references a `portal` listener on a
separately managed Gateway; the bundle does not create that Gateway, listener,
certificate, or public DNS.

The root also includes an AgentHost runtime ConfigMap. It is incomplete, and
Environment's optional AgentHost launch profile remains unset until the exact
Sandbox configure endpoint, queue limit, and operator-owned TLS Secret name
are approved. The Identity Broker migration Job and acceptance Foundation
Probe are separate from the product bundle. Applying Kubernetes resources does
not prove that Azure is provisioned, database roles and grants exist, a
provider is available, or a workload is ready.

For the end-to-end deployment procedure, see
[Deploy Agentweaver to Azure](../../docs/guide/deploy-to-azure.md). Use the
[runtime configuration reference](../../docs/reference/runtime-configuration.md)
for ConfigMap keys, defaults, value sources, and provider validation rules.

## Before you apply the bundle

You need an approved Azure target, a Kubernetes context for its AKS cluster,
reviewed image digests, and permission to apply the requested resources. Use
these example names in reviewed inputs:

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

These are examples, not target-discovery logic. Use the approved deployment
inputs for the actual environment. Treat the assigned managed domain and
managed identity client IDs as dynamic values: read them from the deployed
resources and do not guess them.

Key Vault is the Identity Broker's secrets backend. Its name is set by the
reviewed Bicep `keyVaultName` parameter, and its URI and exact host are runtime
deployment configuration. Use the same configured vault for
`IdentityBroker__SecretRedemption__VaultUri` and
`IdentityBroker__SecretRedemption__VaultHost`.

Before applying:

1. Complete every workload-required runtime ConfigMap value.
2. Create the operator-owned Secrets and backend CA ConfigMap referenced by
   the workloads. AKS creates `identity-broker-tls` from the managed
   `DefaultDomainCertificate` during the guarded certificate-only routing
   stage.
3. Confirm the database, Entra roles, completed Broker migration, and runtime
   grants.
4. Run the guarded certificate-only routing stage before the product release
   bootstrap. It does not require the product Services.
5. Run the source-bound release preflight and apply for the exact target and
   images. Configure full Gateway routing after the product Services exist.

The ordinary `kubectl apply` command does not validate image provenance,
database readiness, identity federation, TLS trust, or external permissions.

## Kustomize layout

| Path | Purpose |
| --- | --- |
| `base/` | Product namespace controls, ServiceAccounts, Deployments, Services, and network policies. |
| `runtime-configs/` | Non-secret ConfigMap snapshots for service hosts and Environment settings. |
| `provider-catalogs/` | Source-bound Projects & Config provider catalog snapshot and ConfigMap manifest. |
| `profiles/` | Optional runtime profiles, including the incomplete AgentHost profile. |
| `migrations/identity-broker/` | Separate Identity Broker migration ServiceAccount and one-shot Job. |
| `routing/identity-broker/` | Portal Web HTTPRoute and Cilium ingress policies included by the root bundle. |
| `acceptance/foundation-probe/` | Supplemental acceptance probe resources. |
| `kustomization.yaml` | Root product bundle. |

The root bundle includes the Portal Web HTTPRoute, but not the managed Gateway,
its `portal` listener, public DNS, or its certificates. It also does not create
or populate PostgreSQL databases, apply migrations, or create operator-owned
Secrets. The separate guarded routing procedure installs and verifies the
managed Gateway and its routes. The migration Job and acceptance probe remain
separate overlays.

## Render and apply the bundle

1. Confirm the active cluster context and render the product bundle:

   ```powershell
   kubectl config current-context
   kubectl kustomize deploy\k8s
   kubectl apply --dry-run=client -k deploy\k8s
   ```

   Rendering and client-side validation do not connect to Azure or prove that
   external prerequisites are ready.

2. Render the separate migration and acceptance overlays when they are in
   scope:

   ```powershell
   kubectl kustomize deploy\k8s\migrations\identity-broker
   kubectl kustomize deploy\k8s\acceptance\foundation-probe
   ```

   The `routing\identity-broker` directory supplies selected resources to the
   root Kustomization; it is not a standalone Kustomize overlay.

3. On a fresh install, run the default-off certificate-only phase of
   `--bootstrap-identity-routing` before `release:deploy-bootstrap --execute`.
   It applies the two managed `DefaultDomainCertificate` resources and waits
   for AKS to create `identity-broker-tls`. It does not apply Gateway, routes,
   backend policies, or ingress policies, and it does not read the MCP Service.
   The release preflight accepts either this certificate-only state or, after
   a root-bundle apply has started, exactly the bundle-owned
   `web-application` route with no Gateway or BackendTLSPolicy. That route must
   target Gateway `identity-broker` in `agentweaver-gateway` and select its
   `portal` listener; an explicit parent port must be 443. It must preserve the
   rendered host, path, and Web backend. The preflight inventories all routes;
   extra or mismatched routing resources fail closed.

4. Apply the Identity Broker migration Job after its migration identity,
   connection ConfigMap, and approved immutable Broker image are ready, and
   before the release bootstrap. Wait for the Job to complete and apply the
   required runtime grants. Broker startup checks the schema; it does not run
   migrations.

   ```powershell
   kubectl apply -k deploy\k8s\migrations\identity-broker
   kubectl wait --for=condition=complete job/identity-broker-migration `
     --namespace agentweaver --timeout=10m
   ```

5. Run `release:deploy-bootstrap --execute` with the exact published receipt,
   target, and provider-catalog inputs. It performs the guarded root-bundle
   apply and verifies live workloads. If an earlier attempt applied the bundle
   but stopped before routing setup, rerun the same guarded command; it accepts
   only the exact Web-route-only intermediate state described above. Do not
   replace this command with a direct `kubectl apply -k deploy\k8s`.
   For v1.0.3, use the normal source-bound receipt artifact and source described
   in the [release operations guide](../../releases/README.md#v103-pre-acceptance-bootstrap);
   the checked-in image digests and AgentHost/Environment pull-size values are
   pinned to that publication.

6. After the release bootstrap creates the product Services, run the full
   default-off `--bootstrap-identity-routing` procedure without the
   certificate-only option. Before any route write, it requires the exact
   private HTTPS MCP Service. It then installs and verifies the managed Gateway,
   certificates, HTTPRoutes, backend TLS policies, and narrow ingress policies.
   The full installer is in the
   [Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#application-routing-source-setting).
   Use the approved managed hostname and route-derived issuer. This operation
   does not create service TLS certificates or change the Broker application.
   An optional callback append requires a separate explicit flag and the exact
   route-derived URI.

Use the guarded
[release deployment procedures](../../releases/README.md#local-v1-release-deployment)
for source-bound, receipt-pinned image rollouts. `release:deploy-bootstrap` is
for its explicitly admitted pre-acceptance path; `release:deploy` is the
separate release-acceptance path. Both require explicit `--execute` for
cluster writes.

## Verify workloads

1. Check the current context and expected product resources:

   ```powershell
   kubectl config current-context
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

3. Check readiness, the deployed image digests, and each workload's exact
   ServiceAccount. If public routing is enabled, verify the Gateway and route
   conditions:

   ```powershell
   kubectl get gateways,httproutes -A
   ```

   Confirm the Gateway is programmed and each required route is accepted with
   resolved backend references. A routing receipt alone does not prove
   end-to-end HTTPS readiness.

4. Run the approved HTTPS health checks and authorized/denied request checks
   in [Azure acceptance](../../docs/guide/azure-acceptance.md). A ready pod
   proves only that its readiness check passed; it does not prove every
   user-facing workflow or provider effect.

## Expected result

The reviewed product workloads are ready in namespace `agentweaver`. The
Portal Web HTTPRoute references the approved managed Gateway listener, and
public routes are programmed when the separate routing procedure is enabled.
A rendered manifest, healthy pod, routing receipt, or `202 Accepted` response
does not prove a complete user workflow, a successful asynchronous effect,
or release acceptance.

## Configure a service

Runtime ConfigMaps contain non-secret values. Most are immutable, so
Kubernetes rejects changes to their data. For a first installation, apply
reviewed values before deploying workloads. For an existing installation,
follow the
[immutable ConfigMap update procedure](../../docs/reference/runtime-configuration.md#update-an-immutable-configmap).
Restart only the affected Deployment after its ConfigMap has been recreated.

Use the
[runtime configuration reference](../../docs/reference/runtime-configuration.md)
for every setting's default, constraints, and value source. For Knowledge,
preserve the stable resource label `agentweaver.knowledge` and increase
`ResourceGeneration` only after wiping or replacing the store.

## Related information

- [End-to-end Azure deployment](../../docs/guide/deploy-to-azure.md)
- [Configure providers](../../docs/guide/configure-providers.md)
- [Configure Knowledge memory](../../docs/guide/configure-knowledge.md)
- [Troubleshoot Agentweaver](../../docs/guide/troubleshooting.md)
