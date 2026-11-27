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

Pass `--credential-status` to read the provider's credential state and both
revisions without returning key bytes:

```powershell
agentweaver project byok show $providerId --project-id $projectId --credential-status
```

The command returns `scope`, `projectId`, `providerId`, `providerRevision`,
`state` (`not_set`, `enabled`, `disabled`, `pending`, or `quarantined`), and
`credentialRevision`.

Update the complete metadata. Copy optional values from `show` when you want
to keep them. If you omit `--wire-api`, `--azure-api-version`,
`--reasoning-effort`, or `--prompt-capacity-tokens`, the update clears those
values. The CLI `update` command also sends `null` for `--rate-card-id` and
`--rate-card-version` when you omit those flags, which clears an existing
rate-card attachment. Copy `--rate-card-id` and `--rate-card-version` from
`show` to keep or replace an attachment. Direct API `PUT` callers that omit
both `rate_card_id` and `rate_card_version` from JSON preserve the existing
attachment; sending both as `null` clears it, and sending only one fails with
`400`. Saving the rate-card link does not activate the provider; Projects
verifies the linked card against Events during key publication.

```powershell
$expectedRevision = Read-Host "Current revision from show"
$displayName = Read-Host "Provider display name"
$providerType = Read-Host "Provider type: openai, azure, or anthropic"
$providerBaseUrl = Read-Host "Provider HTTPS base URL"
$deployment = Read-Host "Model deployment"
$rateCardId = Read-Host "Current rate-card ID from show"
$rateCardVersion = Read-Host "Current rate-card version from show"
$operationId = [guid]::NewGuid().ToString()

agentweaver project byok update $providerId `
  --project-id $projectId `
  --expected-revision $expectedRevision `
  --name $displayName `
  --type $providerType `
  --base-url $providerBaseUrl `
  --model $deployment `
  --rate-card-id $rateCardId `
  --rate-card-version $rateCardVersion `
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

## Set or rotate an API key in the web app

Project owners can add or rotate a project BYOK provider API key in
**Project settings > Connections > BYOK provider drafts**:

1. Open a project provider draft. The page loads the provider metadata and
   its current credential status without displaying any saved key.
2. When the credential status is **No key saved**, enter the key in **Add API key**
   and select **Save key**.
3. When the credential status is **Key saved**, enter the replacement key in
   **Rotate API key** and select **Save key**. The form sends the current
   provider and credential revisions and clears the typed key immediately.
4. If the credential status is **Pending**, **Quarantined**, **Disabled**, or
   unavailable, the key input stays disabled. Use **Retry publication** or
   **Check key status** for an in-flight operation, or recover or cancel a
   quarantined write with the CLI using the shown operation ID.

## Disable an API key in the web app

Project owners can disable an enabled project BYOK provider API key in
**Project settings > Connections > BYOK provider drafts**:

1. Open a project provider draft whose credential status is **Key saved** and
   whose provider revision matches the detail view, with no credential
   operation in progress.
2. Select **Disable API key**.
3. Review the confirmation prompt (**Disable API key for {provider name}?**)
   and select **Confirm disable**, or select **Cancel** to keep the key active.
   Disabling the key creates a `Draft` revision without a credential binding.
   Runs already assigned this key continue to use it, and the provider
   settings stay saved.
4. If the reply is lost, use **Check disable status** or **Retry disable**
   with the shown operation ID. If the provider or key state changes before
   the disable finishes, the page refreshes and requires a new confirmation.

For command syntax and metadata options, see
[Manage project BYOK provider drafts](./cli.md#manage-project-byok-provider-drafts).
