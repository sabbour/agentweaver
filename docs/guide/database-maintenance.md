# Run database maintenance

Use the database maintenance runner to apply reviewed PostgreSQL migrations to
the Azure P1 environment and bring the services onto matching images. One
command does the whole update. A person runs it locally, and GitHub Actions runs
the same command from a manual workflow. No step needs a hand-made pod, a manual
tunnel, or a one-off command.

The runner applies these migrations, in this order:

1. Identity Broker EF Core migrations, when you pass a Broker digest.
2. Orchestrator (Core) migrations 1 to 16 and 18 to 31.
3. Events & Sessions migrations 1 to 9.

The reviewed plan fixes these versions by hash. Core migration 032 is not in the
plan. An image that would apply it fails before it connects.

## What one run does

| Step | What happens | Safe to repeat because |
| --- | --- | --- |
| Source check | Requires a clean checkout whose commit is on `origin/v1`. | Read-only. |
| Locked restore | Restores the persistence, Broker, and relay projects with `--locked-mode`. | Read-only. |
| Plan check | Requires the committed plan file, the plan generator, and the pinned hash to agree. | Read-only. |
| Cluster access | Checks the Azure account, gets a private kubeconfig for `agentweaver-aks`, and checks Kubernetes permissions. | Uses a temporary file that is always removed. |
| Broker egress check | Compares the live Broker migration egress policy with the repository and reports any DNS name that differs. | Read-only. |
| Setup check | Reads the migration identity, its federated credential, and the recorded handover. A workload identity that would need either one stops here and names the setup command. | Read-only. |
| Service image check | Reads the images the Orchestrator and Events & Sessions Deployments run. | Read-only. |
| Lock | Creates the Lease `p1-db-maintenance-lock`. A second run fails here. | Released at the end of every run. |
| Migration identity | Uses the existing identity when it and its federated credential match. Otherwise a run signed in as the PostgreSQL Entra administrator deploys `infra/bicep/p1-postgres-migration-identity.bicep`. | The deployment is declarative. |
| ServiceAccount and egress | Applies `p1-postgres-migration` and its egress policy with server-side apply. | Server-side apply. |
| Ownership handover | Makes the migration identity the owner of the `orchestrator` and `events_sessions` schemas. | Recorded in the ConfigMap `p1-db-maintenance-state`; later runs skip it. |
| Connectivity probes | A short Job under each migration egress policy resolves the PostgreSQL name, including its private link name, and opens port 5432. | Uniquely named and removed. |
| Migration Jobs | Runs the Broker, Orchestrator, and Events & Sessions Jobs on the pinned digests, one at a time. | Migrators skip applied versions. |
| Grants check | Requires one grants marker per P1 Job whose versions equal the plan exactly. | Read-only. |
| Services | Rolls mismatched Deployments to the same digests when asked, then waits for every service to be ready. | Matching images are not changed. |
| Cleanup and receipt | Deletes the run's Jobs and pods, releases the lock, removes temporary files, and writes a receipt. | Runs on success and on failure. |

Without `--execute`, the runner stops after the service image check. It prints
the steps an execute run would take and changes nothing.

## Before you begin

You need:

- A clean checkout of `v1` at a commit that is already on `origin/v1`.
- Node.js 24, the .NET SDK from `global.json`, Azure CLI, `kubectl`, and
  `kubelogin`.
- An Azure CLI sign-in on the Agentweaver subscription and tenant with
  access to the `agentweaver-aks` cluster.
- The image digests of a published release for Orchestrator and
  Events & Sessions, and for the Identity Broker if it has new migrations.
  Read them from the release's runtime manifest.

The P1 migration Jobs require images built from source that prints the grants
marker. Older images fail the grants check after they migrate. Use
`--apps-only` to move services onto an older release.

### One-time setup

One command sets up everything that routine runs need. Run it once per
environment, and again at any time to check the setup:

```powershell
npm run db:maintenance:setup            # check only; exits 1 and lists what is missing
npm run db:maintenance:setup -- --execute
```

With `--execute` it creates only what is missing, reads everything back, and
writes a receipt:

| Part | What it creates |
| --- | --- |
| P1 migration identity | The identity, its federated credential, its ServiceAccount and egress policy, and the schema ownership handover. It reuses the runner in `--prepare-only` mode, with the same preflight, lock, cleanup, and receipt. |
| Workflow identity | `infra/bicep/db-maintenance-ci-identity.bicep`: the identity `agentweaver-id-db-maintenance`, a federated credential for the GitHub environment `v1-db-maintenance`, Reader on the resource group, AKS Cluster User on the cluster, and a custom Kubernetes role scoped to the `agentweaver` namespace. |
| GitHub environment | The environment `v1-db-maintenance`, limited to the `v1` branch, and its variable `AZURE_DB_MAINTENANCE_CLIENT_ID`. |

Run the setup with the Azure CLI signed in as the PostgreSQL Entra
administrator, with the right to assign roles in the `agentweaver` resource
group, and with the GitHub CLI signed in as a repository admin. This is the
same sign-in that runs the P1 bootstrap. The command needs no prompt or
decision, so it runs the same from a terminal, a script, or an agent session
that already has that sign-in.

The setup needs that sign-in because of the ownership handover. The P1 schemas
start out owned by the Entra administrator's database role. PostgreSQL lets
only a member of the owning role give objects away, and Azure Database for
PostgreSQL has no superuser. Only the Entra administrator can also create the
database principal for the migration identity. After the handover, the
migration identity owns the schemas, and later runs, including workflow runs,
need neither that sign-in nor the setup command.

The workflow identity cannot deploy Azure resources or assign roles. If the
migration identity or the handover is missing, a workflow run stops before it
changes anything and names the setup command. Role assignments can take a few
minutes to reach AKS. Run the workflow once without **execute** to confirm the
workflow identity has every permission the runner checks.

## Run it locally

Check preconditions first. This changes nothing:

```powershell
npm run db:maintenance -- `
  --orchestrator-digest sha256:<orchestrator> `
  --events-digest sha256:<events-and-sessions> `
  --broker-digest sha256:<identity-broker>
```

Then apply, and roll the services to the same digests:

```powershell
npm run db:maintenance -- --execute --deploy-apps `
  --orchestrator-digest sha256:<orchestrator> `
  --events-digest sha256:<events-and-sessions> `
  --broker-digest sha256:<identity-broker>
```

Leave out `--broker-digest` when the Broker has no new migrations.

## Run it from GitHub Actions

Open the **v1 database maintenance** workflow and choose **Run workflow** on
the `v1` branch. Fill in the digests. Leave **execute** off for a preflight, and
turn it on to apply. The workflow runs the same command and uploads the receipt
as an artifact, even when the run fails.

From the GitHub CLI:

```powershell
gh workflow run v1-db-maintenance.yml --ref v1 `
  -f orchestrator_digest=sha256:<orchestrator> `
  -f events_digest=sha256:<events-and-sessions> `
  -f services=deploy-after-migrate `
  -f execute=true
```

Only one workflow run executes at a time.

## Keep services ready

Each service's readiness check verifies the schema it expects. If you migrate
the database while a service runs an older image, that service becomes unready.
The runner therefore reads the running images before it changes anything:

| Option | Workflow `services` | Behavior |
| --- | --- | --- |
| None | `block` | Stops before any change when a service runs a different image. |
| `--deploy-apps` | `deploy-after-migrate` | Migrates, then sets the new image on each mismatched Deployment and waits until both services are ready. |
| `--apps-only` | `apps-only` | Skips every database step. Only rolls the Deployments to the given digests under the lock and waits until they are ready. |
| `--allow-app-image-mismatch` | Not offered | Migrates and leaves services as they are. The receipt records that the matching release still needs to be deployed. |

The order is always migrate, then deploy. New images fail readiness against the
old schema, and old images fail it against the new one, so deploying first
does not avoid the gap.

When migrations have already been applied and services are unready, roll the
services to the release that matches the schema:

```powershell
npm run db:maintenance -- --execute --apps-only `
  --orchestrator-digest sha256:<orchestrator> `
  --events-digest sha256:<events-and-sessions>
```

Only the container image changes. The runner never creates a Deployment, and it
fails if one is missing.

## Reconcile Broker egress drift

Every run compares the live `identity-broker-migration-egress` policy with the
repository and prints the DNS names that differ. To restore the repository
policy, add `--reconcile-broker-egress` to an execute run. The connectivity
probe that follows proves the policy still reaches PostgreSQL before the Broker
Job starts.

## Read the receipt

Every run writes a JSON receipt, even when it fails. Locally it goes to
`~/.agentweaver/db-maintenance/<run-id>.json`, outside the checkout, so the
next run's clean-tree check still passes. Use `--receipt-dir` to choose another
folder.

The receipt records the source commit, the operator, each step with its time,
the resolved private link name, each Job's grants marker, the service images
before and after, the cleanup result, and the error when the run fails.

## Recover from a failed run

The runner fails closed. The first failed check stops the run, and cleanup still
runs. Read the error and the receipt, fix the cause, and run the same command
again. Steps that already finished are skipped.

If a run stops without cleanup, for example because the machine lost power, its
lock stays. The next run prints the holder's run ID. After you confirm that run
is not active, release its lock:

```powershell
npm run db:maintenance -- --release-lock <run-id>
```

## What the runner never does

- Apply Core migration 032 or any version outside the reviewed plan.
- Reset, reinitialize, or drop a database, schema, or role.
- Create Azure role assignments or change identities other than the P1
  migration identity. Only the setup command assigns roles, and only the ones
  in its template.
- Change any Deployment setting other than the service image.

These changes need their own review.

After the ownership handover, the operator no longer owns the P1 schemas. The
`--inspect` and `--apply-pending-migrations` modes of
`bootstrap-p1-postgres.mjs` expect operator ownership, so use this runner for
later migrations instead.

## Related information

- [Deploy Agentweaver to Azure](deploy-to-azure.md)
- [Azure foundation resources](../architecture/azure.md)
- [Runtime configuration reference](../reference/runtime-configuration.md#p1-migration-job-settings)
