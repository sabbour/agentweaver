# Manage platform BYOK provider metadata

Use the native CLI to create, read, and update platform BYOK provider
metadata. This procedure does not set a key or activate a provider.
For a Project's provider drafts, see
[Manage project BYOK provider metadata](./manage-project-byok-provider-metadata.md).

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
   resource host without an API path.

   ```powershell
   $displayName = Read-Host "Provider display name"
   $providerType = Read-Host "Provider type: openai, azure, or anthropic"
   $providerBaseUrl = Read-Host "Provider HTTPS base URL"
   $deployment = Read-Host "Model deployment"
   $operationId = [guid]::NewGuid().ToString()

   agentweaver platform byok add `
     --name $displayName `
     --type $providerType `
     --base-url $providerBaseUrl `
     --model $deployment `
     --operation-id $operationId
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
   `--prompt-capacity-tokens`, the update clears those values.

   ```powershell
   $expectedRevision = Read-Host "Current revision from show"
   $displayName = Read-Host "Provider display name"
   $providerType = Read-Host "Provider type: openai, azure, or anthropic"
   $providerBaseUrl = Read-Host "Provider HTTPS base URL"
   $deployment = Read-Host "Model deployment"
   $operationId = [guid]::NewGuid().ToString()

   agentweaver platform byok update $providerId `
     --expected-revision $expectedRevision `
     --name $displayName `
     --type $providerType `
     --base-url $providerBaseUrl `
     --model $deployment `
     --operation-id $operationId
   ```

The update response keeps the same `providerId` and shows the new `name` and
`revision`. The name is display text, not a provider key.

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

## Next step

For command syntax, metadata fields, and API error behavior, see the
[Agentweaver CLI guide](./cli.md#manage-platform-byok-provider-drafts).
