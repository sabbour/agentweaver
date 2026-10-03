# Azure Key Vault Secrets adapter (0.1.0)

`Agentweaver.Secrets.AzureKeyVault` implements the provider-neutral
`ISecretRedemption` contract for the **trusted Identity/control-plane singleton**.
Configure a root HTTPS Azure Key Vault URI in `AzureKeyVaultConfiguration`, then
construct `AzureKeyVaultSecretRedemption` with either an injected `SecretClient`
targeting that vault or an injected `TokenCredential`. For AKS host composition,
call `AzureKeyVaultSecretRedemption.CreateWithWorkloadIdentity(configuration,
identityOptions, clientOptions)` with native `WorkloadIdentityCredentialOptions`
whose `TenantId`, `ClientId`, and absolute `TokenFilePath` are all explicitly set.
The host obtains these settings from its trusted deployment configuration and
mounts the projected service-account token file; do not embed the assertion in
options, source, descriptors or logs. For example:

```csharp
var options = new WorkloadIdentityCredentialOptions
{
    TenantId = tenantId,
    ClientId = clientId,
    TokenFilePath = projectedTokenFilePath,
};
using var redemption = AzureKeyVaultSecretRedemption.CreateWithWorkloadIdentity(
    new AzureKeyVaultConfiguration(vaultUri), options);
```

There is no `DefaultAzureCredential`, developer/CLI identity, client secret or
fallback in this composition. Construction validates configuration but does not
acquire a token. The Azure SDK handles acquisition, caching and projected-file
refresh when a credential is needed; it does not guarantee immediate refresh at
file replacement or authorize the caller. An unavailable file or rejected
exchange produces a typed, safe failure instead of exposing identity assertions
or raw server error bodies. Failed identity POST responses are replaced with a
fixed error body before Azure Identity can emit its own diagnostics; the HTTP
status is retained. This guards against an authority echoing the projected
assertion in an OAuth error, even when HTTP content logging is disabled. The
library does not require a connection string.
It supports Azure public, US Government and China vault DNS suffixes and validates
the vault name.
When injecting an existing `SecretClient`, its host must leave Azure SDK response
content logging disabled; the adapter cannot inspect an already constructed
client's diagnostic options. Credential-construction rejects options that enable
Key Vault response content logging; the workload-identity path also rejects
identity assertion content logging.

The request's `SecretRef.Id` is the Key Vault secret name and its `Version` is
passed to `GetSecretAsync` without a latest-version fallback. The SDK returns
text values; an application storing encoded bytes is responsible for encoding
and decoding them outside this adapter. A returned `SecretCredential` expires
after at most five minutes, or earlier if the vault secret expires. Disposing the
adapter invalidates its outstanding credentials; the injected SDK client and
credential remain owned by the host. Never log or persist `GetValue()` results.

**Trust boundary:** The adapter consumes `Purpose` and `RunId` as part of the
contract but does not authorize either. A trusted caller must validate the run,
purpose and access rights before calling it and again when refreshing a
credential. Do not register this adapter in sandboxes or project-selected
providers, place values in provider descriptors, bindings, databases, workspace
files or snapshots, or enable Azure SDK content logging for secrets. This
library does not implement Identity, OAuth, a credential-delivery service, or
cloud deployment.

Run its no-account transport tests with
`dotnet test tests\Agentweaver.Secrets.AzureKeyVault.Tests\Agentweaver.Secrets.AzureKeyVault.Tests.csproj --no-build --no-restore --configuration Release`
after the root README's locked restore and Release build. They use the actual
Azure SDK HTTP pipelines through in-memory handlers and generated nonsensitive
projected token files; injected-credential cases use a fake token credential.
Live workload identity, vault permissions and network access require a future
deployed-service integration test.
