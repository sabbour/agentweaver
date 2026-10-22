# Configure providers

Use this article to identify the provider choices that the current Agentweaver
1.x catalog can resolve. A provider is an adapter for a capability such as
Memory or Storage. A provider registration makes an adapter available to a
service. A configuration value by itself does not install an adapter or make a
cloud resource available.

## Before you begin

You need access to the v1 source and the approved target's provider catalog.
Read [providers and models](../architecture/providers-models.md) for the
resolution and pinning model. Use the
[runtime configuration reference](../reference/runtime-configuration.md) for
the exact setting names and validation rules.

## Review the current provider catalog

The checked-in snapshot in
`deploy/k8s/provider-catalogs/projects-config-provider-catalog.snapshot.json`
is the source for the default Projects & Config catalog. It is not proof that
the services or providers are deployed.

| Capability | Provider ID | Current catalog behavior |
| --- | --- | --- |
| Sessions | `postgres.native-sessions` | Default. Stores the run journal and session history in PostgreSQL. |
| Messaging | `postgres.native-messaging` | Platform singleton. Stores addressed messages in PostgreSQL. Projects cannot replace it. |
| Memory | `postgres.native-memory` | Default. Stores Knowledge records in PostgreSQL. |
| Sandbox | `agent-sandbox` | Default. Requests isolated Kubernetes Sandbox workloads. |
| Storage | `azure-files-csi` | Default. Provides generation-scoped workspace volumes through the Azure Files CSI driver. |
| Policy | `agt.dotnet-yaml` | Default platform policy adapter. |
| Source control | `github` | Default GitHub repository adapter. |
| Cost | `copilot.usage-cost` | Selected for the `copilot.nano_aiu` meter source. It prices supported Copilot usage from a versioned rate card. |
| Network policy | `cilium` | Selected for the required L3/L4 layer. The adapter also advertises its supported FQDN, CIDR, and DNS capabilities. |

Projects cannot override provider selections for non-model platform
components. These selections come from the platform catalog: Cost is selected
by meter source rather than by a general project default, and Network Policy
is selected by layer. Project model configuration remains independently
selectable.

The authenticated project configuration choices endpoint exposes current
platform provider selections for display alongside model references and
supported reasoning efforts. It is not a provisioning check or a promise that
an upstream provider or resource is available. Configuration writes must send
empty provider-override arrays; any non-empty value returns
`400 project_provider_override_unsupported`. Legacy project provider overrides
remain in stored revisions for compatibility but are ignored by new
selection; they are not automatically removed or migrated. See
[Project settings](../experience/configuration.md) for the owner-backed
choices and save behavior.

## Understand source candidates

Some adapters and options exist in source without being registered in the
checked-in application catalog:

- `cosmos.memory` and `redis.memory` are optional Memory implementation
  candidates. The default catalog does not register either one.
- `agentsessions.records` is an optional, read-only Sessions provider in the
  `Agentweaver.Providers.Sessions.AgentSessions` package. It replays verified
  AgentSessions record exports. The default catalog does not register it, and
  the Events & Sessions host doesn't reference it.
- `azure.byok-usage-cost` is off by default. Events admits Azure BYOK usage only
  from trusted producers. It prices that usage when you configure
  `EventsAndSessions:Cost:Azure` with a versioned rate card, as shown in the
  following example. Without that section, the usage is recorded as `Unpriced`.

  ```json
  {
    "EventsAndSessions": {
      "Cost": {
        "Azure": {
          "ResourceId": "azure-openai-eastus2",
          "ResourceGeneration": 1,
          "OptionsRevision": "azure-cost-v1",
          "OptionsSchemaVersion": 1,
          "PricingModel": "StandardTokens",
          "RateCard": {
            "Id": "azure-retail-prices.standard.eastus2",
            "Version": "2026-10-01",
            "MeterSource": "byok.tokens",
            "Unit": "USD",
            "NanoUnitsPerUnit": 1000000,
            "ModelMultipliers": {
              "gpt-4.1|input": 2.00,
              "gpt-4.1|output": 8.00
            }
          }
        }
      }
    }
  }
  ```

  Rates are per million tokens. Use your own reviewed retail or negotiated prices.
  To change rates, publish a new `Version`. Runs that already pinned the earlier
  card keep it, and their new usage stays `Unpriced`.
- Foundation Probe registrations such as `azure-blob`, `azure-key-vault`, and
  `azure-monitor` are probe descriptors. They do not make application
  providers selectable.
- Model IDs are not provider IDs. The AgentHost model-binding map connects an
  accepted model reference to an exact SDK model ID and source mode.

Registering a source candidate requires a reviewed catalog and runtime
integration. Do not add a catalog entry unless the adapter is available to the
service and its options, capabilities, and resource negotiation are supported.

## Configure a provider

1. **Identify the owning capability.** Use the catalog table to find the
   provider ID and its owning service.
2. **Prepare the external resource.** For example, create the PostgreSQL
   database and Entra role, or install the supported CSI and Sandbox
   controllers. The provider adapter validates an existing resource; it does
   not provision that resource.
3. **Set the provider options.** Update the owning service's runtime
   ConfigMap. Keep credentials and private keys in operator-managed Kubernetes
   Secrets, not in the checked-in JSON files.
4. **Keep identity and generation stable.** The Knowledge Memory
   `ResourceId` is stored with data and must never change. For other providers,
   follow the adapter's resource-identity contract. Increase
   `ResourceGeneration` only after the resource is wiped or replaced. A normal
   restart or options change does not create a new resource generation.
5. **Validate the complete installation.** Render the root Kustomize bundle,
   then run the release preflight before applying it. See
   [deploy Agentweaver to Azure](./deploy-to-azure.md).

## Configure the Identity Broker's upstream provider

The Identity Broker supports one configured upstream OpenID Connect provider
for user sign-in. `IdentityBroker:Issuer` is the Broker's own issuer;
`IdentityBroker:ExternalProvider:Authority` identifies the upstream provider.
Do not use them interchangeably.

Set the upstream authority and registered client ID in the Identity Broker
configuration. Use `MetadataAddress` only when the provider requires a
discovery URL that cannot be derived from `Authority`. Add `ClientSecret` only
for an upstream confidential client, and keep it in the operator-managed
Secret. The code does not select an upstream provider from a catalog or
support dynamic client registration.

The Broker's seeded application clients are separate from its upstream
provider. Each client is `Public` or `Confidential`; both use authorization
code with PKCE, and only a confidential client has a client secret. Keep the
secret in `identity-broker-client-secrets`. See the
[Identity Broker settings](../reference/runtime-configuration.md#identity-broker-settings)
for every field and storage rule.

Workload identity is a different mechanism. It lets a Kubernetes service
authenticate to Azure resources such as PostgreSQL or Key Vault. It does not
sign users in and does not replace the upstream OpenID Connect provider.

## Configure model bindings

Model bindings are a separate runtime map, not provider-catalog registrations.
The `AgentHost__ModelBindingsRevision` value versions the map. Each reference
maps to an exact model ID and a supported source mode. The current checked-in
profile uses `HostedCopilot` for its hosted models.

Use the model ID returned by the supported SDK catalog. The runtime does not
guess an alias or substitute another model. If a reference is absent or the
model is unavailable, startup or run admission fails instead of silently
falling back.

The source supports these model source modes:

| Source mode | Configuration contract |
| --- | --- |
| `HostedCopilot` | Configure the exact model ID and omit `Provider`. The current runtime profile uses the hosted Copilot connection. |
| `Byok` | Configure a `Provider` with `Type` `azure`, `openai`, or `anthropic`, an HTTPS `BaseUrl`, and `WireApi` `responses` or `completions`. `AzureApiVersion` is valid only for `azure`. The provider may include headers with ASCII letters, digits, or hyphens in each header name; header values cannot contain control characters. |

BYOK requires an owner-authorized credential binding; do not put provider
secrets in the model-binding ConfigMap or headers. An optional
`PromptCapacityTokens` value must be positive. Optional
`SupportedReasoningEfforts` entries must be unique values from `low`,
`medium`, `high`, or `xhigh`. These contracts describe source support; the
checked-in deployment profile does not select BYOK.

## Configure spend budgets

Spend budgets limit priced usage that Events records. Add them to
`runLimits.spendBudgets` in the platform runtime defaults or in a project
configuration. Each budget limits one meter source and unit at one scope:

| Scope | Limits | Required fields |
| --- | --- | --- |
| `run` | The run's total. | None. |
| `agent` | One agent's total in the run. | `agentId` |
| `project` | The project's total in one period. | `period` with `periodId`, `startsAt` (inclusive), and `endsAt` (exclusive). |

The following example limits Azure BYOK spend for the project in one month and
Hosted Copilot usage for each run:

```json
{
  "runLimits": {
    "spendBudgets": [
      {
        "budgetId": "azure-monthly",
        "revision": 1,
        "scope": "project",
        "meterSource": "byok.tokens",
        "unit": "USD",
        "softLimit": 400,
        "hardLimit": 500,
        "reservePerDispatch": 2,
        "period": {
          "periodId": "2026-10",
          "startsAt": "2026-10-01T00:00:00Z",
          "endsAt": "2026-11-01T00:00:00Z"
        }
      },
      {
        "budgetId": "copilot-per-run",
        "revision": 1,
        "scope": "run",
        "meterSource": "copilot.nano_aiu",
        "unit": "AIC",
        "hardLimit": 50,
        "reservePerDispatch": 1
      }
    ]
  }
}
```

- `softLimit` records one notification per budget and period when committed
  spend reaches it. It never blocks a dispatch.
- `hardLimit` blocks a new dispatch before send when committed spend, the
  reserves of dispatches that Events hasn't accounted for yet, and the new
  reserve would exceed it.
- `reservePerDispatch` is the amount that each dispatch holds until Events
  accounts for it. Set it to at least the expected cost of one turn.

A project can narrow a platform budget by using the same `budgetId` with lower
or equal limits and the same bucket and period. It can't remove or widen a
platform budget. A hard budget blocks dispatch when its bucket has unpriced
usage or when Orchestrator can't read the spend evidence. Configure a rate card
for the meter source before you set a hard limit, for example the Azure rate
card in [Understand source candidates](#understand-source-candidates).

For the enforcement design and error codes, see
[Scoped spend budgets and dispatch exposure](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md#scoped-spend-budgets-and-dispatch-exposure).

## Expected result

The owning service starts only when its selected provider can validate the
configured resource and negotiate the required capabilities. It pins the
provider identity and generation for the relevant run. Provider resolution
does not prove resource provisioning, deployment, or network enforcement.

## Related information

- [Configure Knowledge memory](./configure-knowledge.md)
- [Runtime configuration reference](../reference/runtime-configuration.md)
- [Providers and models](../architecture/providers-models.md)
