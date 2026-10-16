# Telemetry

## The mental model

Telemetry describes service execution. It does not decide whether an operation committed.

Agentweaver separates signal creation from export. The base package registers OpenTelemetry tracing, metrics, and logging. The Azure Monitor package adds an explicit destination. A host can use the base package without opening a telemetry network connection.

The run journal, usage ledger, owner receipts, and checkpoints remain durable domain state. A trace is useful for diagnosis, not proof that a workflow gate or transaction completed.

## Where this lives

- `packages/Agentweaver.Telemetry/TelemetrySignals.cs` — shared `ActivitySource` and `Meter`.
- `packages/Agentweaver.Telemetry/TelemetryServiceCollectionExtensions.cs` — native OpenTelemetry registration.
- `packages/Agentweaver.Telemetry.AzureMonitor/AzureMonitorTelemetryExtensions.cs` — exporter configuration and destination validation.
- `tests/Agentweaver.Telemetry.Tests/` — signal registration tests.
- `tests/Agentweaver.Telemetry.AzureMonitor.Tests/` — exporter composition and fake-transport tests.

## Core concepts and invariants

### One source name, explicit service identity

`TelemetrySignals.Name` is `Agentweaver`. `TelemetrySignals.Activities` and `TelemetrySignals.Metrics` expose the shared tracing source and meter. Hosts subscribe to this stable source rather than scanning every service-specific type.

`AddAgentweaverTelemetry` requires a nonempty service name and attaches it to traces, metrics, and logs. The call registers the shared source and meter, then invokes optional host callbacks for each signal.

The base registration includes no exporter. It does not automatically instrument every HTTP request or propagate context across every transport. Hosts configure instrumentation and propagation at their own boundaries.

### Logging defaults keep extra context out

The base logging registration sets `IncludeFormattedMessage`, `IncludeScopes`, and `ParseStateValues` to `false`. Host callbacks can change logging behavior. These defaults do not redact arbitrary content supplied by a caller.

Signal producers still control their tags, metric dimensions, and log fields. Keep credentials, secret values, raw content, and authorization material out of those fields.

## Signal lifecycle

1. The host registers the base telemetry package with its service identity.
2. Code creates an activity or records a measurement through the shared instruments.
3. OpenTelemetry processes the signal with the registered resource and host configuration.
4. An exporter sends it only when the host has registered one.

This flow runs alongside request and domain processing. It does not add a database commit or journal acknowledgment.

## Azure Monitor composition

`AddAgentweaverAzureMonitorTelemetry` builds on the base registration. It adds trace, metric, and log exporters using one trusted connection string and the optional supplied `TokenCredential`.

The extension rejects an empty connection string, malformed fields, duplicate field names, and a missing or invalid GUID instrumentation key. It applies exporter customization first, then assigns the selected connection string and credential. The customization callback cannot replace those final destination settings.

The extension creates no Azure resource or credential. Infrastructure provisioning belongs to Bicep; credential selection belongs to the host. Exporter configuration alone is not evidence of ingestion.

## Failure behavior and diagnosis

Invalid service identity or connection-string configuration fails registration. Export failures do not determine domain request success. Diagnose export transport and ingestion separately from owner transaction failures.

When investigating a run, use telemetry to locate the failed boundary, then read the durable owner receipt or journal for committed state. Do not infer zero usage from a missing metric or a completed operation from a successful span.

The Azure Monitor tests use controlled transports. They verify exporter behavior without contacting Azure. Live ingestion requires separate source-bound operational evidence.

## Related pages

- [Telemetry architecture](../architecture/telemetry.md)
- [Persistence](./persistence.md)
- [Infrastructure and deployment](./infra-deployment.md)
- [Testing strategy](./testing-strategy.md)
