# Manage project BYOK provider metadata

Use the native CLI to create, read, and update BYOK provider metadata for one
Project. Your account needs current Owner or PlatformAdmin authority for that
Project. The API checks your authority on each request.

## Select the Project and sign in

Configure the CLI target as described in
[Configure a target](./cli.md#configure-a-target). Use the exact project ID,
not the Project's display name. Project BYOK requests use a token bound to
that project and no run.

```powershell
$projectId = Read-Host "Project ID"
$env:AGENTWEAVER_RUN_ID = $null
agentweaver auth login --scope projects.admin --project-id $projectId
```

The CLI sends `projects.admin` with the exact `project_id`. The scope does not
give your account a project role.

## Add a provider draft

Enter the provider metadata and create the draft:

```powershell
$displayName = Read-Host "Provider display name"
$providerType = Read-Host "Provider type: openai, azure, or anthropic"
$providerBaseUrl = Read-Host "Provider HTTPS base URL"
$deployment = Read-Host "Model deployment"
$operationId = [guid]::NewGuid().ToString()

agentweaver project byok add `
  --project-id $projectId `
  --name $displayName `
  --type $providerType `
  --base-url $providerBaseUrl `
  --model $deployment `
  --operation-id $operationId
```

The API generates the `providerId`. Save it and the `revision` from the JSON
response. The provider name is display text and can change. The command sends
metadata only; it does not send keys or credentials.

## Read and update provider metadata

List all provider drafts for the Project:

```powershell
agentweaver project byok list --project-id $projectId
```

The CLI reads every page and returns one `providers` array. Use `providerId`
from the list or add response to read the current metadata and revision:

```powershell
$providerId = Read-Host "Provider ID from list or add response"
agentweaver project byok show $providerId --project-id $projectId
```

Update the complete metadata. Copy optional values from `show` when you want
to keep them. If you omit `--wire-api`, `--azure-api-version`,
`--reasoning-effort`, or `--prompt-capacity-tokens`, the update clears those
values. Add `--rate-card-id` and `--rate-card-version` together to keep or
replace an attachment. Omitting the pair clears an existing attachment.

```powershell
$expectedRevision = Read-Host "Current revision from show"
$displayName = Read-Host "Provider display name"
$providerType = Read-Host "Provider type: openai, azure, or anthropic"
$providerBaseUrl = Read-Host "Provider HTTPS base URL"
$deployment = Read-Host "Model deployment"
$operationId = [guid]::NewGuid().ToString()

agentweaver project byok update $providerId `
  --project-id $projectId `
  --expected-revision $expectedRevision `
  --name $displayName `
  --type $providerType `
  --base-url $providerBaseUrl `
  --model $deployment `
  --operation-id $operationId
```

The update keeps the same `providerId` and returns the new revision. If the
revision is stale, read the current provider and review its metadata before
starting a new update.

## Recover from an uncertain write

If the write result is uncertain or the CLI cancels the write, repeat the same
command with the same operation ID and fields. The API replays the original
write. Use a new operation ID for a new write. Do not reuse an operation ID
with changed fields.

## Recheck availability for a saved credential

If a saved credential left the provider in `Draft` because pricing was missing
or unavailable, attach the verified rate card and run a same-version recheck:

```powershell
$operationId = [guid]::NewGuid().ToString()
agentweaver project byok recheck-key $providerId `
  --project-id $projectId `
  --expected-revision $expectedProviderRevision `
  --expected-credential-revision $expectedCredentialRevision `
  --operation-id $operationId
```

The Broker verifies the stored, enabled Key Vault version without uploading a
key or creating a new Vault version, and Projects transitions `providerState`
to `Available` when the saved rate card verifies against Events.

## Set, search prices, or disable a credential

To upload or rotate a project UTF-8 API key from redirected stdin (up to 8,192
bytes after stripping one final LF or CRLF):

```powershell
$operationId = [guid]::NewGuid().ToString()
$apiKey | agentweaver project byok set-key $providerId `
  --project-id $projectId `
  --from-stdin `
  --expected-revision $expectedProviderRevision `
  --operation-id $operationId
```

Search authenticated Azure Retail meters before you create a rate card:

```powershell
agentweaver project byok prices `
  --project-id $projectId `
  --region eastus `
  --search gpt-4.1
```

Disable the credential slot with exact provider and credential revisions:

```powershell
agentweaver project byok disable-key $providerId `
  --project-id $projectId `
  --expected-revision $expectedProviderRevision `
  --expected-credential-revision $expectedCredentialRevision `
  --operation-id ([guid]::NewGuid().ToString())
```

Both commands force a project-bound identity without a run binding, even when
`AGENTWEAVER_RUN_ID` is set.

For command syntax and metadata options, see
[Manage project BYOK provider drafts](./cli.md#manage-project-byok-provider-drafts).
