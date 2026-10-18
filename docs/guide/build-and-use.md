# Build and use the foundation libraries

Agentweaver's .NET solution contains contracts and adapters for identity, providers,
persistence, objects, and telemetry. Building these libraries does not start a
deployed Agentweaver platform. The Identity Broker is a source-level service
candidate, and Foundation Probe is an acceptance executable rather than a product
service.

## Prerequisites

Install the .NET 10 SDK version selected by `global.json` and Node.js 24. You need
a Docker-compatible container engine only when you run PostgreSQL integration
tests. Those tests use Testcontainers and must be able to pull the test database
image.

## Restore and build the solution

Run the commands from the repository root:

1. Restore the locked .NET dependencies:

   ```powershell
   dotnet restore Agentweaver.slnx --locked-mode
   ```

   The command restores the dependencies recorded in the lock files.

2. Build the solution in Release mode:

   ```powershell
   dotnet build Agentweaver.slnx --no-restore --configuration Release
   ```

   The build compiles the solution without changing the restored dependency set.

3. Validate release metadata:

   ```powershell
   npm run release:validate
   ```

   This command uses Node.js built-in modules. It does not install packages or
   publish artifacts.

## Use the foundation libraries

Use the provider libraries to describe and resolve available implementations.
`ProviderCatalog.Create` builds an immutable catalog. `ProviderResolver` resolves
and pins a selection after resource negotiation; it does not provision the
selected resource.

Use the Identity library to check server-owned grants before and after secret
acquisition. The Azure Key Vault adapter reads the exact `SecretRef` version
using an injected credential or an explicitly configured AKS workload identity.
The adapter does not grant access by itself.

Use the PostgreSQL library for transactional outbox and inbox operations. Call
`EnqueueAsync` in the same transaction as the domain change. Call `AdmitAsync`
in the same transaction as the consumer effect so the state change and message
admission remain atomic.

Use `AzureBlobObjectStore` to implement `IObjectStore` for opaque platform
objects. The container and caller authorization must already exist before the
adapter is used.

Register telemetry with `AddAgentweaverTelemetry`. Add
`AddAgentweaverAzureMonitorTelemetry` only when the host provides trusted
exporter configuration.

For exact API signatures, see [contracts and endpoints](../reference/contracts).
For package preparation, see [component releases](../reference/releases).
