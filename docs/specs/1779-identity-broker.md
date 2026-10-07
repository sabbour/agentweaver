# Story: Identity broker foundation

**Issue:** [#1779](https://github.com/sabbour/agentweaver/issues/1779).
**Status:** P0 implementation candidate. No publication, deployment, or Azure acceptance.

`Agentweaver.Identity.Broker` is a .NET 10/OpenIddict service.
It owns the `identity_broker` PostgreSQL schema and native OpenIddict stores.
It requires an external OIDC provider, an HTTPS issuer, and explicit certificate material.
It has no development certificate, ambient user-secrets ID, implicit principal, or local product-runtime mode.

The guarded installer can create initial signing material and retained
key-ring storage through a separate default-off operation.
It never generates a fallback certificate during Broker startup.
Existing material remains unchanged, and an orphaned key ring requires its
original signing certificate.
The installer can create an explicit run-owned public acceptance client with
an HTTP loopback callback.
It creates no upstream Entra application or credential.
An intended route-derived issuer is configuration, not public routing or
authorization acceptance.

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

After validating the configured external OIDC identity, the broker maps the upstream
issuer and subject to its local broker user. Broker-signed access tokens contain the
local `sub`, registered OAuth scopes, and resource audience; upstream tenant and role
claims are not forwarded and the broker does not assign Projects roles. Projects &
Config resolves the validated issuer and local subject against its own active
membership and resource-role records. OAuth scopes and tenant selectors cannot
create authority. Signed `project_id` and `run_id` claims are included only after
the broker validates the exact run-bound grant.

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
| `POST /secrets/redeem` | OpenIddict-validated bearer for the configured audience plus JSON `secretId`, `secretVersion`, `purpose`, and `runId`; success returns the short-lived credential value |
| `GET /health/live` | Process liveness |
| `GET /health/ready` | PostgreSQL connectivity |

The JSON consent prompt includes `consent_handle`, `client_id`, `requested_scopes`, and `csrf_token`.
The browser must retain the local and antiforgery cookies.
The client sends `csrf_token` through `X-CSRF-TOKEN` when it submits consent.
Consent is API-only in P0. The consent UI remains P1 work.

## Run-bound SecretRef redemption (#1783)

The redemption endpoint requires a bearer token validated by the existing
OpenIddict validation middleware for this broker's issuer, signature, lifetime,
and the explicit `IdentityBroker:SecretRedemption:Audience`. The audience must
also occur in an operator-registered client resource. The authenticated
principal must contain exactly one `sub`, `project_id`, and `run_id` claim.
Actor identity is the validated `sub`; project and run identity come from the
validated claims, never the request body or headers. The body intentionally
contains only the exact SecretRef identifier/version, purpose, and request run
ID. Unknown properties are rejected rather than accepted as caller-asserted identity.

The broker's supported authorize flow accepts optional `project_id` and
`run_id` query selectors. It stores them in the server-owned pending
authorization across external login and consent; they are not identity claims
and are never copied directly into a token. Before issuing a code-scoped
principal, Identity requires an unexpired active grant for the authenticated
local subject and exact project/run, then places those values in access-token
claims only. The broker repeats the active-binding check at token exchange and
refresh. Redemption still resolves the current durable grant revision for the
exact purpose and SecretRef.

The endpoint composes the admitted `AuthorizedSecretRedemption` primitive with
an Identity-owned PostgreSQL `IGrantAuthority` and the existing
`AzureKeyVaultSecretRedemption` adapter. The broker schema stores immutable
binding snapshots; replacement, renewal, and revocation append revisions under
a row-locked compare-and-swap transaction. Idempotency receipts are persisted
with each mutation. PostgreSQL triggers enforce append-only snapshots and
one-step revision advancement. No HTTP grant-administration endpoint is exposed;
only trusted Identity service components may call the mutation API.

The request carries references only. Credential bytes are not stored in the
grant schema, returned in denials, or logged. Successful redemption returns the
value only to the authenticated caller. Any denial or request cancellation after
backend acquisition invalidates the credential before the service responds.
The backend receives the exact requested SecretRef version.

## Host and deployment contract

### Runtime credential source candidate (#1850)

The source contains a separate runtime grant store. It does not use the
Key Vault secret-redemption grant or change human OAuth clients or claims.
The store keeps cryptographic nonce verifiers, exact runtime/actor/selection/
placement bindings, purpose, audience, revision, state, expiry, and configuration
hash. PostgreSQL revisions and operation receipts are append-only.

The reviewed grant SQL permits `SELECT`, `INSERT`, and `UPDATE` on runtime grant
heads. Runtime grant revisions, operations, and receipts permit only `SELECT`
and `INSERT`. The runtime cannot delete these records, update audit records,
create schema objects, or write migration history. A disposable PostgreSQL test
runs the credential lifecycle through a separate restricted runtime login.

Initial bootstrap material is delivered only through the Environment-owned
out-of-band boundary. A completed delivery receipt is required before nonce
consumption. Consumption, source exchange, and rotation use row-locked CAS.
Exact operation replay returns the original receipt, not a persisted credential
value. Each asynchronous owner lookup uses genuine protected actor credentials
and rechecks the current registration. Missing delivery or owner authorization
must fail explicitly.

The pending-ticket verifier checks the exact nonce, delivery operation,
configuration hash, audience, expiry, and current registration before delivery.
It returns only a grant receipt. It cannot consume a nonce, configure a runtime,
or issue a source credential. Credential values travel only over the fixed
authenticated HTTPS owner channel; they are not stored in an outbox.

Before the first database wait, Identity captures a verifier from a live protected
credential. It does not read the value after expiry. If the credential expires
while waiting for the grant lock, Identity denies the request and records
revocation for the exact verified nonce. Foreign proofs cannot change grant state.
An input that already has a past expiry is rejected before grant verification.
This input rejection does not prove durable owner-side revocation.

The Broker HTTP test uses an actual Broker-issued bearer and a separate nonce.
It covers pending verification, consumption, exchange, source verification,
rotation, revocation, and cookie-only denial. It isolates the owner and delivery
boundaries. The combined local harness separately connects actual current Core,
Projects, Environment, Orchestrator, native SDK, and Events accounting code.
It controls only external placement and SDK transport, catalog, and pricing inputs.
It covers revocation before SDK creation and authority loss during source transaction waits.
This evidence does not prove cloud deployment or paid model execution.

When runtime bootstrap is configured, the existing validated Broker audience
protects these routes. The routes return `Cache-Control: no-store`.

| Route | Result |
| --- | --- |
| `POST /internal/runtime/bootstrap/request` | Delivery receipt for the current registered placement |
| `POST /internal/runtime/bootstrap/verify-pending` | Proof-only pending grant receipt; no credential issuance |
| `POST /internal/runtime/bootstrap/consume` | Consumed bootstrap receipt |
| `POST /internal/runtime/bootstrap/exchange` | Short-lived source credential; replay returns the original receipt without a credential |
| `POST /internal/runtime/source/verify` | Current purpose-bound source receipt |
| `POST /internal/runtime/source/rotate` | New source revision and transient credential |
| `POST /internal/runtime/source/revoke` | Immutable revocation receipt |

The host supplies native .NET configuration through its deployment secret/configuration sources:

| Configuration key | Requirement |
| --- | --- |
| `ConnectionStrings:IdentityBroker` | Entra-only runtime role; `VerifyFull` TLS; no password; no schema or migration privileges |
| `ConnectionStrings:IdentityBrokerMigration` | Used only by `--migrate`; separate Entra-only schema-owner role; no password |
| `ConnectionStrings:IdentityBrokerBootstrap` | Used only by `--bootstrap-identity-postgres`; connects to `postgres` as the approved Entra administrator with `VerifyFull` TLS and no password |
| `IdentityBroker:Bootstrap:*` | Exact PostgreSQL FQDN, administrator username, initial database name, runtime and migration role names, and the two UAMI principal object IDs |
| `IdentityBroker:RuntimeBootstrap:OrchestratorOwnerAddress` / `EnvironmentOwnerAddress` | Optional runtime credential composition; both fixed HTTPS root owner addresses are required when configured |
| `IdentityBroker:RuntimeBootstrap:BootstrapLifetime` / `SourceLifetime` | Explicit positive credential lifetimes, bounded by current registration and actor expiry |
| `IdentityBroker:Issuer` | Public, absolute HTTPS issuer |
| `IdentityBroker:Signing:PfxPath` | Mounted private-key certificate for signing, token encryption, and key-ring protection |
| `IdentityBroker:Signing:PfxPassword` | Deployment secret, never a checked-in value |
| `IdentityBroker:DataProtectionKeyPath` | Durable writable key-ring volume protected by the signing certificate |
| `IdentityBroker:ExternalProvider:Authority` | Absolute HTTPS OIDC authority |
| `IdentityBroker:ExternalProvider:ClientId` | Registered upstream client ID |
| `IdentityBroker:ExternalProvider:ClientSecret` | Optional secret for an explicitly configured confidential upstream client; omit for public authorization-code + PKCE |
| `IdentityBroker:ExternalProvider:MetadataAddress` | Optional explicit HTTPS discovery address |
| `IdentityBroker:Clients` | Explicit client IDs, type, redirects, scopes, resources, and confidential-client secrets |
| `IdentityBroker:SecretRedemption:Audience` | Required HTTPS resource audience, also registered in an operator-seeded client resource list |
| `IdentityBroker:SecretRedemption:VaultUri` | Azure Key Vault root URI; root forms with or without a trailing slash are accepted |
| `IdentityBroker:SecretRedemption:WorkloadIdentityTenantId` / `WorkloadIdentityClientId` | Explicit Entra workload identity; no ambient credential or default identity |
| `IdentityBroker:SecretRedemption:WorkloadIdentityTokenFilePath` | Absolute projected workload-identity token path |
| `IdentityBroker:Migration:WorkloadIdentityTenantId` / `WorkloadIdentityClientId` | Separate explicit identity used only by the migration Job |
| `IdentityBroker:Migration:WorkloadIdentityTokenFilePath` | Absolute projected token path for the migration ServiceAccount |

The upstream app registration must contain the broker's exact public
`/signin-oidc` callback before deployment. This source change does not modify
the app registration. The Broker uses authorization-code flow with PKCE and
omits `client_secret` when `ClientSecret` is unset. A confidential upstream
client can use the optional direct setting supplied by the operator.
The deployment retains signing/encryption material and the protected key ring
across restart. The HTTPS listener uses a separate operator-approved TLS
certificate. The repository contains no certificate, password, OAuth secret,
issuer domain, tenant, client ID, or PostgreSQL role value.
Certificate rotation requires overlap with previous verification and decryption keys through a separate reviewed procedure.
Replacing the sole certificate immediately invalidates previous encrypted tokens and cookies.

The Kubernetes base references the required `identity-broker-runtime-config`
ConfigMap, `identity-broker-signing` and `identity-broker-tls` Secrets, and the
durable `identity-broker-key-ring` PVC. It also references
`identity-broker-client-secrets` when a confidential upstream or OAuth client
is configured. It does not create any of these objects. The runtime ConfigMap
contains non-secret settings and must register the exact issuer, upstream OIDC
authority/client, and every accepted client, redirect, scope, and resource
audience. Confidential client secrets use Secret keys, not ConfigMap values.

The `identity-broker` runtime ServiceAccount is federated to its own UAMI.
That identity uses the same explicit workload-identity settings for native
Key Vault redemption and PostgreSQL token acquisition. It has no database
schema or migration authority. The separate `identity-broker-migration`
ServiceAccount and UAMI are used only by the opt-in migration Job. Neither
identity is the acceptance-only `foundation-probe` principal.

Kestrel listens with HTTPS on port 8443, and the Service exposes port 443 as
`ClusterIP`. No public Ingress, Gateway, or controller is added here. Both
runtime and migration image references require the operator's approved
immutable registry digest; the checked-in sentinel is not deployable.

The separate routing candidate uses native AKS-managed Gateway API resources.
Its managed certificate supplies the `agentweaver` hostname within the AKS
wildcard domain, not the DNS zone root.
BackendTLSPolicy keeps HTTPS between the Gateway and Kestrel.
It validates the backend certificate with system trust and the route hostname.
The guarded installer derives the issuer and `/signin-oidc` callback from the
current admitted HTTPRoute.
It defaults to a read-only public-platform append or no-op plan.
An append requires separate explicit registration and exact-URI confirmation.
It fresh-reads the exact tenant and existing application before the write.
It patches only public redirects and preserves all other application properties.
An existing exact callback remains a no-op.
Explicit Gateway placement and security-policy inputs require separate approval.
The existing P0 namespace remains restricted.
Routing configuration does not prove a running Broker.
The optional readiness gate requires trusted HTTPS and ordinary DNS for both
health endpoints. See [the operator interface](../../scripts/azure/README.md#application-routing-source-setting).

The Dockerfile uses repository-root context and root build properties.
The image pins SDK `10.0.302-noble` independently of the host SDK in `global.json`.
Restore uses `--locked-mode`. Build uses `--no-restore`.
Publish uses both `--no-build` and `--no-restore`.
The Docker runtime and project `ContainerBaseImage` use the same public ASP.NET 10 manifest digest.
The project pin supports native SDK service preparation through the existing release pack helper.
The runtime pin identifies a registry manifest, not a prepared image archive or its configuration.
The project selects `ContainerRuntimeIdentifier=linux-x64` for the default x64 AKS node pool.
Docker builds use `--platform linux/amd64` for the same deployment architecture, independent of the build host.
The runtime uses UID/GID 10001 and exposes HTTPS port 8443.
Kestrel reads an operator-supplied PEM TLS certificate and key through native ASP.NET configuration.
The signing PFX is separate and protects OpenIddict credentials and the persisted data-protection key ring.
This candidate does not trust arbitrary forwarded headers.
The orchestrator probes `/health/live` and `/health/ready` over HTTPS. No shell health-check utility is required.
The key-ring volume must retain encrypted keys across process restart.

The explicit `--bootstrap-identity-postgres` command is an operator-only
first-time path. It uses the local Azure CLI credential, connects to the
configured `postgres` database, and creates the configured service database,
two UAMI principals, and the migration-owned schema only when the target has
no user databases or partial bootstrap state. It verifies an existing complete
bootstrap and refuses to adopt or reset partial state. It does not run EF
migrations. An interrupted creation requires manual reconciliation of the
exact PostgreSQL target before retry. Source support for this command does not
prove that it was used against a live database.

The installer runs this command only when both `--execute` and the separate,
default-off `--bootstrap-identity-postgres` option are supplied. It supports
the guarded full-foundation path and AKS-only redeployment to the exact existing
P0 target; the full-foundation empty-resource-group guard remains unchanged.
For the private PostgreSQL endpoint, the installer creates a temporary TCP-only
proxy pod and loopback-only port-forward. The operator host keeps the
password-free Azure CLI Entra token and uses `VerifyFull` TLS with the
PostgreSQL FQDN as the certificate target. It removes only the run-owned pod
and ConfigMap, stops its port-forward process, and removes temporary local
files. It does not create a public route, Entra app or secret, or Azure role
assignment. Ordinary `--execute` never runs the initializer. The Foundation
Probe remains read-only.

Ordinary startup never runs EF migrations or creates the schema. It checks
that the owned `identity_broker` schema exists and all migrations are applied,
then reconciles the configured clients and scopes. Missing schema, pending
migrations, authentication failure, or incompatible client registration
fails startup. Missing settings name the exact required configuration key.
The separate command
`dotnet Agentweaver.Identity.Broker.dll --migrate` requires only
`ConnectionStrings:IdentityBrokerMigration` and the three
`IdentityBroker:Migration:*` workload-identity settings. It applies owned
migrations under the schema-specific PostgreSQL advisory lock and exits; it
does not start the web host or seed OAuth clients. Operators must first run the
approved `postgres-identity-bootstrap.sql`, then run the migration Job, then
apply `postgres-identity-runtime-grants.sql`. No bootstrap or migration write
was run against a live database for this source change.

Readiness alone does not prove OAuth or deployed acceptance.

## Evidence and release boundary

Tests use real framework middleware, generated credentials, a fake upstream transport, and an isolated Testcontainers PostgreSQL database.
They cover code/PKCE, consent ownership/CSRF, cancellation during a real lock wait, permanent grants, restart, refresh, and invalid protocol.
A generated fake token credential also verifies the PostgreSQL token scope,
one asynchronous token request per new physical Npgsql connection, pooled
checkout reuse without another callback, cancellation and authentication
errors, and rejection of static passwords and synchronous connections.
Another disposable-PostgreSQL test runs the migration command with separate
generated migrator/runtime roles, verifies schema ownership and runtime DML,
and denies runtime schema creation and migration-history writes.
These tests do not create Entra principals or prove Azure token exchange.
A separate native resource server enforces a configured audience for issued and refreshed access tokens.
It rejects invalid issuer, audience, signature, purpose, and expiry.
Trace-level log tests cover successful requests, denial, and invalid/replayed grants without raw token or secret values.
Test artifacts remain under ignored repository artifacts and never use shared credentials.
The #1783 tests also exercise real redemption middleware and HTTP against
disposable PostgreSQL, including revision races, idempotency, ownership,
restart, cancellation, and error redaction. A Testcontainers end-to-end test
also completes the broker's external-login, consent, authorization-code, and
token flow with a durable run grant, then proves successful redemption and
cross-project/run denial using the broker-issued token. These local tests do
not prove deployed token issuance, workload identity, Key Vault RBAC, or Azure
acceptance.

The draft release manifest registers `Agentweaver.Identity.Broker` at project version `0.0.0`.
The draft omits `imageDigest` until actual publication supplies it.
A local Docker image proves only the local build, not registry publication or cloud execution.

Azure publication and deployed proof remain dependent on
[#1790](https://github.com/sabbour/agentweaver/issues/1790).
