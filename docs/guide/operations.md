# Operations Guide

This guide covers the day-to-day operational procedures for running and releasing Agentweaver in production on AKS.

## Release Process

Agentweaver uses [Semantic Versioning](https://semver.org/) (`vMAJOR.MINOR.PATCH`). The current version is tracked in the [`VERSION`](../../VERSION) file at the repo root. All container images and GitHub releases are tagged with this version.

### When to cut a release

| Change type | Command |
|---|---|
| Bug fix | `bash scripts/release.sh patch` |
| New feature (backward-compatible) | `bash scripts/release.sh minor` |
| Breaking change | `bash scripts/release.sh major` |

### Prerequisites

- Clean working tree (no uncommitted changes)
- `gh` CLI authenticated (`gh auth status`)
- `az` CLI authenticated with access to `agentweaverregistry` ACR
- `kubectl` configured to point at the target cluster
- `IDENTITY_CLIENT_ID` and `TENANT_ID` set (or exported) in your environment

### Running a release

```bash
# Patch release (e.g. 0.6.0 -> 0.6.1)
bash scripts/release.sh patch

# Minor release (e.g. 0.6.0 -> 0.7.0)
bash scripts/release.sh minor

# Major release (e.g. 0.6.0 -> 1.0.0)
bash scripts/release.sh major
```

To preview actions without making changes:

```bash
DRY_RUN=true bash scripts/release.sh patch
```

### What the release script does

The [`scripts/release.sh`](../../scripts/release.sh) script automates the full release cycle:

1. **Validates clean working tree** — aborts if there are uncommitted changes.
2. **Bumps the version** — reads `VERSION`, increments the appropriate component, writes the new value.
3. **Commits the version bump** — `chore(release): bump version to vX.Y.Z`.
4. **Creates an annotated git tag** — `vX.Y.Z`.
5. **Generates a changelog** — queries merged pull requests since the last release using `gh pr list`.
6. **Creates a GitHub Release** — publishes the changelog to the GitHub Releases page.
7. **Identifies changed images** — compares file paths against the previous tag using `git diff`.
8. **Builds changed images** — uses `az acr build` (no local Docker daemon required).
9. **Retags unchanged images** — uses `az acr import` for a server-side copy (fast, no rebuild).
10. **Deploys** — runs `scripts/aks/30-deploy.sh` with `IMAGE_TAG=vX.Y.Z`.
11. **Pushes** — pushes the commit and tag to `origin`.

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

To roll back to a previous version, re-run the deploy step with the old tag:

```bash
IMAGE_TAG=v0.6.0 bash scripts/aks/30-deploy.sh
```

All previous semver tags remain in ACR and are not deleted by the release script.

## Manual image builds (development)

To build and push images without cutting a release (e.g. for a staging environment):

```bash
# Builds using the current git SHA as the tag
source scripts/aks/00-variables.sh
bash scripts/aks/20-build-push-images.sh
```

To force a rebuild of all images (ignore changed-path optimisation):

```bash
FORCE_REBUILD=true bash scripts/aks/20-build-push-images.sh
```

## Observability notes

- Token and AIC usage data now lives in **Application Insights / Azure Monitor**, not in the application database.
- The project dashboard throughput chart and agent leaderboard read from `GET /api/projects/{id}/metrics`, which proxies App Insights KQL.
- If `APPLICATIONINSIGHTS_CONNECTION_STRING` is not configured, the metrics endpoint returns empty arrays so the dashboard degrades gracefully.

## Related scripts

| Script | Purpose |
|---|---|
| `scripts/release.sh` | Full semver release (see above) |
| `scripts/aks/00-variables.sh` | Shared AKS variables (source this before other scripts) |
| `scripts/aks/15-provision-monitoring.sh` | Provision Application Insights + AKS Managed Prometheus |
| `scripts/aks/20-build-push-images.sh` | Build and push images to ACR |
| `scripts/aks/30-deploy.sh` | Deploy to AKS |
| `scripts/aks/40-verify.sh` | Smoke-test the deployed cluster |

## Observability

Agentweaver ships with end-to-end telemetry using **Azure Monitor OpenTelemetry Distro** (Application Insights) and **AKS Managed Prometheus**.

### Provisioning monitoring resources

Monitoring is provisioned automatically by `scripts/aks/30-deploy.sh` when Application Insights does not yet exist. To provision it standalone:

```bash
source scripts/aks/00-variables.sh
bash scripts/aks/15-provision-monitoring.sh
```

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

To query in Azure Managed Grafana (linked to the Prometheus workspace), use standard PromQL:

```promql
rate(agentweaver_token_usage_total[5m])
```
