# Telemetry

Use this concept to understand how Agentweaver emits application telemetry.
Telemetry is operational data such as traces, metrics, and logs; it is
separate from the durable Events & Sessions journal.

`Agentweaver.Telemetry` registers native OpenTelemetry tracing, metrics, and logging. It sets the service name on each signal and exposes callbacks for host exporters.

The base library registers no exporter and opens no network destination. Each
host chooses exporters and controls context propagation at its transport
boundaries.

`Agentweaver.Telemetry.AzureMonitor` adds opt-in Azure Monitor exporters for traces, metrics, and logs. The host supplies a trusted connection string and an optional `TokenCredential`.

The exporter does not create Azure resources or credentials. Export failure does not determine request success.

The Foundation Probe composes Azure Monitor for its source-bound acceptance trace. Probe tests use fake transport and do not prove Azure ingestion.

The [Telemetry integration](./overview) is one foundation library boundary. It is not the run journal or durable event store.
