# Operations Guide

This guide covers the day-to-day operational procedures for running and releasing Agentweaver in production on AKS.

For provisioning, image builds, deployment, and verification, use the root
package scripts (`pnpm run` preferred; `npm run` is equivalent). The
[AKS deployment runbook](./deployment-aks.md) defines the canonical sequence
and individual-step instructions.

## Release Process

Agentweaver uses Changesets with the protected
`dev → release/vX.Y.Z → main` flow. Contributors add a changeset, maintainers preview
the calculated version, and `release:prepare` writes version metadata and the durable
changelog section on the release branch. After the prepared branch is promoted to `main`,
`azure:release` tags, creates a GitHub Release from that exact changelog section, builds,
deploys, and verifies.

Follow [RELEASING.md](../../RELEASING.md) for the canonical release procedure and the
[Agentweaver changelog skill](../../.copilot/skills/agentweaver-changelog/SKILL.md) for
the full fragment lifecycle, recovery commands, and changelog/release-notes rules.

### Image tags

| Tag | Meaning |
|---|---|
| `vX.Y.Z` | Immutable semver release tag |
| `latest-release` | Mutable alias — always points to the most recently *built* image |
| `<git-sha>` | Short SHA from ad-hoc builds (CI / development) |

> **Note:** `latest` is explicitly rejected by the variable scripts to prevent accidental use of a mutable tag in production.

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

To roll back to a previous version, redeploy with the old tag using
`azure:deploy`'s `--image-tag` flag (the deploy pipeline is idempotent, so
re-running it against an existing environment just redeploys/verifies rather
than re-provisioning from scratch):

```bash
npm run azure:deploy -- --image-tag v0.6.0
```

All previous semver tags remain in ACR and are not deleted by the release process.

## Manual image builds (development)

To build and push images without cutting a release (e.g. for a staging
environment), use `azure:upgrade`, which builds using the current git SHA as
the tag and then redeploys:

```bash
npm run azure:upgrade
```

## Observability notes

- Token and AIC usage data now lives in **Application Insights / Azure Monitor**, not in the application database.
- The project dashboard throughput chart and agent leaderboard read from `GET /api/projects/{id}/metrics`, which proxies App Insights KQL.
- Configure `APPLICATIONINSIGHTS_CONNECTION_STRING` **and** a Log Analytics workspace id (`APPLICATIONINSIGHTS_WORKSPACE_ID` or `ApplicationInsights:WorkspaceId`) unless your connection string already embeds `WorkspaceId`.
- If App Insights is not configured, or no workspace id can be resolved, the metrics endpoint returns empty arrays so the dashboard degrades gracefully.

## Related scripts

| Command | Purpose |
|---|---|
| `npm run azure:release` | Full semver release (see above) |
| `npm run azure:deploy` | Provision/redeploy AKS, identity, monitoring, OAuth signing key, and PostgreSQL |
| `npm run azure:upgrade` | Build, push, and verify images in ACR, then redeploy and cycle the warm pool |
| `npm run azure:verify` | Verify the current deployment |

Use `pnpm run` in place of `npm run` if pnpm is your selected package runner.
The runbook's [individual-step section](./deployment-aks.md#running-an-individual-step)
shows how to rerun one step.

## Observability

Agentweaver ships with end-to-end telemetry using **Azure Monitor OpenTelemetry Distro** (Application Insights) and **AKS Managed Prometheus**.

### Provisioning monitoring resources

Monitoring is provisioned as part of `npm run azure:deploy`. To rerun only
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

`k8s/worker-hpa.yaml` currently scales `agentweaver-worker` on **CPU utilization** (70% target),
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
