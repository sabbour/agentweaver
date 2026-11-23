# Manage platform BYOK provider metadata

Use the native CLI to create, read, update, price, recheck, disable, and switch
platform BYOK providers. A credential upload uses the dedicated credential
workflow; these commands never upload an API key. See the
[model-provider API routes](../reference/api.md#model-provider-draft-and-available-metadata).
For a Project's provider drafts, see
[Manage project BYOK provider metadata](./manage-project-byok-provider-metadata.md).

## View deployment-managed bindings

Sign in to the web app with a platform session. Open **Platform settings**,
enter your tenant ID under **Deployment-managed BYOK bindings**, and select
**Load bindings**. The list shows the responding Projects service's startup
copy and revision. It shows enabled and disabled BYOK references as
**Managed by deployment**. To refresh the copy, select **Load bindings** again.

## Before you begin

Use the Agentweaver CLI with a configured Gateway, Broker, API audience, and
tenant. See [Configure a target](./cli.md#configure-a-target).

Your account needs current PlatformAdmin authority. Sign in with the
`projects.admin` scope:

```powershell
agentweaver auth login --scope projects.admin
```

The API checks your current authority for every request.
These commands use an unbound identity. They ignore
`AGENTWEAVER_PROJECT_ID` and `AGENTWEAVER_RUN_ID` and reject `--project-id` and
`--run-id`.

## Add a provider draft

1. Enter the provider's metadata. Use `openai`, `azure`, or `anthropic` for
   the provider type. Give the base URL as an HTTPS URL. For Azure, use the
   resource host without an API path. Add `--rate-card-id` and
   `--rate-card-version` together when the runtime rate card exists.

   ```powershell
   $displayName = Read-Host "Provider display name"
   $providerType = Read-Host "Provider type: openai, azure, or anthropic"
   $providerBaseUrl = Read-Host "Provider HTTPS base URL"
   $deployment = Read-Host "Model deployment"
   $addOperationId = [guid]::NewGuid().ToString()

   agentweaver platform byok add `
     --name $displayName `
     --type $providerType `
     --base-url $providerBaseUrl `
     --model $deployment `
     --operation-id $addOperationId
   ```

2. Save the `providerId` and `revision` from the JSON response. The API
   generates the provider ID. The display name can change.

## Read and update provider metadata

1. List provider drafts and copy the provider ID:

   ```powershell
   agentweaver platform byok list
   ```

   `list` reads every page and returns one `providers` array. Use the
   `providerId` field in later commands.

2. Read the current provider metadata and revision:

   ```powershell
   $providerId = Read-Host "Provider ID from list or add response"
   agentweaver platform byok show $providerId
   ```

3. Enter the full metadata for the new revision. Copy optional values from
   `show` when you want to keep them. If you omit `--wire-api`,
   `--azure-api-version`, `--reasoning-effort`, or
   `--prompt-capacity-tokens`, the update clears those values. Omitting the
   rate-card pair clears an existing attachment. Specify both options to keep
   or replace the attachment.

   ```powershell
   $expectedRevision = Read-Host "Current revision from show"
   $displayName = Read-Host "Provider display name"
   $providerType = Read-Host "Provider type: openai, azure, or anthropic"
   $providerBaseUrl = Read-Host "Provider HTTPS base URL"
   $deployment = Read-Host "Model deployment"
   $updateOperationId = [guid]::NewGuid().ToString()

   agentweaver platform byok update $providerId `
     --expected-revision $expectedRevision `
     --name $displayName `
     --type $providerType `
     --base-url $providerBaseUrl `
     --model $deployment `
     --operation-id $updateOperationId
   ```

The update response keeps the same `providerId` and shows the new `name` and
`revision`. The name is display text, not a provider key.

## Save and attach a runtime rate card

Search authenticated Azure Retail prices for a selected Project:

```powershell
agentweaver project byok prices `
  --project-id $projectId `
  --region eastus `
  --search gpt-4.1
```

Use the selected meter IDs and expected USD-per-million values to build the
version-1 Azure Retail rate-card request. To save a complete manual envelope
instead, prepare its `runtimeRateCardSchemaVersion`, `rateCard`, and
`provenance` fields. Save either file with its exact ID and version:

```powershell
agentweaver platform byok save-prices $rateCardId $rateCardVersion `
  --source azure-retail `
  --input .\azure-rate-card.json
```

Use `--source manual` for a manual envelope. The CLI sends the file unchanged,
prints the validated receipt, and exits with code 1 for a conflict or failed
pricing operation. A manual `sourceUrl` must be an HTTPS URL without user
information, a query string, or a fragment.

Attach the exact returned ID and version to an `add` or `update` command. On
an update, omit both options only when you intend to clear the attachment.

## Recheck or disable a saved credential

Use the current provider and credential revisions:

```powershell
agentweaver platform byok recheck-key $providerId `
  --expected-revision $providerRevision `
  --expected-credential-revision $credentialRevision `
  --operation-id ([guid]::NewGuid().ToString())

agentweaver platform byok disable-key $providerId `
  --expected-revision $providerRevision `
  --expected-credential-revision $credentialRevision `
  --operation-id ([guid]::NewGuid().ToString())
```

`recheck-key` verifies the saved Vault version and current pricing attachment.
It does not upload a key or create a Vault version. `disable-key` disables the
credential slot and returns the provider to Draft. A quarantined or
publication-pending receipt exits with code 1.

## Activate or deactivate the platform default

Read `platform byok list` and use its `settingsRevision`. To switch new runs
to an `Available` provider:

```powershell
agentweaver platform byok activate $providerId `
  --expected-revision $providerRevision `
  --expected-settings-revision $settingsRevision
```

To restore hosted Copilot as the platform default:

```powershell
agentweaver platform byok deactivate `
  --expected-settings-revision $settingsRevision
```

The settings revision changes after a switch. Use the new value for the next
switch. Accepted runs keep their saved selections.

## Recover from a write result

If a write returns a conflict, read the current provider with `show`. Review
its current revision and metadata before you create a new update. Use the
current revision and a new operation ID for that update.

If the write result is uncertain or the CLI cancels the write, repeat the same
command with the same operation ID and the same fields. The API replays the
original write. Do not reuse that operation ID with changed fields.

If `list` or `show` reports `model_provider_draft_metadata_requires_update`,
use the provider ID and revision from the error with `update`. Supply complete
corrected metadata. The API does not return the rejected metadata.

## Import model bindings as drafts

Import a model bindings file created by `models byok plan --overlay-output`.
The command creates a platform Draft for each binding and returns its exact
`reference`, `providerId`, creation revision, and operation ID. The Draft does
not contain a key or price. Import does not activate a provider or remove a
deployment binding.

The target Gateway must support authorized target context and provider
reference reservations. Your account needs current unbound PlatformAdmin
authority and the `projects.admin` scope. The Projects service must allow new
reference reservations.

1. Keep the original bindings file and choose a new private context-file path.
   Obtain the intended installation ID from the installation owner through a
   trusted independent channel. The ID is a lowercase UUID.
2. In PowerShell, replace the paths and trusted ID, and capture the context:

   ```powershell
   $file = 'C:\path\bindings.json'
   $context = 'C:\path\import-context.json'
   $trustedInstallationId = '<trusted-installation-id>'
   agentweaver models byok import-context --output $context
   if ($LASTEXITCODE -ne 0) { throw 'The target context was not saved.' }
   $capturedInstallationId = (Get-Content -Raw -LiteralPath $context -ErrorAction Stop |
       ConvertFrom-Json -ErrorAction Stop).installationId
   if ($capturedInstallationId -cne $trustedInstallationId) {
       throw 'The routed installation differs from the intended installation.'
   }
   $digest = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
   $digest
   ```

3. Save the displayed digest, the original context file, the bindings file,
   and the target options. Append the same target options to both commands.
   Run the import:

   ```powershell
   agentweaver models byok import --model-bindings $file --file-sha256 $digest --expected-context $context
   ```

The CLI validates the complete file before creating a Draft. The output lists
confirmed Drafts in `completed` and reports the first stopped row in `stop`.
An `unknown` outcome means the write result needs recovery. Keep the original
context, bindings file, digest, account, and target options. Retry the same
command. The owner returns the original receipt and the CLI continues.

If the context file or digest is lost or uncertain, stop and reconcile with
the original installation. Do not replace the context with a new capture to
recover an uncertain write. A changed context, digest, or file is a new import
attempt. A reference already reserved by another operation returns
`reference_conflict`.

For incorrect metadata on a confirmed Draft, read its current revision and use
`platform byok update` with complete corrected metadata and a new operation ID.
Recover unknown rows first. To import remaining rows, make a new bindings file
without confirmed references and with matching model rates, then calculate its
new digest.

## Next step

For command syntax, metadata fields, and API error behavior, see the
[Agentweaver CLI guide](./cli.md#manage-platform-byok-provider-drafts).
