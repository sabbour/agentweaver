# Configure an Azure OpenAI BYOK model

Use this procedure to let a project run on your own Azure OpenAI deployment
(bring your own key, BYOK). The `agentweaver models byok` commands change the
checked-in runtime maps for AgentHost, Events & Sessions, and Projects &
Config in one step. They also make the project settings that select the model
and set a USD spend limit for each run.

This procedure is for a platform operator who can change the deployment
source and deploy it. The commands do not change a cluster, Key Vault, or
Azure resource. They never read or write an API key.

## Before you begin

Get these items first:

- An Azure OpenAI deployment. Record its name, region, and endpoint.
- The wire API that the deployment and Agentweaver both support:
  `responses` or `completions`. Test it with a small request before you start.
- The reasoning efforts that the deployment accepts. Agentweaver runs only
  `low`, `medium`, `high`, and `xhigh`. Do not list other values.
- The API key, stored as a Key Vault secret in the vault that the Identity
  Broker reads (`IdentityBroker:SecretRedemption:VaultUri`). Record the secret
  name and its exact 32-character version. The secret value is the raw key.
  Do not use a Kubernetes Secret for this key.
- The approved USD hard limit for one run, and the USD amount to reserve for
  each model turn.
- A clone of the Agentweaver source and the `agentweaver` CLI. See
  [Agentweaver CLI](./cli.md).

The examples use PowerShell from the repository root.

## Select the endpoint form

Azure OpenAI has two endpoint forms. Both forms support both wire APIs.

| Endpoint form | `providerType` | `endpoint` | `azureApiVersion` |
| --- | --- | --- | --- |
| v1 API | `openai` | `https://<resource>.openai.azure.com/openai/v1` | Do not set. |
| Versioned API | `azure` | `https://<resource>.openai.azure.com/` | Required, for example `2025-04-01-preview`. |

The host must end in `.openai.azure.com`, `.cognitiveservices.azure.com`, or
`.services.ai.azure.com`. The endpoint must use HTTPS on port 443. It cannot
contain user information, a query, or a fragment.

## Find the token prices

Events & Sessions uses a rate card to change BYOK token usage into USD. A
hard USD budget stops a run when usage is not priced, so you must supply
prices.

1. Find the Azure Retail Prices meters for the deployment model and region:

   ```powershell
   agentweaver models byok prices --region <region> --search <model-version>
   ```

   For example, `--search 5.4` lists the meters whose name contains `5.4`.
   The command reads the public
   [Azure Retail Prices API](https://learn.microsoft.com/rest/api/cost-management/retail-prices/azure-retail-prices).
   It shows each meter ID, meter name, product, SKU, unit, and USD price for
   one million tokens.

2. Record the meter IDs for input, cached input, and output tokens. Use the
   meters for your deployment type, for example Global Standard.

If you have negotiated prices, or the meters are not listed, use manual
prices instead. You then supply the prices, a source URL, and the date that
you read them.

## Write the input file

Create `byok.json` outside the repository. It contains no secrets.

```json
{
  "reference": "azure-gpt",
  "deployment": "<deployment-name>",
  "providerType": "openai",
  "endpoint": "https://<resource>.openai.azure.com/openai/v1",
  "wireApi": "<responses-or-completions>",
  "reasoningEfforts": ["low", "medium", "high"],
  "defaultReasoningEffort": "medium",
  "revision": "<new-model-bindings-revision>",
  "credentialReference": {
    "id": "<key-vault-secret-name>",
    "version": "<32-character-secret-version>"
  },
  "pricing": {
    "source": "azure-retail-prices",
    "region": "<region>",
    "meterIds": {
      "input": "<input-meter-id>",
      "cachedInput": "<cached-input-meter-id>",
      "output": "<output-meter-id>"
    }
  },
  "spendBudget": {
    "budgetId": "byok-per-run-usd",
    "hardLimitUsd": 5,
    "reservePerDispatchUsd": 0.5
  }
}
```

| Field | Value |
| --- | --- |
| `reference` | The model reference that projects select. If the reference exists, it must already be a BYOK binding. |
| `deployment` | The Azure OpenAI deployment name. Agentweaver sends it as the model ID. |
| `providerType`, `endpoint`, `azureApiVersion` | See [Select the endpoint form](#select-the-endpoint-form). |
| `wireApi` | Required: `responses` or `completions`. |
| `reasoningEfforts` | The verified efforts, from `low`, `medium`, `high`, and `xhigh`. |
| `defaultReasoningEffort` | The effort for the project. It must be in `reasoningEfforts`. |
| `promptCapacityTokens` | Optional. The positive prompt token limit of the deployment. |
| `revision` | A new model-binding revision. It must differ from the current revision. |
| `credentialReference` | The Key Vault secret name and exact version. |
| `pricing` | Retail meters, as shown, or manual prices. |
| `spendBudget` | The USD hard limit for one run and the reserve for each turn. |

For the versioned API form, set `providerType` to `azure`, set `endpoint` to
the resource root, and add `azureApiVersion`.

For manual prices, use this `pricing` object:

```json
{
  "source": "manual",
  "inputUsdPer1M": 2.5,
  "cachedInputUsdPer1M": 0.25,
  "outputUsdPer1M": 15,
  "sourceUrl": "https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/",
  "retrievedOn": "<yyyy-MM-dd>"
}
```

`cachedInput` and `cachedInputUsdPer1M` are optional, but set them. Without a
cached-input price, usage that includes cached input tokens is not priced, and
a hard budget then stops new turns in the run.

The command rejects unknown fields. Do not add an API key or any other secret
to the file.

## Make and apply the plan

1. Make a plan:

   ```powershell
   agentweaver models byok plan --input byok.json --source deploy\k8s --output byok-plan.json
   ```

   The command reads the three runtime maps below `deploy\k8s` and prints a
   summary. It writes the full plan to a new file. If you use Retail meters,
   it reads each price from the Azure Retail Prices API. It requires exactly
   one USD consumption price for each meter.

2. Review the summary and the plan file. The plan contains:
   - The new binding and revision.
   - The rate card ID, version, and prices.
   - The changed files, with a SHA-256 digest of each file before and after
     the change.
   - `projectConfiguration`: the model selection, the model binding pin, and
     the spend budget for the project.

3. Apply the plan:

   ```powershell
   agentweaver models byok apply --plan byok-plan.json
   ```

   The command checks every digest before it writes. If a file changed after
   you made the plan, the command stops and writes nothing. Make a new plan.

The command changes these files:

| File | Change |
| --- | --- |
| `deploy\k8s\profiles\agenthost\runtime-config.example.yaml` | Adds the binding and the new revision to the AgentHost map. |
| `deploy\k8s\runtime-configs\events-and-sessions-runtime-config.json` | Copies the binding, and sets the Azure rate card and the `byok.tokens` USD meter. |
| `deploy\k8s\runtime-configs\projects-config-runtime-config.json` | Copies the source mode, enabled state, and efforts, so that projects can select the model. |

AgentHost and Events & Sessions must load the same binding map. The plan
checks that both maps give the same configuration hash as the pin.

## Deploy the change

1. Commit the changed files through your normal review process.
2. Deploy them. See [Deploy Agentweaver to Azure](./deploy-to-azure.md). For
   example:

   ```powershell
   npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --execute
   ```

3. Restart Events & Sessions, so that it reads the new binding map and rate
   card, and wait for the rollout to finish:

   ```powershell
   kubectl rollout restart deployment/events-and-sessions -n agentweaver
   kubectl rollout status deployment/events-and-sessions -n agentweaver
   ```

The deploy helper stops if the rendered AgentHost map would remove a live
BYOK binding, or change it without a new model-binding revision. Add or change
the binding in the source with this procedure before you deploy.

The deploy helper also lets AgentHost reach the BYOK endpoints. It reads the
host of each BYOK binding in the rendered AgentHost map, and adds the host to
the `agenthost-model-egress` Cilium network policy as an exact DNS name on TCP
port 443. It adds no other host. If you remove all BYOK bindings, the policy
allows only DNS. The deploy stops if a BYOK `BaseUrl` is not an HTTPS Azure
OpenAI endpoint on port 443. Do not edit the policy by hand.

After you select the model for a project, run one turn in the project. Make
sure that the turn gets a model response. A connection error or a time-out can
show that the AgentHost pod cannot reach the endpoint.

## Select the model for a project

1. Read the current project configuration and its revision:

   ```powershell
   agentweaver rest --method GET --url "/projects/<project-id>/configuration"
   ```

2. Make a request file. Use the current configuration, and set
   `expectedRevision` to the current revision. Replace `modelSelection` with
   `projectConfiguration.modelSelection` from the plan. Add the budget from
   `projectConfiguration.runLimits.spendBudgets` to the existing budgets.
   Remove any hosted `connectionId`.

3. Send the request:

   ```powershell
   agentweaver rest --method PUT --url "/projects/<project-id>/configuration" --body "@project-configuration-request.json"
   ```

Use `agentweaver api describe updateProjectConfiguration` to see the request
schema.

## Result

New runs in the project use the BYOK deployment. The Identity Broker reads the
exact Key Vault secret version for each run. Events & Sessions prices the
`byok.tokens` usage in USD, and Orchestrator stops a new model turn when the
run would go over the hard limit.

## Limits

- The rate card uses standard token prices. It does not use long-context
  prices, provisioned throughput, or batch prices.
- Agentweaver does not support the `none` or `minimal` reasoning efforts.
- To change prices, run the procedure again with a new `revision`. Runs that
  started with the earlier rate card keep it, and their new usage is not
  priced.
- Orchestrator does not yet allow a coordinated run's coordinator turn when
  a hard spend budget applies.

## Related information

- [Model binding contract](./configure-providers.md#configure-model-bindings)
- [Spend budget rules](./configure-providers.md#configure-spend-budgets)
- [Events & Sessions settings](../reference/runtime-configuration.md#events-sessions-settings)
