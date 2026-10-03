# Story: Identity broker foundation

**Issue:** [#1779](https://github.com/sabbour/agentweaver/issues/1779).
**Status:** P0 implementation candidate. No publication, deployment, or Azure acceptance.

`Agentweaver.Identity.Broker` is a .NET 10/OpenIddict service.
It owns the `identity_broker` PostgreSQL schema and native OpenIddict stores.
It requires an external OIDC provider, an HTTPS issuer, and explicit certificate material.
It has no development certificate, ambient user-secrets ID, implicit principal, or local product-runtime mode.

## Authentication and grants

The native OIDC handler completes token-response and nonce validation before it provisions a local user.
Its authenticated-ticket event remaps the upstream subject to a broker user.
The native cookie handler completes sign-in.
Invalid issuer, audience, signature, expiry, or nonce creates neither a local cookie nor a durable user.
Concurrent first logins for the same issuer and subject return the same durable user ID.
Only conflicts on the issuer/subject unique constraint trigger recovery; cancellation and other database failures propagate.

Authorization-code grants require registered clients, exact redirect URIs, registered scopes, and S256 PKCE.
The broker accepts no dynamic client registration.
Only clients configured with `offline_access` receive refresh-grant permission.
Other clients cannot request that scope or receive refresh tokens.
Consent requires the local cookie, the exact pending subject, and a native antiforgery token.
An atomic PostgreSQL transaction consumes consent and creates the permanent authorization together.
Anonymous, other-user, invalid-scope, replayed, expired, and canceled approvals fail closed.

Issued principals carry the permanent authorization ID and resources from native scope registration.
Resources are global scope attributes in OpenIddict, not client-supplied token audiences.
Clients that share a scope share its operator-declared resource set.
Refresh retains the authorization and configured audiences.
Reference refresh tokens have no reuse grace period.
Replay revokes the authorization and its token family.
Token exchange rejects disabled users and grants bound to another client.

Startup creates missing clients and scopes.
It refuses changes to registered redirects, permissions, requirements, type, consent policy, credentials, or scope resources.
Confidential-client reconciliation uses native secret verification, never plaintext-to-hash comparison.
Credential changes require an explicit administrative migration.

## API contract

| Endpoint | Contract |
| --- | --- |
| `GET /connect/authorize` | Native OAuth validation, external login, or JSON consent prompt |
| `GET /connect/authorize/resume` | Authenticated, single-use continuation of external login |
| `POST /connect/consent` | JSON `consent_handle`, `approve`, optional scope subset, local cookie, and `X-CSRF-TOKEN` |
| `POST /connect/token` | Native form-encoded authorization-code or refresh exchange |
| `GET /diagnostics/whoami` | Local native bearer validation diagnostic, not an audience-policy acceptance test |
| `GET /health/live` | Process liveness |
| `GET /health/ready` | PostgreSQL connectivity |

The JSON consent prompt includes `consent_handle`, `client_id`, `requested_scopes`, and `csrf_token`.
The browser must retain the local and antiforgery cookies.
The client sends `csrf_token` through `X-CSRF-TOKEN` when it submits consent.
Consent is API-only in P0. The consent UI remains P1 work.

## Host and deployment contract

The host supplies native .NET configuration through its deployment secret/configuration sources:

| Configuration key | Requirement |
| --- | --- |
| `ConnectionStrings:IdentityBroker` | Owned PostgreSQL database, production TLS, schema/migration permissions |
| `IdentityBroker:Issuer` | Public, absolute HTTPS issuer |
| `IdentityBroker:Signing:PfxPath` | Mounted private-key certificate for signing, token encryption, and key-ring protection |
| `IdentityBroker:Signing:PfxPassword` | Deployment secret, never a checked-in value |
| `IdentityBroker:DataProtectionKeyPath` | Durable writable key-ring volume, shared by broker replicas |
| `IdentityBroker:ExternalProvider:Authority` | Absolute HTTPS OIDC authority |
| `IdentityBroker:ExternalProvider:ClientId` / `ClientSecret` | Registered upstream confidential client |
| `IdentityBroker:ExternalProvider:MetadataAddress` | Optional explicit HTTPS discovery address |
| `IdentityBroker:Clients` | Explicit client IDs, type, redirects, scopes, resources, and confidential-client secrets |

The upstream registration permits the broker's public `/signin-oidc` callback.
The deployment retains signing/encryption certificates and the protected key ring across restart.
Certificate rotation requires overlap with previous verification and decryption keys through a separate reviewed procedure.
Replacing the sole certificate immediately invalidates previous encrypted tokens and cookies.

The Dockerfile uses repository-root context and root build properties.
The image pins SDK `10.0.302-noble` independently of the host SDK in `global.json`.
Restore uses `--locked-mode`. Build uses `--no-restore`.
Publish uses both `--no-build` and `--no-restore`.
The Docker runtime and project `ContainerBaseImage` use the same public ASP.NET 10 manifest digest.
The project pin supports native SDK service preparation through the existing release pack helper.
The runtime pin identifies a registry manifest, not a prepared image archive or its configuration.
The project selects `ContainerRuntimeIdentifier=linux-x64` for the default x64 AKS node pool.
Docker builds use `--platform linux/amd64` for the same deployment architecture, independent of the build host.
The runtime uses a non-root account and exposes port 8080.
The deployment supplies an HTTPS Kestrel endpoint and mounted TLS certificate through native Kestrel configuration.
OAuth traffic must reach Kestrel over HTTPS, including traffic from a TLS-terminating ingress.
This candidate does not trust arbitrary forwarded headers.
The orchestrator probes the two health endpoints directly. No shell health-check utility is required.
The runtime volume permissions must permit the image account to read certificates and write protected data-protection keys.

Startup applies owned migrations under a schema-specific PostgreSQL advisory lock.
Database failure, incompatible client registration, and missing configuration fail startup.
Readiness alone does not prove OAuth or deployed acceptance.

## Evidence and release boundary

Tests use real framework middleware, generated credentials, a fake upstream transport, and an isolated Testcontainers PostgreSQL database.
They cover code/PKCE, consent ownership/CSRF, cancellation during a real lock wait, permanent grants, restart, refresh, and invalid protocol.
A separate native resource server enforces a configured audience for issued and refreshed access tokens.
It rejects invalid issuer, audience, signature, purpose, and expiry.
Trace-level log tests cover successful requests, denial, and invalid/replayed grants without raw token or secret values.
Test artifacts remain under ignored repository artifacts and never use shared credentials.

The draft release manifest registers `Agentweaver.Identity.Broker` at its initial `0.1.0` project version.
A component-scoped changeset records minor release intent. No manual version bump occurs.
The draft omits `imageDigest` until actual publication supplies it.
A local Docker image proves only the local build, not registry publication or cloud execution.

Run-grant authorization and exact-version Key Vault redemption composition remain dependent on
[#1783](https://github.com/sabbour/agentweaver/issues/1783).
Azure publication and deployed proof remain dependent on
[#1790](https://github.com/sabbour/agentweaver/issues/1790).
This broker does not expose secret redemption or purpose-bound run-token APIs.
