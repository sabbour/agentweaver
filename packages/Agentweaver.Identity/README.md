# Identity authorization boundary (1.0.0)

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
  requires a different grant (a new `GrantId` or a replacement entry in the
  authority's store); this type exposes no setters.
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
4. Re-reads the authority again after that `await` returns. A grant revoked,
   expired, or replaced with a binding that no longer matches this exact
   request during the backend call invalidates the just-acquired credential
   and throws instead of returning it.
5. Clamps the returned credential's expiry to the grant's expiry when the
   backend would otherwise issue a longer-lived one, by invalidating the
   original and issuing a new `SecretCredential` carrying the same opaque
   value. The clamp reads `GetValue()` only to repackage it; the value is
   never logged, serialized, or exposed beyond that reassignment.

Every denial throws `SecretAuthorizationDeniedException` with a
`SecretAuthorizationDenialReason` and no other detail; its message never
contains a secret value, grant identifier, or request payload. Cancellation
is honored before the first authority read, between the authority read and
the backend call, and after the backend call returns (invalidating an already
acquired credential); a cancelled backend call propagates without authorizing
again. Backend/authority operational
failures (for example a Key Vault service error) propagate unchanged; they
are not swallowed or reinterpreted as a denial.

## What this does not do

This library implements no wire authentication, OAuth/OpenIddict broker,
running service, or Azure deployment. It does not mint tokens, authenticate a
caller over a network, or replace `ISecretRedemption`'s existing contract or
the Key Vault adapter's composition. `IGrantAuthority` is a contract; a
durable, revocable grant store is separate, future work. See
[docs/architecture/design/provider-seams.md#secrets](../../docs/architecture/design/provider-seams.md#secrets)
and
[docs/architecture/design/services-and-release.md](../../docs/architecture/design/services-and-release.md)
for the Identity trust boundary this composes into.

Run its tests with
`dotnet test tests\Agentweaver.Identity.Tests\Agentweaver.Identity.Tests.csproj --no-build --no-restore --configuration Release`
after the root README's locked restore and Release build. They use fake
in-memory `IGrantAuthority` and `ISecretRedemption` implementations; there is
no network, Azure account, or deployed dependency.
