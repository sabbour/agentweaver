# Identity and secrets

## The mental model

Identity answers two different questions: who is calling, and which credential can that caller use for this operation? An authenticated identity does not grant access to every project or secret.

Identity Broker signs Agentweaver tokens after upstream OpenID Connect (OIDC) sign-in. Projects & Config owns current project membership and roles. A separate secret grant binds one actor, project, run, purpose, secret ID, and exact secret version. The Key Vault adapter retrieves bytes only after the Identity authorization layer approves that binding.

## Where this lives

- `services/identity/Agentweaver.Identity.Broker/Program.cs` — authentication and trusted dependency composition.
- `services/identity/Agentweaver.Identity.Broker/IdentityBrokerEndpoints.cs` — authorization, consent, and token exchange.
- `services/identity/Agentweaver.Identity.Broker/RefreshTokenHandlers.cs` — atomic refresh redemption and replay handling.
- `services/identity/Agentweaver.Identity.Broker/IdentityGrantAuthority.cs` — revisioned secret grants.
- `services/identity/Agentweaver.Identity.Broker/SecretRedemptionEndpoints.cs` — authenticated secret redemption.
- `services/identity/Agentweaver.Identity.Broker/RuntimeGrantAuthority.cs` — runtime bootstrap, source, and model-session grants.
- `packages/Agentweaver.Identity/AuthorizedSecretRedemption.cs` and `GrantContracts.cs` — exact grant checks.
- `packages/Agentweaver.Secrets.AzureKeyVault/AzureKeyVaultSecretRedemption.cs` — exact-version backend access.
- `packages/Agentweaver.Secrets.AzureKeyVault/AzureKeyVaultSecretVersionWriter.cs` — protected secret-version writes.

## Core concepts and invariants

### Tokens identify callers; owners authorize operations

The broker creates a local subject after upstream identity validation. It issues registered scopes and resource audiences, not upstream application roles. Resource services validate the token and obtain current effective permissions from Projects & Config. A tenant selector selects an existing membership; it cannot create one.

Project/run-bound tokens use one of two paths. Secret operations require an active secret-grant binding. Projects-owned operations require a private Projects receipt for current membership, the Orchestrator assignment, and the accepted run selection. The broker repeats the relevant check during exchange and refresh. A signed binding restricts the token to that project and run; it is not a project role.

### A reference is not a credential

`SecretRef` identifies a secret and version. Grant records persist that reference and authorization metadata, not the secret value. `SecretCredential` is the short-lived, invalidatable in-memory result.

`IdentityGrantAuthority` appends grant revisions with an expected revision and an idempotency key. An exact retry returns its committed result. Reusing the key for different metadata conflicts. Revocation appends a revoked revision rather than replacing history.

## Sign-in and token lifecycle

1. The authorization endpoint validates the requested client, scopes, redirect URI, and optional run binding.
2. Upstream OIDC authentication creates or finds the local broker user.
3. Consent uses the local cookie, an antiforgery token, and a single-use pending authorization handle.
4. Authorization-code exchange requires S256 PKCE. OpenIddict signs the resulting Agentweaver token for the registered resources.
5. Refresh rechecks the local account and run authority. Atomic redemption prevents two requests from consuming the same refresh token.

Reusing a redeemed or revoked refresh token revokes its authorization and token family. A lost concurrent redemption also rejects the exchange and revokes the family.

## Secret redemption flow

`POST /secrets/redeem` accepts `secretId`, `secretVersion`, `purpose`, and `runId`. The validated bearer supplies the actor, project, and run claims. The caller cannot supply those identity claims in the body.

`AuthorizedSecretRedemption` then:

1. Finds exactly one active, unexpired grant for the complete binding.
2. Calls the secret backend.
3. Reads the grant again after the asynchronous acquisition.
4. Compares its identity, revision, state, expiry, and every binding field.
5. Limits the credential lifetime to the grant expiry.

The Key Vault adapter requests the exact version. Its credential lifetime is at most five minutes and ends earlier when the vault secret expires. The broker invalidates the credential after writing the response. A failed post-acquisition check invalidates it before returning an error.

The AKS composition uses explicit workload identity settings. It has no `DefaultAzureCredential` fallback. The adapter handles retrieval; the authorization wrapper handles run and purpose checks.

## Runtime and connection credentials

Runtime credentials have their own registration and purpose checks. A bootstrap verifier is single-use and tied to the runtime configuration. Source grants support observation, rotation, and revocation. Model-session grants bind the current runtime registration to the accepted model and credential reference.

GitHub Copilot, GitHub Repo App, and remote MCP OAuth connections remain separate integrations. None grants a platform or project role. The credential-less sandbox gateway flow described in the architecture is a proposal, not a replacement for these implemented redemption paths.

## Failure behavior

Missing or ambiguous identity claims return `401`. Invalid redemption input returns `400`. A missing, changed, revoked, or expired grant returns `403`. Backend failures return a redacted `502` response. Redemption responses use `Cache-Control: no-store`; backend diagnostics and secret bytes do not enter the error response.

## Related pages

- [Identity contracts and configuration](../architecture/identity-secrets.md)
- [Contracts and endpoints](../reference/contracts.md)
- [Projects and configuration](./projects-config.md)
- [Agent runtime](./agent-runtime.md)
