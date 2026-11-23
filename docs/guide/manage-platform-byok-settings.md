# Manage a platform BYOK provider in Platform Settings

Use **Platform settings** to save a model provider, its prices, and its
write-only key. Then choose the provider for new runs.

## Before you begin

Your account needs current PlatformAdmin authority. Use an unbound platform
session with `api.read` and `projects.admin`.

Prepare the provider's HTTPS endpoint, model or deployment name, supported
reasoning efforts, prompt capacity, and exact input and output prices per
million tokens. Prepare the key only if the provider requires one.

## Add and save a provider

1. Open **Platform settings**.
2. Select **Add provider**.
3. Select **Custom endpoint**, **Azure OpenAI**, or **Anthropic**.
4. Enter the display name, base URL, and model or deployment.
5. Keep the provider's reasoning efforts and prompt capacity. These values
   remain unchanged when you edit other fields.
6. Select one pricing option:
   - Select **Save manual prices** and enter exact decimal prices, a rate-card
     ID, a version, and a source URL.
   - Select **Use a saved rate card** and enter the saved card ID and version.
7. Enter the API key if the provider needs one. Leave the field blank when a
   saved key must remain unchanged.
8. Select **Save provider**.

Agentweaver saves prices first, then provider metadata, then the key. The key
field clears after submission and the key does not appear in later reads.

## Reopen and update a Draft

Select **Edit** on a `Draft` provider. `Available` provider metadata is
read-only.

Choose a pricing action every time you reopen a Draft:

- Select **Save manual prices** to create a rate-card version.
- Select **Use a saved rate card** to keep or replace the card link.
- Select **Clear the saved rate card**, then select the confirmation checkbox,
  to clear the link.

When a manual rate-card version conflicts, Agentweaver saves the prices under a
new version instead of replacing the existing card.

After you correct pricing for a Draft that has a saved key, select **Recheck
saved key**. The page checks the current credential version and publishes the
provider from the stored key. It does not write a second key version.

## Recover a pending key publication

A pending key publication shows `publication_pending` and keeps the original
operation ID and revision fences in page memory. Select **Retry publication**.
The retry checks the stored key state without sending the key again.
Retry errors appear beside the **Retry publication** action.

A quarantined publication shows `quarantined` with a recovery handoff. Use the
named recovery operation only with verified evidence for that original key
write. Do not enter a new key for the same uncertain operation.

## Choose the default for new runs

Use a provider only after its state is `Available` and its key status is
**Key saved**. Select **Use for new runs**.

Select **Use Copilot for new runs** to restore hosted Copilot. These controls
change the default for fresh runs. Existing accepted runs keep their saved
provider selection.

When the page shows the setup prerequisite, initialize platform model settings
before changing the default.

## Check the result

The provider card shows three separate values:

- `Draft` or `Available`
- **Key saved** or **No key saved**
- **Default for new runs**

After a successful switch or conflict, the page reloads the current settings
revision and provider revisions.
