# Two-GitHub-App production contract

- **Status:** Normative production contract; implementation is intentionally out of scope for this document.
- **Tracking:** [#939](https://github.com/sabbour/agentweaver/issues/939), part of
  [#938](https://github.com/sabbour/agentweaver/issues/938).
- **Scope:** All Web, REST, and MCP GitHub-capability surfaces; execution identities; App webhooks; cutover requirements.

## Product boundary

Microsoft Entra is the sole Agentweaver product sign-in. GitHub is an optional connected
capability implemented by exactly two GitHub Apps:

| App | Allowed credential | Prohibited capability |
| --- | --- | --- |
| Agentweaver orchestrator (Repo App) | Caller-bound user authorization for interactive repository and Copilot operations; just-in-time, downscoped installation authorization for unattended repository operations | Unscoped token selection, sandbox token delivery |
| Agentweaver orchestrator (Copilot App) | Project-bound user authorization for unattended Copilot | Installation, repository permissions, repository operations, and a private-key PEM |

The direct cutover assumes that both required GitHub Apps are correctly configured before
GitHub capabilities are activated.

## Cross-surface authorization

Implement one shared authorization predicate, consumed by REST, Web, and MCP before any
credential-mutating operation. Its policy is:

- A bind, replace, authorization begin/redeem, unattended repository configuration begin,
  or activation requires an authenticated human Entra subject: an `EntraObjectId` is
  present and the `agentweaver_internal` claim is absent.
- A shared/internal API key never satisfies that policy and can never bind or replace a
  project Copilot credential.
- Project Owner is required for project-scoped mutation. Platform Admin does not bypass
  the human-subject requirement for binding or replacement.
- Disconnect also requires a human Entra subject. A Project Owner or human Platform Admin
  may disconnect as a de-privileging operation; this exception does not grant binding or
  replacement authority.
- Audit events for these actions must never identify `agentweaver-internal` as the actor.

The same predicate and state codes are the contract for all transports. Transport adapters
may translate them to their native error shape, but may not broaden authorization.

| State code | Meaning | Client action |
| --- | --- | --- |
| `human_entra_subject_required` | Caller is not an authenticated human Entra subject | Sign in as an authorized human |
| `project_owner_required` | Human caller lacks current project Owner authority | Request project access |
| `authorization_transaction_invalid` | State, subject, cookie, PKCE, or expiry validation failed | Begin a new authorization |
| `authorization_transaction_consumed` | State was already redeemed | Begin a new authorization |
| `github_binding_unavailable` | Required current binding is absent, invalid, or revoked | Reconnect or reconfigure |
| `github_installation_unavailable` | Installation or canonical repository grant is unavailable | Install or reconfigure the Repo App |
| `github_capability_unavailable` | GitHub operation cannot safely proceed | Remediate the displayed GitHub state; do not retry with another identity |

## Authorization transaction contract

Both user-authorization flows persist a cross-replica transaction with an App kind,
purpose, initiating Entra subject, immutable project ID when applicable, expiry, opaque
return-route key, PKCE verifier material, and a hash of a random callback-cookie secret.
The browser receives only the state, its callback cookie, and the approved redirect.

The locally persisted validation, claim, and state transition are atomic. An atomic
conditional claim transitions an eligible transaction from `pending` to `redeeming`, so
only one caller can redeem it. That local atomic boundary cannot include the external
provider code exchange.

1. The authenticated Entra subject equals the stored initiating subject.
2. The callback cookie secret hashes to the stored value, compared in constant time.
3. The transaction has not expired.
4. The conditional claim succeeds.
5. For a project Copilot transaction, the initiating project ID remains unchanged and the
   caller is still a Project Owner.

The claim owner performs the OAuth code exchange using PKCE `S256`, then atomically
persists its local binding/result and transitions the transaction to `completed` or
`failed`. It returns success only after the binding is durable. If an external exchange
succeeds but local credential/binding persistence fails, the credential material is
discarded, the transaction is finalized as failed with a safe reason code, and the user
must begin a fresh authorization; the code is never replayed.

Every failure is fail-closed. There is no resume-without-cookie route. Return locations
are server-side opaque keys selected from an allowlist at transaction creation; callers
never provide a URL or path. Cross-origin and protocol-relative candidates are rejected
at storage time.

MCP browser handoff returns a distinct, non-enumerable transaction ID, browser URL, and expiry.
The transaction ID is cryptographically random and is never derived from or equal to OAuth
state. It is bound to its stored App kind and initiating Entra subject. OAuth state, PKCE
material, and callback-cookie material are persistence-only values and are never externally
serialized.
Polling is authorized only for the initiating Entra subject and exposes only
`pending`, `completed`, `failed`, or `expired`; it exposes no code, credential metadata,
other user's GitHub identity, or raw provider error.

## Binding, repository, and execution invariants

- A Copilot project ID is pinned when authorization begins, rechecked at redemption, and
  has exactly one active binding. A single GitHub account may back more than one project
  only through distinct explicit project bindings; no project may inherit another
  project's binding.
- `CredentialVersion` identifies the durable authorization grant pinned by a binding,
  authorization, or run snapshot. It is stable across access-token refresh and rotation;
  it is never an access-token value, token version, or credential-reference version.
- The Repo App's reviewed permissions are dynamically verified. The Copilot App must
  assert zero repository permissions at registration validation time and fail
  startup/diagnostics when that assertion is false.
- The Repo App installation setting **Request user authorization (OAuth) during
  installation** remains disabled and registration validation must reject an enabled
  setting.
- Retries and resumes use only their immutable run identity snapshot. Missing, changed,
  revoked, or expired snapshot material blocks the operation; resolving a current default,
  another account, a platform credential, or another project binding is forbidden.
- The purpose-bound broker is the sole token-store caller. Its only resolution shape is
  explicit `(purpose, snapshotRef)`; parameterless, ambient, current-user, and generic
  fallback resolvers are forbidden. An architecture test must fail when any non-broker
  assembly references a token-store type.

Broker purposes are `interactive_repository` and `interactive_copilot`, which use the
initiating user's Repo App authorization from the run snapshot; `unattended_repository`,
which uses the Repo App installation authorization; and `unattended_copilot`, which uses
the project Copilot App binding. Each purpose validates its matching App,
project/repository scope, permission/grant digest, and snapshot reference before issuing a
bounded capability.

## Sandbox and private-key boundary

All GitHub repository reads and writes for a run occur through backend capability adapters.
The backend either prepares the run workspace before sandbox execution or materializes
requested read results through those adapters. A trusted, internal, run-bound request
channel may carry the authorization to request one of those operations, but it carries no
GitHub credential, is not reachable or controlled by the model, and is not an alternate
token format. It is internal Agentweaver authorization only; the backend still resolves
the matching capability from the immutable run identity snapshot.

The backend performs clone, fetch, contents reads, commit, push, and pull-request actions
through a capability-specific adapter using that snapshot. It returns only sanitized,
operation-specific results to the sandbox; never a token, credential metadata, raw provider
body/error, remote URL with embedded credentials, or unrestricted repository response.

Direct sandbox GitHub network paths are unsupported and must be removed or proxied through
the backend: raw `git clone`, `git fetch`, `git push`, or credential helpers targeting
GitHub; `gh`; and direct GitHub REST or GraphQL calls. This does not authorize any fallback
token injection into a process, environment, workspace, volume, CLI argument, or internal
request.

The AgentHost, MCP service, executor, sandbox, model-controlled shell, child process,
environment, workspace, volume, CLI arguments, and preview process must not receive a Repo
App user authorization, App JWT, private key, or installation token. The Repo App PEM is
available only to the API identity through its scoped Key Vault path.

AgentHost may receive only in-memory, purpose-tagged Copilot material through its trusted
configuration channel and must enforce that purpose. It refuses startup when a GitHub
credential environment variable is present. The `GITHUB_TOKEN` and
`Providers:GitHubCopilot:GitHubToken` seeding paths are deletion requirements, not dormant
compatibility paths. Tests inspect every spawned child process environment, including git,
preview, and model-shell processes.

## App webhook contract

One Repo App webhook endpoint replaces all per-project GitHub authorization exemptions and
hooks. It performs a request body-size cap and timeout before HMAC computation, then
verifies raw body bytes with the established constant-time HMAC-SHA256 verifier before
parsing. Signature failure returns `401` without body detail and logs only the delivery ID
and a reason category.

Verification accepts current and previous webhook secrets in constant time for a bounded
rotation interval. GitHub does not provide a signed delivery timestamp, so Agentweaver does
not assert an unenforceable delivery-age test. Persisted, non-null `X-GitHub-Delivery`
uniqueness is claimed before processing for lifecycle and automation deliveries alike; HMAC
validation remains mandatory. Routing is an authorization decision: a
delivery affects only a project with both the persisted installation ID and canonical
`repository.id`; `full_name` is display data only.

## Forward-only cutover contract

The cutover destructively removes the legacy OAuth/device-flow implementation, endpoints,
MCP aliases, configuration, secret paths, records, and documentation. It has no
compatibility lane, runtime switch, recovery lane, or migration `Down` operation that
recreates legacy credential storage. It does not make assertions about provider or platform
records outside Agentweaver control.

A residual-reference CI test scans shipping assemblies, configuration and deployment
manifests, MCP manifests, and published documentation for legacy OAuth/device-flow
identifiers, secret names, and endpoints. The test is required before the cutover can ship.

## Audit event contract

Credential-mutating actions, authorization transaction completion, broker capability
issuance/denial, project disconnect, installation/grant changes, webhook lifecycle
processing, activation/invalidation, and run snapshot validation emit a typed,
allowlisted audit event. Each event records an Entra subject and actor classification,
action enum, resource ID, App/purpose where applicable, outcome enum, closed reason-code
enum, correlation ID, timestamp, and credential version or digest where applicable.

For credential mutation, the actor classification must be a human Entra subject;
`agentweaver-internal` is invalid. A webhook event identifies the GitHub App webhook
delivery rather than a human actor. Audit fields never include tokens, refresh material,
PEMs, App JWTs, repository contents, raw provider requests/responses/errors, or arbitrary
exception text. Serialization-level tests reject emitted payloads containing `ghu_`,
`ghs_`, `ghp_`, `github_pat_`, PEM markers, or JWT-shaped strings.

## Production callback registration action

Before production activation, the operator must register the production callback URLs for
both Apps. The exact URLs must be taken from the implemented, deployed public API origin
and the final App-specific endpoint mappings. Do not derive them from the current legacy
`/auth/github/callback` convention or from placeholder origins; the implementation and
target public origins must be reconciled first.

## Required verification

Implementation must prove the shared human-subject predicate on REST, Web, and MCP; all
transaction binding and replay conditions; Owner recheck and one-binding exclusivity; zero
Copilot-App repository permissions; broker-only token-store access; webhook limits,
rotation, replay, and canonical routing; removal of pod credential paths; and typed audit
schema/redaction enforcement.
