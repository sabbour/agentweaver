# Dedicated Azure environment

`infra/bicep/main.bicep` defines a dedicated v1 network and AKS cluster. It includes Entra-only PostgreSQL, Key Vault, Blob Storage, and Azure Monitor.

PostgreSQL, Key Vault, and Blob use private networking. Azure Monitor uses a private workspace path for queries and an explicit ingestion path. The definitions do not prove private DNS resolution or live access.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-foundation.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-azure-foundation.png'" alt="Structural Azure topology and evidence map. AKS has a public API endpoint with managed Entra, Azure RBAC, and local accounts disabled. The dedicated VNet has separate AKS-node, delegated PostgreSQL, and private-endpoint subnets; Key Vault and Blob private endpoints and the in-VNet AMPLS private endpoint attach to the private-endpoint subnet, while AMPLS remains global. The Foundation Probe Job exercises exact-version Key Vault secret redemption, a Blob object-store round trip, and Entra-authenticated PostgreSQL effects. Workspace and App Insights are in eastus2. Bicep grants the probe identity Key Vault Secrets User at the vault, Storage Blob Data Contributor at the storage account, Log Analytics Reader at the workspace, and Monitoring Metrics Publisher at App Insights. No live deployment or P0 completion is claimed." />
  </a>
  <figcaption>Structural resource and evidence dependencies. Bicep and Kustomize definitions are not a deployment record; the figure does not claim live readiness or P0 completion.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-foundation.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-foundation.drawio'">Open editable draw.io source</a></p>

The following sequence shows the verifier's time-ordered, read-only checks after the probe Job completes. It does not launch the Job or deploy resources.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-acceptance.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-azure-acceptance.png'" alt="After a Foundation Probe Job completes, the read-only verifier checks the source-bound registry manifest against the Job image, pod image, and pulled imageID before reading and validating the native receipt. It then queries Azure Monitor for fresh correlated evidence. Missing or mismatched evidence remains blocked." />
  </a>
  <figcaption>Ordered acceptance evidence checks, not a deployment sequence. Only complete source-, target-, image-, identity-, receipt-, and telemetry-bound evidence sets <code>deployedAcceptance</code>; this does not claim broader P0 readiness.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-acceptance.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-acceptance.drawio'">Open editable draw.io source</a></p>

## P0 region placement

The intended v1 P0 placement is:

| Resource | Region | Notes |
| --- | --- | --- |
| AKS, private VNet and subnets, PostgreSQL Flexible Server, Key Vault, Blob Storage, and Private Endpoints | `eastus2euap` | PostgreSQL is Entra-only and uses `Standard_B2s`. |
| Log Analytics workspace and Application Insights | `eastus2` | Provider metadata advertises these Monitor resources in East US 2, not East US 2 EUAP. |
| Azure Monitor Private Link Scope (AMPLS) | `global` | Monitor resources can link to the global AMPLS. |
| AMPLS Private Endpoint | `eastus2euap` | It is in the EUAP VNet/subnet; the endpoint region follows its VNet. |

The root Bicep parameters bind that placement and the Entra administrator:

| Input | Requirement |
| --- | --- |
| `location` | Set to `eastus2euap` for the VNet and core resources. The Bicep default is the resource-group location; reviewed P0 parameters must select the intended region. |
| `monitorLocation` | Required; set to `eastus2` for Log Analytics and Application Insights. |
| `postgresEntraAdminObjectId` | Required directory object ID for the PostgreSQL Entra administrator. |
| `postgresEntraAdminPrincipalName` | Required directory principal name for that administrator. |
| `postgresEntraAdminPrincipalType` | Required; `User`, `Group`, or `ServicePrincipal`. `Unknown` is rejected, and a group is not required. |

Supply real administrator values only through reviewed operator parameters; documentation and checked-in examples keep placeholders.

Provider metadata advertises AKS, including API version `2026-07-02-preview`, Key Vault, Storage, VNets, Private Endpoints, and PostgreSQL `Standard_B2s` in `eastus2euap`. East US 2 EUAP has no Retail Prices API meters, so this page makes no EUAP cost quote; standard `eastus2` rates are only a planning proxy. This region map is not deployment evidence and adds no 0.x product scope.

## AKS and Application Routing preview

The Bicep source uses `Microsoft.ContainerService/managedClusters@2026-07-02-preview`. It enables native `webAppRouting` and sets `nginx.defaultIngressControllerType` to `'None'`. This source does not select an NGINX controller or create an application route.

The template enables the native managed Gateway API installation and
Application Routing implementation. Its Gateway class is `approuting-istio`.
The class name describes Azure's internal implementation, not a service-mesh
addon or an Istio API requirement.
The separate default-off routing installer uses native managed certificates,
v1 Gateway and HTTPRoute resources, and verified backend HTTPS.
It requires explicit approved Gateway namespace and security-policy inputs.
The P0 workload namespace remains restricted.
The issuer comes from the admitted route hostname.
Public callback changes remain read-only append/no-op plans.
Only the optional ordinary-DNS and trusted-HTTPS health checks prove Broker readiness.
See [the operator interface](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#application-routing-source-setting).

When `appRoutingDnsZoneResourceIds` is empty, the template requests AKS's managed default domain. The `appRoutingDomain` output reports `managedDefaultRequested: true` and reads `domainName` from `aks.properties.ingressProfile.webAppRouting.defaultDomain.domainName`. The source does not construct a hostname. The preview API describes an autogenerated domain with a signed TLS certificate; offline validation does not prove preview registration, regional support, or HTTPS serving.

When the array contains custom zone IDs, the template disables the managed default domain. It accepts one to five unique existing public or private DNS zone IDs in the exact authorized subscription. The guard rejects malformed, cross-subscription, and PrivateLink zone IDs. Public zones can be in one resource group and private zones in another; all zones of each kind must share one resource group.

AKS supplies a separate `appRoutingIdentity` with `resourceId`, `clientId`, and `objectId`. Bicep scopes Key Vault Certificate User to the existing vault and grants each configured DNS zone only its matching public or private DNS Zone Contributor role at that exact zone. These are definitions, not live role grants. The routing identity is separate from the Foundation Probe identity.

AKS also enables the Key Vault CSI provider and secret rotation with `enableSecretRotation: 'true'` and `rotationPollInterval: '2m'`. These values are template settings; the addon has not been activated in Azure.

## Network and acceptance checks

AKS exposes a public API endpoint protected by managed Entra authentication and Azure RBAC; local accounts are disabled. The cluster keeps Azure Linux nodes, Cilium networking, OIDC/workload identity, and ACNS security with observability disabled. The source leaves `kubernetesVersion` empty. Runtime acceptance requires Kubernetes 1.29 or later.

The base NetworkPolicy denies traffic by default. The Foundation Probe overlay grants its own egress. The base does not provide telemetry exporter egress.

The Foundation Probe Job has a dedicated workload identity. It exercises Key Vault, Blob, PostgreSQL, and telemetry without redeeming through the Identity broker. A separate read-only consumer checks the completed Job and pod, verifies the exact registry image, validates the native receipt, and queries Azure Monitor after completion for matching stored telemetry. Configuration observations remain separate from runtime evidence.

These definitions and diagrams are not deployment evidence. Live role grants, addon activation, DNS zones, certificates, controllers, routes, and HTTPS serving are not verified by this page. The consumer has only been validated with local fixtures; it has not run against Azure or AKS. See [Azure acceptance](../guide/azure-acceptance) for the evidence boundary and deployment approval requirements.
