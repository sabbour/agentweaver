# Identity and secrets

## 1–4. Goals, constraints, context and strategy

Use this page to understand how Agentweaver separates caller identity from
permission to use a particular credential. An authenticated user does not
automatically have access to every project or secret.

The design separates upstream sign-in, current project permission and exact
credential use. A credential request can therefore fail after authentication
succeeds. It must not bypass revocation by relying on a previously signed token.

## 5. Building blocks and trust boundaries

### Distinguish identity from credential access

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
| `RuntimeOwnerHttpTransport.cs` | Shared internal owner HTTPS trust and bounded requests |
| `AzureKeyVaultSecretRedemption.cs` | Exact-version Key Vault reads |
| `AzureKeyVaultSecretVersionWriter.cs` | Protected secret-version writes |
| `Program.cs` | Authentication and trusted dependency composition |

These files are under `services/identity/Agentweaver.Identity.Broker/` or
`packages/Agentweaver.Identity/` and
`packages/Agentweaver.Secrets.AzureKeyVault/`.

The Broker runtime reads `identity_broker.__ef_migrations_history` at startup
but does not own or write that table. The migration command applies the
canonical runtime table grants as the separate migration owner.
[Migration history and grants](../reference/runtime-configuration#identity-broker-migration-history-and-grants)
defines the runtime role setting and the separately approved repair path.

Internal owner HTTPS clients validate server certificates against the
read-only `service-backend-ca` mount at
`/var/run/service-backend-ca/ca.crt`. `RuntimeOwnerHttpTransport` supplies the
bounded owner-request handler; Environment's `RuntimeSandboxHttpTransport`
retains direct-to-Pod-IP routing for bootstrap and sandbox calls while
validating the TLS hostname and the same backend CA. `RuntimeServiceTlsIdentity`
adds custom-root chain validation to pinned mTLS owner clients while retaining
hostname checks, the configured peer leaf pin, server-authentication purpose,
the client certificate, and online revocation checks. Public issuer discovery
and external OAuth providers continue to use normal system trust.

## 6. Runtime view

### Understand token authority

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

### Follow the sign-in flow

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

Each sign-in has its own ad hoc authorization for its token family; the
permanent authorization remains the reusable consent record. OpenIddict rejects
ordinary sequential refresh-token replay before the custom replay handler and
revokes that session's tokens without revoking consent. A lost concurrent
redemption claim rejects the exchange and invokes the custom family revoker,
which revokes all family tokens and only revokes the authorization when it is
ad hoc. Legacy tokens attached to a permanent authorization are revoked without
revoking the saved consent.

### Redeem a secret

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

### Understand runtime and connection credentials

Runtime credentials have separate registration and purpose checks. A bootstrap
verifier is single-use and tied to runtime configuration. Source grants support
observation, rotation, and revocation. Model-session grants bind the current
runtime registration to the accepted model and credential reference.

GitHub Copilot, GitHub Repo App, and remote MCP OAuth connections are separate
integrations. None grants a platform or project role. The credential-less
sandbox gateway flow described in the architecture is a proposal, not a
replacement for the implemented redemption paths.

### Diagnose failures

| Condition | Result |
| --- | --- |
| Missing or ambiguous identity claims | `401` |
| Invalid redemption input | `400` |
| Missing, changed, revoked, or expired grant | `403` |
| Secret backend failure | Redacted `502` |

Redemption responses use `Cache-Control: no-store`. Error responses do not
include backend diagnostics or secret bytes.

## 7–9. Deployment, shared concepts and decisions

Broker runs as a shared HTTPS owner with its PostgreSQL schema and configured
OIDC/secret backends. Runtime identity reads migration history; the separate
migration owner applies schema changes and canonical table grants.
The [deployment view](./infra-deployment.md) describes target wiring.

Projects remains the live role owner, while [AgentHost](./agent-runtime.md)
consumes separate runtime purposes. Secret retrieval, project authority and
network trust are complementary checks, not interchangeable evidence.
See [canonical ADR statuses](./00-system-overview.md#9-architecture-decisions);
the [credential-less proposal](../architecture/identity-secrets.md#credential-less-sandbox-proposal)
does not replace implemented Sandbox credential delivery.

## 10–12. Quality, risks and glossary

| Context and stimulus | Response and measure | Verification state |
| --- | --- | --- |
| Grant is revoked while the backend retrieves a secret | Post-acquisition recheck denies and invalidates the credential | Secret-authority controlled tests |
| Refresh redemption loses its atomic claim | Reject the exchange, revoke the session token family and ad hoc authorization, and preserve permanent saved consent | Broker integration tests exercise the deterministic family-revocation path and consent reuse; not live upstream SSO |
| Key Vault returns an exact-version credential | Lifetime is at most five minutes and no later than grant/vault expiry | Adapter/authority tests; not deployed vault access |

An unexpired token can outlive a role or grant. Identity and resource owners
must preserve rechecks and redacted errors; diagnostic backend failures must
not disclose secret bytes.

**Terms:** `SecretRef` is an exact-version reference; `SecretCredential` is an
invalidatable in-memory value; a scope is a route permission, not a project role.

## Related content

- [Identity contracts and configuration](../architecture/identity-secrets.md)
- [Contracts and endpoints](../reference/contracts.md)
- [Projects and configuration](./projects-config.md)
- [Agent runtime](./agent-runtime.md)
