# Telemetry

Telemetry helps you locate a failing boundary; owner state tells you what
committed. Traces, metrics and logs are operational diagnostics, not the
Events & Sessions journal, usage ledger or a workflow approval.

## Signals and destinations

`Agentweaver.Telemetry` registers native OpenTelemetry tracing, metrics, and logging. It sets the service name on each signal and exposes callbacks for host exporters.

The base library registers no exporter and opens no network destination. Each
host chooses exporters and controls context propagation at its transport
boundaries.

`Agentweaver.Telemetry.AzureMonitor` adds opt-in Azure Monitor exporters for traces, metrics, and logs. The host supplies a trusted connection string and an optional `TokenCredential`.

The exporter does not create Azure resources or credentials. Export failure does not determine request success.

The Foundation Probe composes Azure Monitor for its source-bound acceptance trace. Probe tests use fake transport and do not prove Azure ingestion.

The [Telemetry integration](./overview) is one foundation library boundary. It is not the run journal or durable event store.

## Trade-offs and limits

Keeping export optional lets a host create signals without a network
destination. It also makes instrumentation, propagation, destination and
sensitive-field handling host responsibilities. Missing telemetry does not
mean zero usage, and a successful span does not prove a durable commit.

## Use this knowledge

The [telemetry deep dive](../deep-dive/telemetry.md) explains registration,
signal flow, exporter validation and failure isolation. Use the
[runtime configuration reference](../reference/runtime-configuration.md) for
host settings, not this conceptual view.
