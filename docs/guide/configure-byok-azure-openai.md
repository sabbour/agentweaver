# Configure an Azure OpenAI BYOK model

Use this procedure to let a project run on your own Azure OpenAI deployment
(bring your own key, BYOK). The `agentweaver models byok plan` command writes a
model bindings file for your environment. The deploy helper adds the binding
from this file to the runtime maps for AgentHost, Events & Sessions, and
Projects & Config. The command also makes the project settings that select the
model and set a USD spend limit for each run.

Keep the model bindings file outside the repository. The deployment source
then keeps only the product model bindings, and you can deploy any later
source commit with the same file.

This procedure is for a platform operator who deploys Agentweaver. The
commands do not change a cluster, Key Vault, or Azure resource. They never
read or write an API key.

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
| `reference` | The model reference that projects select. It must not be a product model reference. |
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

## Make the plan

1. Make a plan and a model bindings file. Use the deployment source of the
   commit that you deploy:

   ```powershell
   agentweaver models byok plan --input byok.json --source deploy\k8s --output byok-plan.json --overlay-output <path-outside-repository>\model-bindings.json
   ```

   The command reads the three runtime maps below `deploy\k8s` and prints a
   summary. It writes the full plan and the model bindings file to new files.
   It does not change the deployment source. If you use Retail meters, it
   reads each price from the Azure Retail Prices API. It requires exactly one
   USD consumption price for each meter.

2. Review the summary and the plan file. The plan contains:
   - The new binding and revision.
   - The rate card ID, version, and prices.
   - `projectConfiguration`: the model selection, the model binding pin, and
     the spend budget for the project.

3. Review the model bindings file. It contains:
   - `baseRevision` and `baseModelBindings`: the product model bindings that
     the file adds to.
   - `revision`: the new model-binding revision.
   - `modelBindings`: the BYOK binding.
   - `rateCard` and `modelRates`: the rate card ID, version, and USD prices
     for one million tokens.

   The file contains no API key and no Key Vault reference. Keep it with your
   environment settings, outside the repository.

The deploy helper adds these items to the rendered runtime maps:

| Runtime map | Change |
| --- | --- |
| `ConfigMap/agenthost-runtime-v1` | Adds the binding and the new revision. |
| `ConfigMap/events-and-sessions-runtime-config` | Copies the binding, and sets the Azure rate card and the `byok.tokens` USD meter. |
| `ConfigMap/projects-config-runtime-config` | Copies the source mode, enabled state, and efforts, so that projects can select the model. |

AgentHost and Events & Sessions must load the same binding map. The plan
checks that both maps give the same configuration hash as the pin.

Do not use `agentweaver models byok apply` for an environment binding. It
writes the binding into the deployment source, and the deploy helper then
stops for deployment source that does not have the binding.

## Deploy the change

1. Deploy with the model bindings file. See
   [Deploy Agentweaver to Azure](./deploy-to-azure.md). For example:

   ```powershell
   npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --model-bindings <path-outside-repository>\model-bindings.json --execute
   ```

   To deploy an exact source commit, give the same option to
   `npm run azure:deploy-from-commit`.

2. Use the same file for each later deploy to this cluster. If you do not,
   the deploy stops before it changes the cluster.

The deploy helper checks the file before it changes the cluster:

- The product revision and product bindings must be equal to
  `baseRevision` and `baseModelBindings`. If the product bindings change in
  a later commit, make a new file and a new revision from that commit.
- A reference in the file must not exist in the product bindings.
- The deploy stops if the rendered AgentHost map would remove a live BYOK
  binding, or change it without a new model-binding revision.
- The deploy stops if it would change the live Azure rate card without a new
  revision.

The deploy helper restarts Events & Sessions when its runtime map changes, so
that it reads the new binding map and rate card. You do not have to restart
it.

The deploy helper also lets AgentHost reach the model services. It reads the
model bindings in the rendered AgentHost map, and adds hosts to the
`agenthost-model-egress` Cilium network policy as exact DNS names on TCP port
443:

- For each BYOK binding, it adds the host of the binding `BaseUrl`.
- If at least one binding uses the GitHub Copilot provider (`SourceMode`
  `HostedCopilot`), it adds the GitHub Copilot hosts in the table that follows.

| Host | Use |
|---|---|
| `api.github.com` | The Copilot CLI reads the Copilot user record. The record gives the Copilot API host for the plan of the user. |
| `api.githubcopilot.com` | Default Copilot API host. |
| `api.individual.githubcopilot.com` | Copilot API host for individual plans. |
| `api.business.githubcopilot.com` | Copilot API host for Copilot Business. |
| `api.enterprise.githubcopilot.com` | Copilot API host for Copilot Enterprise. |

The helper adds no other host and no wildcard. Copilot telemetry,
experiment, and public-code matching hosts are not in the list, because a model
turn does not need them. GitHub Enterprise Cloud with data residency
(`ghe.com`) is not supported. If the map has no BYOK binding and no GitHub
Copilot binding, the policy allows only DNS. The deploy stops if a BYOK
`BaseUrl` is not an HTTPS Azure OpenAI endpoint on port 443. Do not edit the
policy by hand.

Source commits before this change cannot use `--model-bindings`.
`npm run azure:deploy-from-commit` stops when the commit does not support it.

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

## Move an existing binding to a model bindings file

If you added a binding to the deployment source with `apply`, move it to a
model bindings file. Keep the same revision, so that the project pins stay
valid:

1. Make the file from the product source, as in [Make the plan](#make-the-plan).
   Use the same `reference` and `revision` that you applied.
2. Compare the file with the live maps. Read the binding from
   `ConfigMap/agenthost-runtime-v1`. Read the rate card ID, version, and
   model rates from the `EventsAndSessions__Cost__Azure__` keys in
   `ConfigMap/events-and-sessions-runtime-config`.
3. If the rate card version or a rate is different, set the live value in
   `rateCard` and `modelRates`. Retail prices and the rate card version can
   change after you apply the plan.
4. Deploy source without the applied binding, and give the file with
   `--model-bindings`. The deploy stops if the binding or the rate card is
   different from the live value at the same revision.

A model bindings file can contain more than one binding. `plan` writes one
binding. To add another binding, put it and its model rates into the same
file by hand, and set a new `revision`.

## Change or remove the binding

Each change to the model bindings uses a new revision. A new revision makes
the model binding pin of every project not valid. After the deploy, select the
model again for each project that uses a model binding. Use the
`projectConfiguration` from the new plan.

To change the binding, for example its prices, make a new plan and a new model
bindings file with a new `revision`. Then deploy with the new file.

To remove the binding:

1. Change each project that selects the BYOK model to another model.
2. Deploy without `--model-bindings`. Name the binding to remove:

   ```powershell
   npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --remove-model-binding <reference> --execute
   ```

   The deploy stops if the reference is not a live BYOK binding, if the
   rendered map still has it, or if the revision does not change. Deploy
   source with its product revision, or use a model bindings file with a new
   revision.

## Limits

- The rate card uses standard token prices. It does not use long-context
  prices, provisioned throughput, or batch prices.
- Agentweaver does not support the `none` or `minimal` reasoning efforts.
- To change prices, run the procedure again with a new `revision`. Runs that
  started with the earlier rate card keep it, and their new usage is not
  priced.
- A model bindings file adds bindings to the product bindings. It cannot
  change or remove a product binding.

## Related information

- [Model binding contract](./configure-providers.md#configure-model-bindings)
- [Spend budget rules](./configure-providers.md#configure-spend-budgets)
- [Events & Sessions settings](../reference/runtime-configuration.md#events-sessions-settings)
