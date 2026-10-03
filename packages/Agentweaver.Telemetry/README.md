# OpenTelemetry foundation

`Agentweaver.Telemetry` composes the native OpenTelemetry .NET tracing, metrics,
and logging providers for a service. Register it once with a stable, nonempty
service name:

```csharp
services.AddAgentweaverTelemetry("agentweaver.events");
```

The registration listens to `TelemetrySignals.Activities` and
`TelemetrySignals.Metrics` and sets `service.name` on all three signals.
Consumers can add exporters using the optional tracing, metrics, and logging
builder callbacks. No exporter or network destination is registered by this
library; the [Azure Monitor integration](../Agentweaver.Telemetry.AzureMonitor/README.md)
and optional OTLP sinks compose on these callbacks. Dispose the service provider to shut down its
processors. A telemetry export failure must never govern request success.

Use low-cardinality, non-sensitive tags such as a provider seam or a bounded
outcome. Do **not** record credentials, payloads, prompts, user-supplied names,
resource URLs, or unbounded run IDs. Log structured safe event names, not
secret-bearing exception bodies. Propagation between services must be wired
at their transport boundaries; this library does not start a service, forward
headers, or persist a run journal, delivery state, or usage ledger.
