# Identity and secrets

Use this page to understand how Agentweaver separates caller identity from
permission to use a particular credential. An authenticated user does not
automatically have access to every project or secret.

## Distinguish identity from credential access

Identity Broker is the service that signs Agentweaver tokens after upstream
OpenID Connect (OIDC) sign-in. Projects & Config owns current project
membership and roles. A secret grant binds one actor, project, run, purpose,
secret ID, and exact secret version. The Key Vault adapter retrieves secret
bytes only after Identity approves that binding.

| Source | Responsibility |
| --- | --- |
| `IdentityBrokerEndpoints.cs` | Authorization, consent, and token exchange |
| `RefreshTokenHandlers.cs` | Atomic refresh redemption and replay handling |
| `IdentityGrantAuthority.cs` | Revisioned secret grants |
| `SecretRedemptionEndpoints.cs` | Authenticated secret redemption |
| `RuntimeGrantAuthority.cs` | Runtime bootstrap, source, and model-session grants |
| `AuthorizedSecretRedemption.cs` | Exact grant checks |
| `AzureKeyVaultSecretRedemption.cs` | Exact-version Key Vault reads |
| `AzureKeyVaultSecretVersionWriter.cs` | Protected secret-version writes |
| `Program.cs` | Authentication and trusted dependency composition |

These files are under `services/identity/Agentweaver.Identity.Broker/` or
`packages/Agentweaver.Identity/` and
`packages/Agentweaver.Secrets.AzureKeyVault/`.

## Understand token authority

The Broker creates a local subject after validating upstream identity. It
issues registered scopes and resource audiences, not upstream application
roles. Resource services validate the token and obtain current effective
permissions from Projects & Config. A tenant selector can select an existing
membership; it cannot create one.

Project/run-bound tokens use one of two paths:

- Secret operations require an active secret-grant binding.
- Projects-owned operations require a private Projects receipt for current
  membership, Orchestrator assignment, and accepted run selection.

The Broker repeats the relevant check during exchange and refresh. A signed
binding restricts a token to that project and run; it is not a project role.

A `SecretRef` identifies a secret and version. Grant records persist the
reference and authorization metadata, not the secret value. A
`SecretCredential` is the short-lived, invalidatable in-memory result.

`IdentityGrantAuthority` appends grant revisions with an expected revision and
idempotency key. An exact retry returns its committed result. Reusing a key for
different metadata conflicts. Revocation appends a revoked revision and
preserves history.

## Follow the sign-in flow

You need a registered client and a valid upstream OIDC sign-in to obtain an
Agentweaver token.

1. The authorization endpoint validates the client, scopes, redirect URI, and
   optional run binding.
2. Upstream OIDC authentication creates or finds the local Broker user.
3. Consent uses the local cookie, an antiforgery token, and a single-use
   pending authorization handle.
4. Authorization-code exchange uses proof key for code exchange (PKCE) with
   the S256 code challenge method. OpenIddict signs the Agentweaver token for
   registered resources.
5. Refresh rechecks the local account and run authority. Atomic redemption
   prevents two requests from consuming the same refresh token.

Reusing a redeemed or revoked refresh token revokes its authorization and token
family. A losing concurrent redemption also rejects the exchange and revokes
the family.

## Redeem a secret

Before redemption, obtain a valid bearer for the bound actor, project, and run,
and ensure an active grant covers the intended purpose and exact secret
version.

`POST /secrets/redeem` accepts `secretId`, `secretVersion`, `purpose`, and
`runId`. The bearer supplies the actor, project, and run claims; the caller
cannot provide those identity claims in the body.

1. `AuthorizedSecretRedemption` finds exactly one active, unexpired grant for
   the complete binding.
2. It asks the secret backend for the credential.
3. It reads the grant again after the asynchronous acquisition.
4. It compares the grant identity, revision, state, expiry, and every binding
   field.
5. It limits credential lifetime to the grant expiry.

The Key Vault adapter requests the exact version. Credential lifetime is at
most five minutes and ends earlier if the vault secret expires. The Broker
invalidates the credential after writing the response, and it invalidates a
credential before returning an error if the post-acquisition check fails.

The Azure Kubernetes Service (AKS) composition uses explicit workload identity
settings and has no `DefaultAzureCredential` fallback. The adapter retrieves the secret; the
authorization wrapper enforces run and purpose checks.

For the supported configuration keys, see the
[runtime configuration reference](../reference/runtime-configuration.md).

## Understand runtime and connection credentials

Runtime credentials have separate registration and purpose checks. A bootstrap
verifier is single-use and tied to runtime configuration. Source grants support
observation, rotation, and revocation. Model-session grants bind the current
runtime registration to the accepted model and credential reference.

GitHub Copilot, GitHub Repo App, and remote MCP OAuth connections are separate
integrations. None grants a platform or project role. The credential-less
sandbox gateway flow described in the architecture is a proposal, not a
replacement for the implemented redemption paths.

## Diagnose failures

| Condition | Result |
| --- | --- |
| Missing or ambiguous identity claims | `401` |
| Invalid redemption input | `400` |
| Missing, changed, revoked, or expired grant | `403` |
| Secret backend failure | Redacted `502` |

Redemption responses use `Cache-Control: no-store`. Error responses do not
include backend diagnostics or secret bytes.

## Related content

- [Identity contracts and configuration](../architecture/identity-secrets.md)
- [Contracts and endpoints](../reference/contracts.md)
- [Projects and configuration](./projects-config.md)
- [Agent runtime](./agent-runtime.md)
