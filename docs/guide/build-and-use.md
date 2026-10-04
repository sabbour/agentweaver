# Build and use

## Requirements

Use the .NET 10 SDK selected by `global.json`, Node.js 24, and a Docker-compatible engine.

PostgreSQL integration tests start a disposable Testcontainers database. They require permission to pull its test image.

## Restore and build

Run these commands from the repository root:

```powershell
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
npm run release:validate
```

The release validator uses Node.js built-in modules. It does not install packages.

## Use the foundation libraries

The provider packages define contracts and resolution. `ProviderCatalog.Create` builds an immutable catalog. `ProviderResolver` resolves and pins selections after resource negotiation.

The Identity library checks server-owned grants before and after secret acquisition. The Azure Key Vault adapter reads the exact `SecretRef` version with an injected credential or explicit AKS workload identity.

The PostgreSQL library provides transactional outbox and inbox operations. Call `EnqueueAsync` in the same transaction as domain state. Call `AdmitAsync` in the same transaction as consumer effects.

`AzureBlobObjectStore` implements `IObjectStore` for opaque platform objects. The container and caller authorization already exist before adapter use.

Register telemetry with `AddAgentweaverTelemetry`. Add `AddAgentweaverAzureMonitorTelemetry` when the host supplies trusted exporter configuration.

These libraries do not start a product platform. The Identity broker is an unpublished service candidate. The Foundation Probe is an acceptance executable, not a product service.

Read [contracts and endpoints](../reference/contracts) for exact signatures and [component releases](../reference/releases) for package preparation.
