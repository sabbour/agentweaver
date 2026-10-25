# Session material deployment validation

Events & Sessions is the only service in this deployment path that connects to
Blob for session material. Its runtime ConfigMap provides one HTTPS container
URI. The Events host uses its projected workload identity credential to create
the Blob client. Runtime clients send material to the Events owner over HTTP;
they do not receive Blob account keys, SAS values, connection strings, or
container credentials.

The deployment composes that setting through the
[Events runtime ConfigMap](../../../deploy/k8s/runtime-configs/events-and-sessions-runtime-config.json),
the [Events Deployment](../../../deploy/k8s/base/workloads/events-and-sessions-deployment.json),
and the root [Kustomize configuration](../../../deploy/k8s/kustomization.yaml).
Kustomize supplies the Events ServiceAccount client ID from the same runtime
ConfigMap. The Deployment selects that ServiceAccount and carries the workload
identity label.

The compiled storage template defines `session-material` as a private container.
The Events managed identity receives Storage Blob Data Contributor only at that
container scope. The
[Events network policy](../../../deploy/k8s/base/events-and-sessions-network.yaml)
allows the configured Blob, PostgreSQL, DNS, and private owner destinations.
These source relationships describe intended composition, not an applied role
assignment or a proven network path.

The PostgreSQL maintenance Job runs with its separate migration ServiceAccount
and connection. Its plan includes Events migration 010 and the runtime grants
for the material inventory. The runtime Deployment continues to use the Events
runtime identity and does not consume the migration connection or plan settings.

Readiness and provider catalogs are not session-material storage evidence.
Health can report that the service and database schema are ready without
performing a typed Blob write, read, restore, or recovery. This validation
checks rendered source and compiled templates only. It does not log in to Azure,
prove deployed permissions or private network reachability, or exercise
session-material persistence.
