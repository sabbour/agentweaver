# Identity and secrets

`Agentweaver.Identity.Broker` is a .NET 10 service candidate. It uses OpenIddict and owns the `identity_broker` PostgreSQL schema. The service has no published image or deployment.

The broker accepts an external OIDC provider. It validates the upstream issuer, signature, audience, expiry, and nonce before it creates a local user.

OAuth authorization codes require registered clients, exact redirect URIs, registered scopes, and S256 PKCE. Consent requires the local cookie and antiforgery token. Refresh-token replay revokes the authorization and token family.

After upstream OIDC validation, the broker creates a local subject and issues access tokens with registered OAuth scopes and resources. It does not forward upstream tenant or role claims and does not assign application roles. Projects & Config is the sole live owner of issuer-and-subject project memberships and resource-role assignments. Other resource services obtain current effective permissions through `GET /api/authorization/context` instead of keeping duplicate membership or role records or caches. The route uses the validated caller and returns a versioned, no-store context; token claims and tenant selectors cannot create authority. Grant-validated `project_id` and `run_id` are included only for tokens bound to that exact project and run.

```mermaid
sequenceDiagram
    actor User
    participant IdP as Configured OIDC provider
    participant Broker as Identity Broker
    participant API as Resource API
    participant Projects as Projects & Config authorization owner
    User->>IdP: Authenticate
    IdP-->>Broker: Validated upstream identity
    Broker->>Broker: Replace upstream subject with local broker subject
    Broker->>Broker: Apply registered scopes and resource audience
    Broker-->>API: Signed access token (sub, scope, audience, optional project/run binding)
    API->>API: Validate issuer, signature, lifetime, and audience
    API->>Projects: GET /api/authorization/context with caller token
    Projects-->>API: Effective permissions, contract v1, Cache-Control: no-store
    API->>API: Authorize this request; keep no membership or role cache
    Note over API,Projects: Each privileged request obtains fresh owner context; no authorization pins are issued.
```

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'" alt="After bearer validation, Identity.Broker passes the authenticated actor to the authorization wrapper. The wrapper checks the exact grant before and after exact-version Key Vault access, invalidates a credential on a failed postcheck, and the host invalidates it after responding." />
  </a>
  <figcaption>This sequence starts after native bearer validation. It shows the redemption boundary, not upstream sign-in, OAuth consent, or grant issuance.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-identity-redemption.drawio'">Open editable draw.io source</a></p>

The broker issues `project_id` and `run_id` claims only after it finds an active grant for the authenticated local user. Token exchange and refresh repeat that binding check. These claims constrain a request to the signed project/run; they do not grant a project role. Projects & Config owns its issuer-and-subject memberships, resource-role assignments, and immutable authorization audit in `projects_config`. Its runtime database principal can read but cannot insert, update, or delete those records; provisioning and revocation use a separate privileged source path.

`POST /secrets/redeem` accepts a `SecretRef` identifier and exact version, purpose, and run ID. The bearer token supplies actor, project, and run claims. The broker does not accept caller-supplied identity claims.

`AuthorizedSecretRedemption` checks the exact grant before and after backend acquisition. A changed, expired, revoked, missing, or ambiguous grant denies redemption. The broker invalidates an acquired credential after the response.

The Key Vault adapter uses an injected credential or explicit `WorkloadIdentityCredentialOptions`. The AKS composition requires tenant ID, client ID, and an absolute projected token-file path. It has no `DefaultAzureCredential` fallback.

The adapter requests the exact Key Vault version. A returned credential expires within five minutes or at the earlier vault expiry. The grant store keeps references and binding snapshots, not secret values.

No secret-redemption grant-management HTTP endpoint exists. Read [contracts and endpoints](../reference/contracts) for the implemented route list and [configuration](../reference/contracts#identity-host-configuration) for host keys.

## Remote MCP OAuth source boundary

The Broker source candidate defines an Identity-owned connection binding for the
authenticated human, tenant, project, stable connection ID, Environment
configuration revision and digest, opaque Identity binding reference, canonical
HTTPS endpoint and resource, issuer, client ID, redirect URI, installed MCP
transport profile, and canonical scopes. Each provider configuration also names
the exact approved resource URIs, OAuth endpoints, and scopes. Consent state binds that snapshot and its
connection revision to a short-lived correlation ID. It stores only a hash of
the random state, the S256 challenge, and a protected verifier `SecretRef`
identifier/version; it does not persist the raw state, verifier, access token,
or refresh token. Connection credentials are represented only by protected
secret references and provider expiry metadata.

The lifecycle state transitions cover single-use callback claiming and
completion, binding and expiry checks, refresh-attempt and credential-revision
compare-and-swap outcomes, and disconnect/revocation state. These are source
contracts and transition helpers, not a wired provider protocol. The Broker
exposes bearer-authenticated, no-store status, consent preparation, and
disconnect management routes. They recheck the owner's current Projects
authorization and the current Environment configuration. Consent preparation
links Identity's opaque reference to the exact Environment revision and digest,
re-reads that pin, then fetches metadata only for the exact configured resource
and issuer. It rejects redirects, bounds metadata responses, and validates the
resource, issuer, scopes, S256/code profile, and exact operator-approved OAuth
endpoints. It returns a constructed authorization URI and stores only a
protected verifier reference and correlation state; the web application uses a
same-origin popup callback page to relay only the authorization code and state
to its opener. The Gateway forwards that pair with the current user's bearer to
Identity. Identity rechecks current Projects authority and Environment
configuration, claims the callback once, redeems the protected verifier, and
exchanges the code only at the metadata-approved token endpoint. It stores
returned access and refresh tokens only as protected `SecretRef` versions and
publishes them with connection and credential revision checks. A timeout or
secret-store failure after a possible provider request resolves the callback
claim as uncertain rather than making the code reusable. Projects authorization
and Environment configuration use separate configured owner addresses;
Environment requests are not routed through Projects.
Status redacts credential references and always reports credential use
unavailable. Disconnect requires the expected connection, credential, and
configuration revisions; it clears the stored references through a
revision-guarded update, but does not delete secret versions or revoke tokens at
the provider.

Refresh requires expected connection, credential, and configuration revisions.
Identity claims a durable refresh lease before redeeming the exact refresh-token
version. It rechecks Projects authority and the Environment binding after each
provider or secret-store wait, then commits new SecretRefs with a revision
compare-and-swap. Provider rejection clears local credentials and marks the
connection revoked. A timeout, store failure after provider exchange, or stale
lease after restart marks the connection indeterminate; Identity does not retry
a refresh token that the provider may have consumed.

The Identity package also validates supplied protected-resource and
authorization-server metadata against the bound resource, issuer, requested
scopes, S256/code profile, and an explicit endpoint list. It can construct the
corresponding authorization URI from the bound redirect/resource/scopes and
short-lived PKCE material. These helpers perform no discovery HTTP, do not
grant authority to the endpoint list, and are not called by a browser redirect
route.

The web client has a typed management API and a callback relay, but no project
settings flow yet starts consent or owns the pending connection state. Provider
revocation and purpose-bound credential delivery to MCP requests are not wired
in this source slice.
No deployed OAuth consent or remote MCP credential use is established. The
following flow describes the implemented management and callback boundary:

```mermaid
sequenceDiagram
    actor Human
    participant Broker as Identity Broker
    participant Projects as Projects authorization owner
    participant Environment as Environment configuration owner
    participant MCP as Remote MCP server
    participant IdP as Configured OAuth issuer
    Human->>Broker: GET status or POST consent/refresh/disconnect
    Broker->>Broker: Resolve connection owned by authenticated subject
    Broker->>Projects: Recheck current ReadProjects or WriteProjects authority
    Projects-->>Broker: Current project authority
    Broker->>Environment: Read current connection snapshot and immutable configuration
    Environment-->>Broker: Current configuration revision and pins
    alt Consent preparation
        Broker->>Environment: Link opaque Identity reference with revision and digest CAS
        Environment-->>Broker: Final immutable revision, digest, and receipt
        Broker->>Environment: Re-read final revision and current head
        Environment-->>Broker: Confirmed binding pins
        Broker->>MCP: Fetch protected-resource metadata without redirects
        MCP-->>Broker: Resource and approved authorization-server issuer
        Broker->>IdP: Fetch issuer metadata without redirects
        IdP-->>Broker: Exact approved endpoints and S256/code profile
        Broker->>Broker: Build authorization URI; persist state hash and protected verifier reference
        Broker-->>Human: Authorization URI; caller controls browser navigation
    else Callback with code and state
        Web-->>Web: Same-origin popup relay; clear callback query
        Web->>Gateway: Send only code and state with current bearer
        Gateway->>Broker: Forward exact callback body and bearer
        Broker->>Broker: Claim correlation; recheck authority and configuration
        Broker->>IdP: Exchange code with PKCE verifier and exact resource/redirect
        IdP-->>Broker: Access and optional refresh token response
        Broker->>Broker: Write protected secret versions; commit only current binding by CAS
        Broker-->>Web: Redacted owner status
    else Disconnect with expected revisions
        Broker->>Broker: CAS connection and credential revisions; clear SecretRefs
        Broker-->>Human: Disconnected status; no credential-use authority
    else Refresh with expected revisions
        Broker->>Broker: CAS durable refresh lease
        Broker->>Projects: Recheck current WriteProjects authority
        Broker->>Environment: Recheck exact configuration revision and resource
        Broker->>IdP: Exchange refresh token at the approved token endpoint
        IdP-->>Broker: New access and optional rotated refresh token
        Broker->>Broker: Store protected SecretRefs and CAS current claim
        Broker-->>Human: Authorized status or explicit indeterminate result
    else Status
        Broker-->>Human: Redacted status; credential use unavailable
    end
    Note over Broker,Environment: Provider revocation, MCP credential use, and deployed behavior remain unavailable
```

## GitHub connections are separate integrations

Core sign-in, GitHub Copilot credentials, and GitHub App repository credentials have
different owners and purposes. Broker OAuth refresh does not refresh a GitHub Copilot
credential. A GitHub App installation token cannot authenticate the Copilot SDK.

| Integration | Current source boundary | Missing work |
| --- | --- | --- |
| Core sign-in | Broker OIDC, OAuth, consent, refresh, and purpose-bound secret redemption exist. | These contracts do not supply either GitHub connection lifecycle. |
| GitHub Copilot | Identity implements owner-bound account linking, PKCE/cookie callbacks, exact-version writes, refresh rotation, and current binding checks in source. The SDK consumes only the current user access token. | Deployed OAuth, writer permissions, and live entitlement remain outside [#1906](https://github.com/sabbour/agentweaver/issues/1906) source evidence. |
| GitHub App | Identity owns the Repo App user connection, installation/repository selection, refresh, and run-bound exact-repository token mint used by Source Control. | Reachable settings UI and deployment are not included in this source slice; the relevant Web/MCP work remains [#1859](https://github.com/sabbour/agentweaver/issues/1859) and [#1908](https://github.com/sabbour/agentweaver/issues/1908). |
| Web and MCP | Existing core routes and tools are implemented. | Reachable retained settings and real GitHub connection/repository routes: [#1859](https://github.com/sabbour/agentweaver/issues/1859) and [#1908](https://github.com/sabbour/agentweaver/issues/1908). |

The compatible 0.x Copilot flow binds browser state, PKCE, cookies, and a single-use
callback to the current subject. Its refresh path serializes redemption, rotates
refresh tokens, and uses version checks. Provider rejection can require reconnect;
a transient failure must not fabricate that result.

The #1907 source candidate implements that GitHub App producer inside Identity. User
authorization uses OAuth S256 PKCE, an owner-bound state and protected verifier, and a
short-lived HttpOnly callback cookie. Installation setup uses its own owner-bound
single-use callback. Identity discovers only installations and repositories visible to
the connected user. A returned selection code is short-lived and is submitted only in
the initial run-scoped Orchestrator `/pin` request, never in accepted Project/Run
configuration or the durable pin. Identity stores only its hash, binds its first
run-bound use to one owner and project, and permits later minting only for that same
project using the hash.

The Copilot source flow is not deployed login, writer permission, or paid-execution authority.
The separate GitHub App and browser integrations retain their own delivery and acceptance boundaries.
Accepted narrow source contracts remain accepted.
Automatic webhook delivery and workflow triggers also need a separate service-identity
scope decision; the current human-authenticated relay does not prove that parity.
Identity stores rotating OAuth token references and provider expiry metadata, not token
values. Refresh is serialized by a database lease and credential-revision compare-and-swap.
Provider rejection marks the connection revoked; an uncertain rotation locks the
connection instead of retrying a possibly consumed refresh token. Before token mint,
Identity rechecks the active actor/project/run grant, exact connection revision,
installation and repository, and current provider discovery. It redeems the exact
GitHub App private-key SecretRef for the bound run, signs the App JWT in memory, and
requests an installation token for exactly one repository with `contents:write` and
`pull_requests:write`. It requests `issues:write` only when the pinned provider needs
issue creation. `IssueWriteGranted` is true only when GitHub's returned permission map
confirms `issues:write`; a requested but ungranted permission fails closed, including
an explicit provider rejection, without a narrower retry. Permission digests derive
from the actual returned map, remints preserve the immutable pinned scope, and GitHub's
expiry and returned permission set are authoritative.
The private key and installation token are never stored in PostgreSQL. The Orchestrator
receives the token only for the current operation and invalidates it afterward; later
operations repeat Identity's binding and permission checks.

The browser integration uses one configured HTTPS Web origin. When GitHub Repo App
support is enabled, `IdentityBroker:WebOrigin` is required and is the only origin
allowed to send credentialed requests to the Broker's browser OAuth and Repo App fetch
routes. CORS preflight is handled before authentication; owner authentication,
antiforgery, and callback-cookie checks remain unchanged. Internal token redemption,
runtime, health, and diagnostics routes are not browser CORS surfaces. The Broker's
GitHub callback URI stays on the Broker origin, and successful completion redirects
to the fixed Web settings path. The Gateway uses the same explicit Web origin but
remains bearer-only and does not accept cookie credentials.

These source routes and tests do not mean that the Broker, settings surface, GitHub
App, Key Vault writer permissions, or any cloud service have been deployed. The
connection does not create a Projects role, broaden OAuth authority, or enable paid
execution. Automatic public webhook delivery and workflow triggers still require a
separate service-identity scope decision; the current human-authenticated relay does
not provide that authority.

### Minimum Copilot credential writer source scope

The approved minimum source work in #1906 adds protected secret-version writes inside
the existing Key Vault adapter and trusted Identity lifecycle. It returns opaque
`SecretRef` metadata, not token values. Identity stores connection identity, credential
kind, owner and revision, freshness, state, and the current secret-version reference.
Tokens remain in the protected secret store and runtime memory, never raw PostgreSQL columns.

The approved selection-contract repair uses explicit `SourceMode` and a stable
`ConnectionId` for a hosted platform/project binding. The ID is a reference, not
authority. Trusted Identity facts and the current Core selection must establish the
owner, domain, credential kind, freshness, and revocation state under existing sharing
rules. This adds no personal-provider precedence or blanket credential sharing.

Each ModelSession receipt and redemption pins the current connection revision, exact
secret version, grant revision, and credential kind. Normal token rotation advances
those owner-controlled revisions without changing the accepted selection or its hash.
Fresh proof is required after remote waits and before SDK consumption. Stale receipts
deny or require legitimate current refresh; caches cannot supply authority. A change
of owner, kind, scope, or GitHub identity requires an authorized new binding or selection.
Legacy stored selections remain unchanged, and unsupported hosted selections require
an explicit migration-required or unavailable result until a new selection is authorized.
There is no automatic migration or hash rewrite. Exact `CredentialReference` remains BYOK-only.

The owner claims refresh before upstream rotation. After writing a new secret version,
it rechecks current authority and publishes the reference through its PostgreSQL revision
compare-and-swap. Azure secret writes are not an atomic connection-state CAS.
A failed write, revoked authority, or lost CAS must not report a new current credential.
Interrupted rotation requires an explicit recovery outcome; unreferenced versions are
not permission for automatic cloud cleanup.

This source scope is not deployed writer authority. Existing P0 Key Vault Secrets User
assignments remain read-only and unchanged. A deployed writer needs secret SET/new-version,
exact-version GET, and metadata rights at the actual configured credential-store scope.
The exact supported role or custom actions and bootstrap scope must be verified with
the separately approved live proposal. Do not default to the broader Secrets Officer
role or assume secret-prefix RBAC. Missing writer support or permissions must return
an explicit unavailable or denied result before hosted SDK use, without raw token fallback.

## Separate runtime credential candidate

Runtime configure and observe grants use a separate Identity-owned store. They
do not widen secret redemption, OAuth claims, or project roles. The optional
Broker owner routes require a validated bearer and an independent cryptographic
nonce bound to the exact runtime registration, purpose, audience, and expiry.
OpenIddict supplies expiry through its validated principal metadata.

Identity captures only a verifier before database waits. After the grant lock
is acquired, it checks the exact stored binding and fresh owner authority.
Expiry or authority loss after that verified proof records immutable revocation
before denial. An already-expired input is rejected without claiming a verified
nonce or durable cleanup. Audit tables permit only `SELECT` and `INSERT`; grant
heads also permit CAS updates.

The auth-first SDK hook consumes a delivered configure nonce before session creation.
It takes the model reference from the current registration and rechecks source and
owner authority after SDK awaits. The registration candidate also pins the exact
Environment provider, lifecycle generation, lease revision, and lease-bounded expiry.
After SDK preparation, the hook compares the current registration, verifies the
observe grant, and checks lifetimes and cancellation immediately before `session.create`.
There is no asynchronous owner lookup after that final grant verification.
These pins do not give a run-bound token public project-write authority.

The combined local harness uses actual Broker OAuth, current Core membership and roles,
accepted Projects selection, Environment lease/profile, native SDK callbacks, and Events accounting.
It controls only external placement and SDK transport, catalog, and pricing inputs.
Revocation before SDK preparation produces zero SDK requests and source records.
Grant revocation, expiry, or Environment retirement during SDK preparation produces
zero native session creations, source records, and accounting entries.
Authority loss during source transaction waits denies the observation without accounting.
This evidence does not prove cloud deployment or paid model execution. See the
[runtime credential source contract](../reference/contracts#runtime-credential-source-candidate).

## PostgreSQL authentication and workload boundary

Runtime PostgreSQL connections use the explicit `WorkloadIdentityCredential`, no password, and `SslMode.VerifyFull`. Npgsql acquires an Entra token asynchronously through `UsePasswordProvider` for each new physical connection, using the fixed scope `https://ossrdbms-aad.database.windows.net/.default`. The runtime username is the separately bootstrapped Entra runtime role.

Ordinary host startup verifies that the `identity_broker` schema exists and that all migrations are applied; it never applies migrations or creates database roles. Only the explicit `--migrate` command runs migrations, in a separate one-shot Job with its own ServiceAccount, workload identity, connection-string key, token settings, and schema-owner role. See [PostgreSQL configuration and roles](../reference/contracts#identity-postgresql-access).

The Kubernetes source defines an HTTPS Deployment behind an HTTPS `ClusterIP` Service. It defines no Ingress, Gateway, or ingress controller. The source has not been deployed. Operators must supply the real issuer and external OIDC authority/domain, client registrations and credentials, approved immutable image, TLS certificate/key, signing PFX/password, durable key-ring storage, approved PostgreSQL administrator and roles, and the exact read-only Key Vault fixture. These inputs are placeholders or external responsibilities; this page makes no deployed-readiness claim.
