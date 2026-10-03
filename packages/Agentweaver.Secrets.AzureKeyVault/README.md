# Azure Key Vault Secrets adapter (0.1.0)

`Agentweaver.Secrets.AzureKeyVault` implements the provider-neutral
`ISecretRedemption` contract for the **trusted Identity/control-plane singleton**.
Configure a root HTTPS Azure Key Vault URI in `AzureKeyVaultConfiguration`, then
construct `AzureKeyVaultSecretRedemption` with either an injected `SecretClient`
targeting that vault or an injected `TokenCredential`. The host supplies the
credential (for example, AKS workload identity via Azure Identity); this library
does not embed credentials or require a connection string. It supports Azure
public, US Government and China vault DNS suffixes and validates the vault name.
When injecting an existing `SecretClient`, its host must leave Azure SDK response
content logging disabled; the adapter cannot inspect an already constructed
client's diagnostic options. The credential-construction overload rejects options
that enable content logging.

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
`dotnet test tests\Agentweaver.Secrets.AzureKeyVault.Tests\Agentweaver.Secrets.AzureKeyVault.Tests.csproj --configuration Release`.
They use the actual Azure SDK HTTP pipeline with an in-memory handler and fake
token credential. Live workload identity, vault permissions and network access
require a future deployed-service integration test.
