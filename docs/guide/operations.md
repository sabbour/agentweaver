# Operations Guide

This guide covers the day-to-day operational procedures for running and releasing Agentweaver in production on AKS.

For provisioning, image builds, deployment, and verification, use the root
package scripts (`pnpm run` preferred; `npm run` is equivalent). The
[AKS deployment runbook](./deployment-aks.md) defines the canonical sequence
and individual-step instructions.

## Release Process

Agentweaver uses Changesets and the protected
`dev → release/vX.Y.Z → main` flow. `release:prepare` generates the version
mirrors and changelog on the release branch.

From the exact promoted `main` SHA, publication and deployment are independent:

```bash
npm run release:publish
npm run azure:deploy-from-release -- vX.Y.Z
```

`release:publish` creates the annotated tag and GitHub Release only.
`azure:deploy-from-release` requires that existing published tag, imports or
rebuilds its images, deploys them, and verifies the live environment. By
default it imports the images already published for that tag by
`.github/workflows/publish-images.yml` instead of rebuilding them from source —
this is the fastest way to deploy an already-tagged release and never touches
cluster/ACR/Postgres/identity infrastructure. Add `--image-source acr-build`
to rebuild the images from source into ACR instead:

```bash
npm run azure:deploy-from-release -- vX.Y.Z --image-source acr-build
```

For the normal first shipment to the default environment:

```bash
npm run azure:release
```

This composes publication and deployment. It never calculates or commits a
version. See [RELEASING.md](../../RELEASING.md) for preparation and recovery, and
the [Agentweaver changelog skill](../../.copilot/skills/agentweaver-changelog/SKILL.md)
for the full fragment lifecycle, recovery commands, and changelog/release-notes rules.

### Image tags

| Tag | Meaning |
|---|---|
| `vX.Y.Z` | Immutable semver release tag |
| `<git-sha>` | Short SHA from ad-hoc builds (CI / development) |

> **Note:** `latest` and `latest-release` are rejected by the variable scripts. Select a release tag or an immutable commit tag.

## Verifying a deployed version

### Check the running image tag

```bash
kubectl get deployment agentweaver-api \
  --namespace agentweaver \
  --output jsonpath='{.spec.template.spec.containers[0].image}'
```

### Check OCI image labels (version + commit SHA)

```bash
az acr repository show-tags \
  --name agentweaverregistry \
  --repository agentweaver-api \
  --orderby time_desc \
  --top 5
```

Or inspect labels on the image:

```bash
# Pull the manifest (no local pull needed)
az acr manifest show \
  agentweaverregistry.azurecr.io/agentweaver-api:v0.6.1
```

Each image is built with the following OCI labels:

| Label | Value |
|---|---|
| `org.opencontainers.image.version` | Semver tag (e.g. `v0.6.1`) |
| `org.opencontainers.image.revision` | Full git commit SHA |

## Rolling back a release

To roll back to a previous published version, check out its exact tag commit and
deploy that release:

```bash
npm run azure:deploy-from-release -- v0.6.0
```

All previous semver tags remain in ACR and are not deleted by the release process.

## Manual image builds (development)

To build and push images without cutting a release (e.g. for a staging
environment), use `azure:deploy-from-local`, which builds using the current git SHA as
the tag and then redeploys:

```bash
npm run azure:deploy-from-local
```

To deploy another committed ref without switching the current checkout:

```bash
npm run azure:deploy-from-commit -- <sha-or-ref>
```

## Observability notes

- Token and AIC usage data now lives in **Application Insights / Azure Monitor**, not in the application database.
- The project dashboard throughput chart and agent leaderboard read from `GET /api/projects/{id}/metrics`, which proxies App Insights KQL.
- Configure `APPLICATIONINSIGHTS_CONNECTION_STRING` **and** a Log Analytics workspace id (`APPLICATIONINSIGHTS_WORKSPACE_ID` or `ApplicationInsights:WorkspaceId`) unless your connection string already embeds `WorkspaceId`.
- If App Insights is not configured, or no workspace id can be resolved, the metrics endpoint returns empty arrays so the dashboard degrades gracefully.

### AgentHost assembly recovery diagnostics

Assembly RAI and Build & Test use the coordinator run's warm-pool AgentHost. Recovery is
bounded and reason-specific:

- `agenthost_configure_copilot_token_refreshed` means AgentHost explicitly rejected the
  configured Copilot credential, the API rotated that exact user/account scope, and Build &
  Test will recreate the one-time-configured pod once.
- `agenthost_configure_copilot_unauthorized` means no different credential could be
  produced. The failure is not retried; the submitting user must repair GitHub/Copilot
  authorization.
- `agenthost_pod_reaped` triggers one inline pod redispatch for a non-terminal run.
  `agenthost_pod_reaped_recovery_exhausted` means the replacement was also unavailable and
  stops further automatic redispatch.

Logs include `RunId`, pod name, reason, recovery action, and bounded attempt counts. Token
values are never logged. These reasons distinguish pod lifecycle churn from credential
failure so operators should not treat either path as a generic retryable AgentHost error.

## Diagnosing agent turn infrastructure failures

Agent turns executed through AgentHost use structured terminal reasons.
`agent_turn_internal_error` with `retryable: true` is the fallback for an unstructured
`run.failed`, an unsupported or unset A2A event, or a pod bridge turn that throws before
emitting a structured terminal. Other A2A exceptions use `a2a_transport_failure`, whose
retryability follows the transport error. A clean stream that ends without
`agent.turn.end` uses retryable `agent_host_turn_incomplete`. In the collective Build &
Test stage, the assembly reason prefixes the applicable reason with `build_test_infra_`,
for example `build_test_infra_agent_host_turn_incomplete`.

This fallback does not hide more specific outcomes. Caller cancellation remains
cancellation, and typed timeouts or failures retain their original error code and
retryability. A retryable terminal means the workflow may safely consider a bounded
retry or redispatch; it never converts the interrupted turn into a success.

To investigate:

1. Inspect the persisted run events with `GET /api/runs/{id}/events` and record the
   `errorCode`, `retryable`, message, and diagnostic fields.
2. Correlate the run id with worker and AgentHost logs to determine whether the pod,
   transport, or turn failed.
3. Retry or redispatch through the normal run/coordinator controls only after confirming
   that the task is still valid.

Diagnostics in the run event are deliberately bounded, flattened to one line, and
credential-redacted. They are safe context for triage, not a replacement for
restricted server-side logs.

## Related scripts

| Command | Purpose |
|---|---|
| `npm run azure:release` | Full semver release (see above) |
| `npm run release:publish` | Create the annotated tag and GitHub Release without deploying |
| `npm run azure:deploy-from-release -- vX.Y.Z [--image-source acr-build]` | Deploy an existing published release (import already-published GHCR images by default, or rebuild from source) |
| `npm run azure:provision-infra` | Provision/redeploy AKS, identity, monitoring, OAuth signing key, and PostgreSQL |
| `npm run azure:deploy-from-local` | Build, push, and verify images in ACR, then redeploy and cycle the warm pool |
| `npm run azure:deploy-from-commit -- <sha-or-ref>` | Deploy an arbitrary exact commit through a temporary detached worktree |
| `npm run azure:verify` | Verify the current deployment |

Use `pnpm run` in place of `npm run` if pnpm is your selected package runner.
The runbook's [individual-step section](./deployment-aks.md#running-an-individual-step)
shows how to rerun one step.

## Observability

Agentweaver ships with end-to-end telemetry using **Azure Monitor OpenTelemetry Distro** (Application Insights) and **AKS Managed Prometheus**.

### Provisioning monitoring resources

Monitoring is provisioned as part of `npm run azure:provision-infra`. To rerun only
that step, see the runbook's [individual-step section](./deployment-aks.md#running-an-individual-step)
(`scripts/azure/steps/15-provision-monitoring.mjs`).

This creates:
- A **Log Analytics workspace** (`agentweaver-logs`)
- A **workspace-based Application Insights** resource (`agentweaver-insights`) — workspace-based is required for the Agents (Preview) view
- Stores the connection string as `appinsights-connection-string` in Key Vault
- Enables **AKS Managed Prometheus** on the cluster

### Finding the Application Insights resource

1. Open the [Azure Portal](https://portal.azure.com)
2. Navigate to your resource group (`agentweaver-rg` by default)
3. Select the **Application Insights** resource named `agentweaver-insights`

### Using the Agents (Preview) view

The **Agents (Preview)** view in Application Insights shows GenAI-specific telemetry including agent runs, token usage, and model calls.

1. In the Application Insights resource, select **Agents (Preview)** from the left menu
2. Use the time range picker to scope your investigation
3. Filter by agent using the `gen_ai.agent.name` attribute — this maps to the configured agent name in the squad definition (e.g. `morpheus`, `seraph`)

Key span attributes emitted by Agentweaver:

| Attribute | Description |
|---|---|
| `gen_ai.agent.name` | Squad agent name |
| `gen_ai.agent.id` | Agent identifier |
| `gen_ai.usage.input_tokens` | Prompt tokens consumed |
| `gen_ai.usage.output_tokens` | Completion tokens produced |
| `gen_ai.request.model` | Model deployment name |
| `gen_ai.operation.name` | `chat` or `execute_tool` |

### Querying a specific run in Application Insights Search

To find all telemetry for a single run by its `RunId`:

1. In Application Insights, select **Search** (or **Transaction search**)
2. Enter the RunId (e.g. `run_abc123`) in the search box
3. Alternatively, use **Logs** with a KQL query:

```kusto
traces
| where customDimensions["RunId"] == "run_abc123"
| order by timestamp asc
```

Or to see all token usage for a run:

```kusto
customMetrics
| where name == "agentweaver.token.usage"
| where customDimensions["run_id"] == "run_abc123"
| summarize totalTokens = sum(value) by tostring(customDimensions["agent_name"])
```

### AKS Managed Prometheus metrics

Business metrics emitted by `AgentWeaverMetrics` are exported to the AKS Managed Prometheus workspace:

| Metric | Type | Description |
|---|---|---|
| `agentweaver_token_usage_total` | Counter | Token usage by agent and model |
| `agentweaver_run_duration` | Histogram | Run duration in milliseconds |
| `agentweaver_run_errors_total` | Counter | Run errors by type |
| `agentweaver_run_active` | UpDownCounter | Currently active runs |
| `agentweaver_run_queued` | Gauge | Active-project Ready backlog tasks awaiting coordinator pickup (`backlog_tasks.state='ready' AND run_id IS NULL`), sampled every 15s. Legacy name retained; aggregate with `max`, not `sum`, because every replica exports the same global snapshot. |

To query in Azure Managed Grafana (linked to the Prometheus workspace), use standard PromQL:

```promql
rate(agentweaver_token_usage_total[5m])
```

### Worker autoscaling (queue depth vs. CPU)

`k8s/base/worker-hpa.yaml` currently scales `agentweaver-worker` on **CPU utilization** (70% target),
which is a poor proxy for actual backlog — the worker is I/O-bound, not CPU-bound.

The `agentweaver_run_queued` gauge above (issue #108) exists specifically to provide a real
queue-depth signal for this HPA. In the current system that signal is **not**
`runs.status='pending'` — backlog pickup creates coordinator runs directly as `in_progress`. The
durable queue is the set of active-project backlog tasks still in **Ready** with no bound `run_id`
yet, which is what the gauge now publishes every 15 seconds. Because each replica exports the same
shared-store total, Prometheus/KEDA queries must use `max(agentweaver_run_queued)` (or the
equivalent single-series selector), not `sum(...)`.

The HPA itself has **not yet been switched over** — wiring an `external` metric type into a plain
`HorizontalPodAutoscaler` requires a Kubernetes External Metrics API adapter capable of serving
Azure Monitor managed-Prometheus-backed queries, and no such adapter is currently provisioned in
`scripts/azure/`. The two realistic paths forward (tracked against #108 — see
`decisions/inbox/niobe-108-hpa-investigation.md` for the full analysis) are:

1. **KEDA with a Prometheus scaler** (Microsoft's supported pattern for scaling on Azure Monitor
   managed Prometheus metrics) — query `agentweaver_run_queued` via the workspace's Prometheus
   query endpoint using `max(...)`, not `sum(...)`.
2. Provision a `k8s-prometheus-adapter`-style External Metrics API adapter and wire `worker-hpa.yaml`
   with a `type: External` metric block pointing at it.

Until one of these is chosen and the supporting cluster component is provisioned, the worker
continues to scale on CPU (with the gauge available for manual/Grafana-based capacity monitoring
in the meantime).
