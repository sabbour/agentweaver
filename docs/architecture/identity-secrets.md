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

## Credential-less sandbox proposal

**Status: proposed design, not implemented or deployed.** Credential-less means
that untrusted sandbox code cannot obtain real upstream credentials, including
model keys, Copilot access tokens, Git credentials, Azure bearer tokens, refresh
tokens, private keys, or equivalent exchangeable cloud assertions. A `SecretRef`
that the guest can redeem is not credential-less. Neither is federation that
lets the guest obtain an upstream bearer token without a stored secret.

The current [AgentHost](agenthost.md) consumes a model credential in the native
SDK inside the Sandbox. That source remains a separate legacy credential-delivery
path; its guarded tools, private state, and credential-free process environment
do not establish this stronger boundary. The diagrams above describe existing
Identity source, not the proposed guest boundary below.

### Trust boundary and placement

The trusted outbound gateway is an implementation of the existing L7 Network
Policy layer and Tool & MCP mediation role, not a new identity service or the
browser Gateway/BFF's arbitrary upstream proxy. Identity still owns credential
sourcing, connection lifecycle, and exact-version redemption through
`ISecretRedemption` / `AuthorizedSecretRedemption`. Projects & Config owns live
membership and roles; Orchestrator owns current actions, Policy receipts, runtime
registration, and execution fences. Environment owns placement, network intent,
leases, and readiness. Credential sourcing and permission to perform an outbound
effect are separate checks.

Untrusted code includes guest shell commands, tools, dependencies, repository
content, and any guest SDK process. The trusted gateway, Identity clients, token
caches, and any credential-consuming native runtime must be **outside that
guest's security boundary**. Use the existing remote-service or in-process
provider hosting patterns in a trusted host. A sidecar is acceptable only where
the provider proves independent isolation from guest code. Two containers in one
Kata Pod normally share one guest VM; a second container is not a second VM.
Container names, different UIDs, private `HOME`, or read-only mounts alone do not
prove that credentials are inaccessible.

Environment admission must prove that the guest cannot read trusted host/gateway
process memory, private `HOME`, token caches, credentials, signing or TLS private
keys, service sockets, host paths, shared private volumes, or snapshots of that
state. Do not expose workload-identity projected assertions, Kubernetes service
account tokens/API access, IMDS/metadata endpoints, or cloud token exchange to the
guest. A workspace shared for authorized file operations carries data only,
never host state or credential helpers. Treat its contents as hostile on the
trusted side. Snapshots and SDK-cache export cannot include credential-bearing
state; disable that export for a mode that cannot prove safe separation.

The guest uses a **low-authority gateway identity**, separate from owner/service
tokens and upstream credentials. Bind it to one current runtime registration,
tenant/project/run/session, placement generation, execution fence, gateway
audience, expiry, and allowed operation profile. Prefer provider-attested
transport identity or sender-bound proof verified outside the guest; the gateway
does not trust a source IP or guest-supplied identity header. A guest-held proof
can be copied by hostile guest code, so sender binding is not a claim that a
guest private key is unextractable. Enforce the registered placement/channel,
replay protection, and current authority as well. The proof cannot authenticate
to upstreams, redeem secrets, exchange for owner tokens, configure another
runtime, or gain public `WriteProjects`. Replaying it from another placement or
session must fail. It still permits misuse of operations actually authorized to
that guest; it is not a secret-free assertion that the guest has no authority.

### Authorized request flow

Prefer a structured gateway operation for a bound model, repository, Azure
resource, or connection over accepting an arbitrary URL. The gateway resolves
the upstream route and credential from trusted owner state.

```mermaid
sequenceDiagram
    participant Guest as Untrusted guest
    participant Gateway as Trusted outbound gateway outside guest
    participant Owners as Projects / Orchestrator / Environment
    participant Identity as Identity and Secrets provider
    participant Upstream as Approved upstream
    Guest->>Gateway: Bound operation, input, operation ID, gateway-only proof
    Gateway->>Owners: Validate current actor, action, selection and placement fences
    Owners-->>Gateway: Current authority and committed Policy admission
    Gateway->>Gateway: Validate destination, request and upstream TLS peer
    Gateway->>Identity: Acquire credential for exact accepted connection and purpose
    Identity->>Identity: Check current grant before and after acquisition
    Identity-->>Gateway: Short-lived credential for trusted use only
    Gateway->>Owners: Recheck current authority and bindings after waits
    Owners-->>Gateway: Same authorized action and current fences, or deny
    Gateway->>Upstream: Insert authentication outside guest; send validated request
    Upstream-->>Gateway: Response or bounded stream
    Gateway->>Gateway: Apply response contract and remove credential exposure
    Gateway-->>Guest: Authorized data and credential-free operation receipt
    Note over Gateway,Identity: Any failed postcheck invalidates acquired credential and prevents dispatch
```

Authenticate first, then obtain fresh `GET /api/authorization/context` and the
current Orchestrator action/registration and Environment lease evidence through
their existing owner contracts. The gateway uses an admitted trusted runtime/
actor delegation derived from authenticated enrollment, not its administrator
identity or a guest-supplied subject. That delegation stays outside the guest;
missing owner-route support fails explicitly without impersonation or broader
service grants. Validate input and destination before acquiring
credentials. Resolve and validate the upstream TLS connection; any connection,
DNS, credential, policy, or owner wait requires fresh binding/authority checks
before authentication insertion and dispatch. Identity's post-acquisition grant
check does not replace the gateway's action and placement postcheck. No saved
allowlist, admission receipt, or token cache is fresh authorization by itself.

Freeze the validated method, route, resource, body hash, operation ID, and binding
for dispatch. Do not accept new bytes or follow a new destination after the
final check. Fail closed on missing, ambiguous, changed, revoked, expired, or
unavailable authority. This follows existing owner/action fencing, not a new
cross-service atomic transaction: revocation cannot undo an upstream effect
already sent. On reconnect, retry, each new WebSocket operation, and each model
turn, repeat authorization. Long streams have bounded lifetimes and current
grant/lease checks; revoke, expiry, cancellation, or failed checks stop forwarding
and close the channel. Do not claim instantaneous rollback of in-flight bytes.

### Connection and action mapping

An accepted reference identifies a connection; it does not authorize one. The
server-owned mapping must include the following facts, with revisions supplied
by their current owners rather than trusted from guest JSON:

| Bound facts | Required decision |
| --- | --- |
| Actor issuer/subject, tenant, project, run, session, agent and turn | Intersect current Projects authority, registered session/work item, and signed run constraints. A tenant selector or guest action label cannot choose another user. |
| Accepted selection reference/hash and project/configuration/platform revisions | Use the immutable accepted model/repository/connection selection; do not rewrite hashes or choose a fallback credential. |
| Runtime instance/registration revision, execution fence, Environment lifecycle and provider/current fences, lease revision/expiry, placement/resource generations, applied network-intent generation | Require the exact current active resources and negotiated protocol profile before and after waits. A previous generation cannot dispatch through a replacement gateway or placement. |
| Connection ID/revision, credential kind, sourcing provider, exact SecretRef version where applicable | Identity resolves the current authorized connection under its existing sharing rules. The guest cannot choose an arbitrary secret or supply a token. Rotation follows the owner revision without silently changing accepted scope. |
| Upstream audience, minimum scopes/permissions, destination scheme/host/port, method, route/resource, operation and action purpose | Derive from a platform-enabled, project-narrowed operation profile. Validate request data as well as the URL: an allowed POST endpoint can still carry a forbidden resource or operation. |
| Delegated user identity versus application/workload identity | Require the configured mode and its separate consent/resource permissions. An app credential is not proof of user delegation; do not substitute a broader app identity when delegated access fails. |

Reuse `RuntimeActionRequest` / `RuntimeActionAdmission` and the current AGT guard
for `model.turn` and existing tool/execution admissions. The current action ID
set does **not** describe arbitrary GitHub or Azure effects. Extend the owning
typed tool/Source Control operation contract only for an admitted operation and
bind its resource, input hash and permission to the existing action/Policy
receipt. An `exec.shell` grant alone must not authorize all upstream operations
of a shell script. Source Control approval and merge guards still apply.
Provider-returned permissions/audiences must match the request; unexpectedly
broader tokens must not be used as a convenient fallback.

### Supported protocols and SDK limits

These are proposed profiles, not a claim of current transparent-proxy support.
One header-injection proxy cannot safely support every authentication protocol.

| Operation | Credential-less path and limits |
| --- | --- |
| Model HTTP, streaming and WebSocket | Bind the model, approved endpoint/API, input and `model.turn` admission. Inject headers only on the validated upstream connection; bound streaming frames, backpressure, cancellation, reconnect and lifetime. Each new operation on a socket needs its own action admission; opening a socket is not permission for later turns or tools. |
| Hosted Copilot SDK | SDK 1.0.18 currently receives the real token through `Models.ListAsync(gitHubToken: ...)` and `SessionConfig.GitHubToken` over native RPC. An authenticated `UriRuntimeConnection.ConnectionToken` secures SDK transport; it does not replace Copilot authentication. Empty mode disables ambient login, not session-token delivery. Do not pass an upstream token, redeemable reference, or pretend Copilot token into a guest runtime and call it injection. |
| BYOK SDK | `RuntimeByokProvider.ToSdkProvider` currently puts the key in `ProviderConfig.ApiKey` with explicit `BaseUrl`, wire API and optional headers. A gateway base URL alone does not prove that this SDK mode avoids credential consumption or direct egress. Validate the exact SDK/native pair's supported authentication and routing before admission. |
| Git smart HTTP/HTTPS | Map the exact repository and Git service: authorized `info/refs` service discovery, `git-upload-pack` reads, and separately admitted `git-receive-pack` writes. Check repository/ref/write restrictions through Source Control, not just HTTP method. Use an admitted gateway endpoint or protocol adapter; guest credential helpers must never receive a GitHub token. Handle challenges and redirects inside the trusted adapter, not via a raw redemption route. |
| GitHub API tools | Use typed, resource-bound tools through Source Control or the existing Tool & MCP role. Identity mints only the accepted installation/repository permissions. An API route that returns credentials or changes connection authority is not an ordinary repository tool. |
| Azure SDK HTTP | Bind the exact cloud, service audience and resource/action. ARM, Storage, Key Vault and other services have different token audiences; the guest cannot request arbitrary `TokenCredential.GetToken` scopes. SDKs that demand a real bearer token or signed request need trusted-side SDK execution or a specifically validated mediated transport, not a guest `DefaultAzureCredential` fallback. Challenge handling must stay within the admitted audience. |
| Generic HTTP broker-backed connection | A reviewed connection profile maps protocol, destination, authentication and operation semantics. Inject only its credential, with bounded request/response rules. Unknown endpoints, token-returning APIs, or opaque operations that cannot be checked are unsupported, not a blanket HTTP tunnel. |
| Git SSH, mTLS, pinned TLS, non-HTTP signing or authentication | Require a trusted protocol adapter holding keys and signing material outside the guest, with equivalent resource/action checks, or return explicit unsupported capability. A generic HTTP proxy cannot promise SSH signing, mTLS client-key isolation, TLS-pin compatibility, or safe credential injection into opaque encrypted traffic. |

For an incompatible SDK mode, admit a **trusted model-runtime adapter outside
the guest** using the existing AgentHost library and the same Copilot harness,
registration, action checks and observation contracts. It consumes credentials
there and returns only authorized native responses, tool requests and result
data. Guest file/shell operations cross a narrow guarded bridge; the trusted SDK
must not execute hostile workspace code in its own credential-bearing process.
This is a proposed placement/profile split, not current remote-runtime support.
If the exact SDK mode cannot preserve that separation, admission returns
`CapabilityUnavailable` (or an explicit unsupported runtime capability), with
no credential-delivery downgrade.

Preserve native session/model identity, SDK/runtime versions, abort/idle
behavior, typed tool results, usage events, and the existing receipt identities.
Cancellation uses the native abort/idle contract; failed abort prohibits another
turn or cache capture. Events remains the sole financial ledger. Forward actual
Copilot AI credits and SDK 1.0.18 `TotalNanoAiu` through Orchestrator source
receipts and existing Events acknowledgment; do not multiply weighted nano-AIU
again, infer cost from gateway bytes, or add a gateway ledger. Keep existing
deny-new-dispatch behavior at the observed turn boundary and current delivery
semantics. No all-future-source accounting-completeness gate is added here.

### Network, TLS and hostile input

The selected L3/L4 provider must force credential-bearing traffic through the
admitted L7 gateway: default deny, exact gateway/control-route access, controlled
DNS and no direct protected upstream access, even without `HTTP_PROXY`. Enforce
both IPv4 and IPv6, alternate ports, UDP/QUIC, IP literals, proxy chains and
CONNECT tunnels. Unmediated credential-free destinations, if separately allowed,
must not provide a route to a protected upstream or credential-returning broker.
Direct credential acquisition endpoints stay denied. Guest DNS choices or
`NO_PROXY` cannot change enforcement. No host networking, privileged guest
network changes, escape sockets, or permissive fallback when a layer fails.

At the gateway, normalize URLs once; reject user-info, alternate encodings,
ambiguous paths, duplicate/conflicting authentication headers, and mismatches
between operation, URL, HTTP authority/Host and TLS SNI. Never trust a guest-supplied
header as an identity assertion. Build upstream Host/SNI and authentication from
the validated profile; reject guest `Authorization`, `Proxy-Authorization`,
API-key, identity-forwarding and connection-specific credential headers instead
of forwarding or merging them. Strip hop-by-hop headers and reject ambiguous
HTTP framing/request smuggling. Bound bodies, headers, decompression, frames,
concurrency and timeouts; parse resource/action selectors before a protected
effect. Reuse upstream idempotency only where supported. An uncertain external
write is an explicit unresolved outcome, not permission for a blind retry.

Resolve DNS on the trusted side and validate every resolved address against the
profile and restricted ranges, including loopback, link-local, private networks,
metadata and Kubernetes/control endpoints. A private upstream needs an explicit
resource-specific mapping, not blanket private-CIDR access. Connect to the
validated address while verifying TLS against the approved hostname; do not
reresolve after validation without another check. Apply the same rules to
reconnects. Reject redirects by default. Any profile that admits a redirect
requires independent destination, audience and action validation, with fresh
authentication for that target; never forward the old credential automatically.

Validate upstream certificates, hostname and approved trust roots. Never disable
TLS verification. Prefer structured endpoints to TLS interception. If a reviewed
SDK/protocol profile requires interception, terminate only that profile's allowed
hosts/protocols, with a narrowly scoped trusted CA certificate installed in the
guest; its CA signing key and per-host private keys stay outside the guest. The
guest authenticates the gateway separately, and the gateway independently
validates upstream TLS. A broad trust root does not make arbitrary interception
authorized. A pinned client must use a supported explicit endpoint/adapter or
fail admission; mTLS client certificates and keys stay on the trusted upstream
leg. This proposal does not select Nginx or any new proxy product.

Responses expose approved data, not upstream authentication state. Allowlist
response headers and operation schemas; do not return bearer challenges with
secrets, auth cookies, signed credential URLs, token exchange results, or
credential-minting responses. Reject known injected credential reflection in
decoded bodies/stream frames and headers, including protocol-supported encoded
forms; sanitize errors before forwarding. Text redaction alone cannot prove an
arbitrary malicious upstream will never encode a token. Admission therefore
requires a trusted upstream and a reviewed response contract; unsupported opaque
or credential-returning operations fail closed. This boundary protects
broker-managed credentials, not every secret a user puts into allowed data.

No credentials or request authentication bytes enter receipts, logs, traces,
error strings, crash dumps, exported SDK caches, workspace files or snapshots.
Record safe operation IDs, binding revisions, admitted resource references,
Policy/source receipt references, outcomes and sanitized error codes through
existing owners. Do not log entire URLs, headers or bodies as a shortcut.
Streaming inspection must not buffer/log secrets or silently corrupt native
results; reject an unsafe response explicitly.

### Lifetime, revocation and trusted-host limits

Credential expiry is bounded by provider expiry and the current grant, connection
and lease limits, using the existing lifetime-narrowing behavior. A static API
key may have a longer upstream lifetime: limiting its wrapper's use window does
not shorten the real key's validity. Keep that value only in the protected store
and trusted operation memory; use the narrowest provider credential available.
Where a provider cannot downscope a key per operation, state that resource/action
narrowing is gateway-enforced, not an upstream token-scope guarantee. Cache only in
trusted memory; partition by issuer/subject, identity mode, tenant/project/run/
session, connection/revision/secret version, provider, audience/scopes, destination
profile, registration/execution/placement fences and policy generation. Pooling
connections or cached tokens must not mix users or sessions. Cache hits still
need fresh owner/action authorization; old revision entries are unusable.

Refresh and rotation remain Identity-owned. Revocation or changed authority denies
new dispatch, invalidates local cache entries and closes affected active channels
when observed. Use upstream revocation where the provider supports it; otherwise
an already-issued token can remain valid until its upstream expiry.
`SecretCredential.Invalidate` clears one wrapper reference, not immutable string
copies or provider tokens. Do not promise remote revocation by clearing a wrapper.
Disable credential-bearing dumps and persistent caches, minimize copies, and
invalidate on cancellation/error and after operation use.

Gateway administration, provider options, route profiles, trust roots and token
issuance are trusted control-plane operations with separate owner/operator
authorization, not guest-accessible routes. Use least-privilege service identities
and protected host-to-Identity transport; never grant the L7 provider arbitrary
Secrets access merely because it mediates traffic. The cluster/node runtime,
network enforcer, trusted host and Identity service are trust assumptions.
Compromise of those components can expose credentials. Isolation does not prevent
an authorized guest from misusing allowed data/actions, nor erase effects already
accepted upstream.

### Minimal contract changes and admission

These are bounded follow-on design changes, **not implemented fields or routes**:

| Existing surface | Proposed change |
| --- | --- |
| Projects accepted selection and `RuntimeBinding` | Add an explicit credential handling mode (`legacy-delivery` or `credential-less`) and versioned operation/runtime profile reference, separate from hosted/BYOK `ModelSourceMode`. Pin profile revision and resource generations without credentials or cached authority. Preserve old JSON/hashes; changing mode needs authorized new selection/registration. |
| `ProviderDescriptor`, `EffectiveProviderCandidate`, `ResourceNegotiation` and `ProviderResolver.PinNetworkPolicy` | Use existing required/advertised/negotiated capability sets for guest credential isolation, forced mediation and exact protocol support. Define shared capability names only for the supported adapters; profile details remain versioned adapter options. Require L7 for this mode even though L7 remains optional for other selections. No new Identity or Model provider enum. |
| `ISandboxProvider`, `SandboxProvisionedResource`, Environment lease/placement/profile reads | Return verified boundary/endpoint references and profile evidence, not credentials. Environment compares exact negotiated isolation, workspace separation, lease/fences and applied L3/L4 plus L7 generations after provisioning/readback. A descriptor or a second container is not proof. |
| `EnvironmentEgressManager` and AgentHost `/health/ready` | Include the credential mode, reachable admitted gateway, current protocol/runtime profile and isolation/network enforcement evidence in readiness. Preserve existing configured/ready phases and measured budgets. Policy-object readback alone cannot establish forced mediation; require provider conformance and placement-specific datapath evidence before credential-less dispatch. |
| `/runtime/v1/configure`, `/runtime/v1/refresh` and registration/profile contracts | In credential-less mode deliver only gateway-specific proof/reference and configuration, never model credentials or owner credentials usable at raw redemption endpoints. A trusted external model adapter keeps configure/observe/model credentials on its side and preserves the existing registration and source contracts. |
| Identity `/secrets/redeem`, `/internal/runtime/model-session/redeem`, source exchange/rotate and connection/token-mint routes | Restrict raw credentials and credential exchange to authenticated trusted service consumers plus current actor/run purpose checks. A guest gateway proof is rejected even with a valid connection ID or SecretRef. Network denial complements, not replaces, this endpoint authorization. Do not change browser sign-in or widen runtime/public roles. |
| L7 adapter operation endpoint | Add a versioned bounded request, for example `POST /egress/v1/operations`, in the trusted adapter, not the BFF. Carry operation ID, registration/profile reference, typed operation/input and gateway proof; derive identity, route, audience and credential server-side. Return sanitized data/stream and existing owner receipt references, never a redeemable token. Do not expose unrestricted CONNECT or token endpoints. |
| Runtime/action and Source Control guards | Bind each supported upstream operation to existing current action/Policy admissions and exact input/resource identity; add narrowly typed operations where the existing IDs cannot express the effect. Preserve merge approvals, native result and usage receipts, and Events ownership. |

Before accepting this mode, resolve the selected adapters and exact SDK/profile,
require the capabilities, provision, observe actual resource separation, verify
the applied network generations, and negotiate the proved capabilities. Persist
only the accepted evidence under the existing owner CAS/fences. Readiness and
resume recheck the same current evidence. Missing L7, inaccessible Identity,
unverified network enforcement, unsupported protocol/SDK mode or stale evidence
returns an explicit unavailable/denied result; it never returns legacy readiness.
The current Cilium-only implementation has no L7 adapter and cannot claim this
mode. A ready legacy AgentHost also cannot claim it.

Existing selections without this proposed field remain historical/legacy,
not silently compliant. Introduce the mode only through an explicit platform
and project-compatible profile and newly accepted binding; no automatic secret
movement, selection-hash migration or change to old accounting receipts.
The follow-on slice is bounded: first one trusted L7 operation profile, forced
placement/network admission, guest proof, and supported model-runtime integration;
then separately admit Git/Azure/other profiles with the cases below. Unknown SDK
compatibility is a validation item for its profile, not a broad release blocker.
This proposal is separate from the current P1 critical path and does not reopen
accepted native accounting or add a new P1 acceptance prerequisite.

### Planned acceptance matrix

**Unexecuted requirements.** Documentation checks do not prove runtime isolation,
SDK compatibility, token-free deployment or permission for live testing.

| Case | Positive evidence | Negative evidence |
| --- | --- | --- |
| Boundary and proof | Exact provider/placement proof reaches only the admitted gateway operation; upstream sees only the trusted-side credential. | Guest environment/files/process memory/caches/mounts/sockets/snapshots contain no upstream token or projected assertion; copied proof from another placement/session, raw redemption, IMDS, Kubernetes and token exchange all deny. |
| Current authorization | Exact user/app mode, connection, audience/scopes, resource/action and current registration/selection/lease/network generations pass before and after waits. | Change or revoke each binding during acquisition, DNS/TLS or owner waits; no authenticated upstream request follows a failed postcheck. Cache hits, allowlists, shell grants and forged owner headers cannot bypass action guards. |
| Forced mediation | Approved request succeeds via the selected L3/L4 and L7 providers with current enforcement evidence. | Direct IP/FQDN/IPv6, alternate ports, UDP/QUIC, CONNECT, proxy chains, guest DNS/NO_PROXY and gateway loss never reach protected upstreams directly. |
| HTTP/TLS safety | Approved route resolves to a validated peer, with exact Host/SNI, trusted TLS and outside-guest auth insertion. | DNS rebinding, private/metadata targets, redirects, forged auth/Host/SNI, malformed framing and invalid certificates deny; pins/mTLS do not trigger TLS-verification disablement or a guest key fallback. |
| Model/native behavior | Supported exact SDK/native/profile preserves model/session IDs, streaming, tool results, cancellation/abort-idle, actual AIUC/TotalNanoAiu and existing Orchestrator/Events receipts. | Unsupported SDK authentication or WebSocket operations deny admission; no raw token in guest RPC or cache, no new turn after failed abort, no duplicate pricing or proxy-byte ledger. |
| Git/GitHub | Exact repository smart-HTTP read and separately admitted write/API operation use least-privilege trusted tokens and Source Control guards. | Cross-repository/ref writes, unapproved merge, helper redemption, credential-returning API and unimplemented SSH/signing/mTLS modes deny explicitly. |
| Azure/generic | Exact resource/audience/action uses an admitted transport or trusted SDK adapter and reviewed response schema. | Arbitrary GetToken scope, broader app fallback, credential-mint/list-secret operation, opaque unsupported protocol and guest cloud identity acquisition deny. |
| Cache/response isolation | Partitioned cache reuse still rechecks authority; authorized data and safe receipt survive response loss under existing idempotency rules. | Cross-user/session/revision reuse, reflected/encoded credentials, auth cookies, signed secret URLs and log/trace/error/cache/snapshot leakage fail; uncertain writes are not blindly replayed. |
| Rollout and readiness | Newly accepted credential-less mode has actual negotiated isolation, L7/protocol support and current enforcement evidence. | Legacy/absent mode, two containers sharing an unprotected guest, descriptor-only proof, policy-object-only proof and missing adapters never report credential-less readiness. |

## PostgreSQL authentication and workload boundary

Runtime PostgreSQL connections use the explicit `WorkloadIdentityCredential`, no password, and `SslMode.VerifyFull`. Npgsql acquires an Entra token asynchronously through `UsePasswordProvider` for each new physical connection, using the fixed scope `https://ossrdbms-aad.database.windows.net/.default`. The runtime username is the separately bootstrapped Entra runtime role.

Ordinary host startup verifies that the `identity_broker` schema exists and that all migrations are applied; it never applies migrations or creates database roles. Only the explicit `--migrate` command runs migrations, in a separate one-shot Job with its own ServiceAccount, workload identity, connection-string key, token settings, and schema-owner role. See [PostgreSQL configuration and roles](../reference/contracts#identity-postgresql-access).

The Kubernetes source defines an HTTPS Deployment behind an HTTPS `ClusterIP` Service. It defines no Ingress, Gateway, or ingress controller. The source has not been deployed. Operators must supply the real issuer and external OIDC authority/domain, client registrations and credentials, approved immutable image, TLS certificate/key, signing PFX/password, durable key-ring storage, approved PostgreSQL administrator and roles, and the exact read-only Key Vault fixture. These inputs are placeholders or external responsibilities; this page makes no deployed-readiness claim.
