# Identity authorization boundary (0.0.0)

**Package baseline: 0.0.0.**

`Agentweaver.Identity` implements the trusted Identity authorization boundary
around the provider-neutral `ISecretRedemption` contract from
`Agentweaver.Abstractions`. It composes an existing redemption backend (for
example `Agentweaver.Secrets.AzureKeyVault`) and denies a request unless a
host-authenticated actor and an authoritative, server-owned grant permit the
*exact* request: the same actor, project, run, purpose, and SecretRef
identifier and version. Workload identity (see the Key Vault adapter) lets the
platform authenticate to Azure; it does not authorize an Agentweaver caller.
This library is that separate authorization layer.

```csharp
var actor = new TrustedActorContext(actorId, projectId, runId);
ISecretRedemption redemption = new AuthorizedSecretRedemption(actor, grantAuthority, backend);
var credential = await redemption.RedeemAsync(request, cancellationToken);
```

## Trust boundary and grant model

- `TrustedActorContext` is constructed only from a value the host already
  authenticated (for example AgentHost's run/actor validation). Nothing in it
  comes from caller-supplied request data.
- `SecretRedemptionGrant` is an immutable, server-owned record binding exactly
  one actor, project, run, purpose, and SecretRef identifier/version, with an
  `Active`/`Revoked` state and a strictly future expiry. There is no
  wildcard or admin-scope grant shape. Changing what a grant authorizes
  requires a new `GrantId` or a changed immutable `Revision`; this type exposes
  no setters. The optional constructor `revision` follows `timeProvider` and
  defaults to `"1"` for a new grant. The authority must advance it on every
  replacement, revocation or renewal, including revoke/reactivate cycles.
- `IGrantAuthority.FindGrantsAsync` is the only way to look one up. Returning
  zero candidates denies as `NoGrant`; returning more than one denies as
  `Ambiguous` rather than guessing. Implementations own revocation and expiry
  storage; this library never caches a grant across calls.

## Fail-closed redemption

`AuthorizedSecretRedemption.RedeemAsync`:

1. Rejects a request whose `RunId` does not match the trusted actor's bound
   run before any lookup.
2. Reads the grant authority fresh, validates every binding (actor, project,
   run, purpose, exact secret ID and version), and the grant's `Active` state
   and future expiry. A denial here never calls the backend.
3. Only then calls the backend's `RedeemAsync` to acquire the credential.
4. Re-reads the authority after acquisition and compares the entire immutable
   snapshot, including grant ID, revision and expiry. A replaced or revised
   grant is denied even if every request binding still matches. Revocation,
   expiry, missing or ambiguous authority results also deny.
5. Calls `SecretCredential.LimitLifetime` to narrow metadata on the same
   object to the earlier backend/grant expiry. It never reads `GetValue()` or
   copies a credential. Backend invalidation still reaches the returned object.
   A null, invalidated or expired backend result fails explicitly.

Every denial throws `SecretAuthorizationDeniedException` with a
`SecretAuthorizationDenialReason` and no other detail; its message never
contains a secret value, grant identifier, or request payload. Cancellation
is honored before the first authority read, between the authority read and
the backend call, and after the backend call returns (invalidating an already
acquired credential); a cancelled backend call propagates without authorizing
again. Every failure after a credential is acquired, including lifetime limiting,
authority errors and cancellation after the second await or final expiry check,
invalidates it before propagating. Backend/authority operational
failures (for example a Key Vault service error) propagate unchanged; they
are not swallowed or reinterpreted as a denial.

Authority implementations must return coherent immutable snapshots, with a
revision that changes for every state change; byte-identical snapshots without
a new revision cannot reveal a revoke/reactivate cycle between reads. Rechecks
are not an atomic transaction with a backend, and revocation after return does
not retroactively revoke a delivered credential. Refresh always reauthorizes.
Invalidation cannot erase value strings a consumer has already copied.

## Runtime bootstrap contracts

The library also defines a separate runtime-bootstrap contract. It does not reuse
`SecretRedemptionGrant` or change the human Broker profile.

`RuntimeRegistration` binds a server-generated runtime identifier to the delegated
actor, accepted run selection, current session/agent/turn, execution fence, and
actual Environment placement UID and generation. The registered profile fixes
the configure and observation HTTPS endpoints.
The Orchestrator execution fence and both Environment fencing generations are
separate pins. A numeric match between these different owners is not authority.

`IRuntimeRegistrationOwner` must check current authority with genuine actor
credentials supplied through `RuntimeActorAuthorization`. This protected-memory
input is not a claim or permission receipt. Each owner must authenticate it and
check its current permissions. Stored actor identifiers cannot replace authentication.
`IRuntimeBootstrapDelivery` belongs to the Environment owner. It delivers a
short-lived credential out of band to the exact registered placement. Its receipt
contains references, the configuration hash, and placement/fence evidence, not a
credential value. A missing registered delivery adapter must report unavailable.

The configure and observation credential purposes are separate. Default JSON and
diagnostic strings omit credential values. These contracts alone do not establish
a runtime channel or prove SDK provenance.

## Service and deployment limits

This library implements no wire authentication, OAuth/OpenIddict broker,
running service, or Azure deployment. It does not mint tokens, authenticate a
caller over a network, or replace `ISecretRedemption`'s existing contract or
the Key Vault adapter's composition. `IGrantAuthority` is a contract; a
durable, revocable grant store is outside this library. The Identity broker's
P0 composition (#1783) implements that authority in its owned PostgreSQL schema
and exposes the authenticated redemption boundary; deployment and Azure
acceptance remain separate. See
[docs/architecture/design/provider-seams.md#secrets](../../docs/architecture/design/provider-seams.md#secrets)
and
[docs/architecture/design/services-and-release.md](../../docs/architecture/design/services-and-release.md)
for the Identity trust boundary this composes into.

The separate Broker source candidate also contains owner-bound Remote MCP OAuth
lifecycle records and state transitions, plus authenticated redacted status,
consent preparation, and disconnect management. Consent preparation links the
Identity reference to the current immutable Environment configuration, rechecks
the returned pins, and stores the verifier only through the protected-secret
writer. It does not yet wire provider discovery, browser authorization or
callback exchange, token refresh or recovery transport, provider revocation, or
purpose-bound credential use by MCP requests. Its status reports credential use
as unavailable. The Identity package also has pure metadata validation and
S256 authorization-URI construction helpers; they do not fetch metadata,
authorize network destinations, or trigger browser redirects. See the
[Remote MCP OAuth source boundary](../../docs/architecture/identity-secrets.md#remote-mcp-oauth-source-boundary).

Run its tests with
`dotnet test tests\Agentweaver.Identity.Tests\Agentweaver.Identity.Tests.csproj --no-build --no-restore --configuration Release`
after the root README's locked restore and Release build. They use fake
in-memory `IGrantAuthority` and `ISecretRedemption` implementations; there is
no network, Azure account, or deployed dependency.
