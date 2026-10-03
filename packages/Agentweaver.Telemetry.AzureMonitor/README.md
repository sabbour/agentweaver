# Azure Monitor telemetry integration

`Agentweaver.Telemetry.AzureMonitor` composes the supported Azure Monitor
OpenTelemetry SDK exporters for traces, metrics, and logs atop the lower
`Agentweaver.Telemetry` foundation. The connection string must come from the
service's trusted configuration; never store it in source control or log it.
An injected `TokenCredential` is optional for Microsoft Entra authentication.
The library does not create a credential, Azure resource, or running service.

```csharp
services.AddAgentweaverAzureMonitorTelemetry(
    "agentweaver.events",
    configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
        ?? throw new InvalidOperationException("Azure Monitor is not configured."),
    credential: serviceCredential);
```

Add `using Agentweaver.Telemetry.AzureMonitor;`. Supply `serviceCredential`
from the hosting service's identity configuration (or omit it when the
connection string alone is used). A nonempty service name and a connection
string containing a valid instrumentation key are required. Missing, duplicate,
or malformed fields fail registration; the Azure SDK validates its own endpoint
and other exporter settings when the provider starts.

Tracing, metrics, and logging callbacks can add independent OpenTelemetry
exporters, including an optional OTLP sink configured by the caller.
`configureExporter` can supply SDK settings (for example transport and
offline storage); the explicit connection string and credential arguments
take precedence. Dispose the service provider at shutdown to flush SDK
processors. Export failures are non-authoritative: never use telemetry as
authorization, accounting, durable event delivery, or proof of ingestion.
No-network tests use an injected HTTP transport and credential; deployed
Azure validation requires a separately provisioned integration environment.
