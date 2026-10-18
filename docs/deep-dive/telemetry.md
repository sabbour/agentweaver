# Telemetry

Use this page to understand how Agentweaver describes service execution with
OpenTelemetry and how those diagnostic signals differ from committed product
state. A trace can help you find a failure, but it does not prove that an
operation committed.

## Understand signal ownership

Agentweaver separates signal creation from export. OpenTelemetry is the
framework used to create and process traces, metrics, and logs. The base
package registers those signals; the Azure Monitor package adds an explicit
destination. A host can use the base package without opening a
telemetry network connection.

The run journal, usage ledger, owner receipts, and checkpoints remain durable
domain state. A trace is diagnostic evidence, not proof that a workflow gate
or transaction completed.

| Source | Responsibility |
| --- | --- |
| `TelemetrySignals.cs` | Shared `ActivitySource` and `Meter` |
| `TelemetryServiceCollectionExtensions.cs` | OpenTelemetry registration |
| `AzureMonitorTelemetryExtensions.cs` | Exporter composition and destination validation |
| `tests/Agentweaver.Telemetry.Tests/` | Signal registration tests |
| `tests/Agentweaver.Telemetry.AzureMonitor.Tests/` | Exporter composition and fake-transport tests |

## Register and emit signals

`TelemetrySignals.Name` is `Agentweaver`. Hosts subscribe to the stable
`TelemetrySignals.Activities` tracing source and `TelemetrySignals.Metrics`
meter instead of scanning service-specific types.

`AddAgentweaverTelemetry` requires a nonempty service name and attaches it to
traces, metrics, and logs. It registers the shared source and meter, then
invokes optional host callbacks for each signal.

The base registration includes no exporter. It does not automatically
instrument every HTTP request or propagate context across every transport.
Hosts configure instrumentation and propagation at their own boundaries.

Signal producers control tags, metric dimensions, and log fields. Keep
credentials, secret values, raw content, and authorization material out of
them. The base logging registration sets `IncludeFormattedMessage`,
`IncludeScopes`, and `ParseStateValues` to `false`. Host callbacks can change
these defaults; they do not redact arbitrary caller content.

1. Register the base telemetry package with the host's service identity.
2. Create an activity or record a measurement through the shared instruments.
3. Let OpenTelemetry process the signal with the registered resource and host
   configuration.
4. Export the signal only when the host has registered an exporter.

This work runs alongside request and domain processing. It does not add a
database commit or journal acknowledgment.

## Send signals to Azure Monitor

Before adding the Azure Monitor exporter, configure one trusted connection
string and, when required, supply the host-selected `TokenCredential`.

`AddAgentweaverAzureMonitorTelemetry` builds on the base registration and adds
trace, metric, and log exporters. It rejects an empty connection string,
malformed fields, duplicate field names, and a missing or invalid GUID
instrumentation key. The extension applies exporter customization first, then
sets the selected connection string and credential. The customization callback
cannot replace those final destination settings.

The extension does not create an Azure resource or credential. Bicep owns
infrastructure provisioning; the host owns credential selection. Exporter
configuration alone does not prove ingestion. See the
[runtime configuration reference](../reference/runtime-configuration.md) for
supported host settings.

## Diagnose telemetry separately

Invalid service identity or connection-string configuration fails
registration. Export failures do not determine domain request success.
Investigate exporter transport and ingestion separately from owner transaction
failures.

When investigating a run, use telemetry to locate a failed boundary, then read
the durable owner receipt or journal for committed state. Do not infer zero
usage from a missing metric or completion from a successful span.

Azure Monitor tests use controlled transports and do not contact Azure. Live
ingestion requires separate source-bound operational evidence.

## Related content

- [Telemetry architecture](../architecture/telemetry.md)
- [Persistence](./persistence.md)
- [Infrastructure and deployment](./infra-deployment.md)
- [Testing strategy](./testing-strategy.md)
