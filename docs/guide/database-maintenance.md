# Run database maintenance

Use the database maintenance runner to apply reviewed PostgreSQL migrations to
the Azure P1 environment and bring the services onto matching images. One
command does the whole update. A person runs it locally, and GitHub Actions runs
the same command from a manual workflow. No step needs a hand-made pod, a manual
tunnel, or a one-off command.

The runner applies these migrations, in this order:

1. Identity Broker EF Core migrations, when you pass a Broker digest.
2. Environment EF Core migrations 1 to 11.
3. Orchestrator (Core) migrations 1 to 16, 18 to 33, and 35 to 38. Reserved gaps are not applied.
4. Events & Sessions migrations 1 to 10.

The reviewed plan fixes these versions by hash. Core migration 017 is not in the
plan, while 032 is included with its exact runtime table grants. Migration 035
adds backlog queue positions, 036 adds append-only coordinator-root model-turn
admissions, 037 adds append-only child-spawn authorization receipts and
consumption records, and 038 adds the Source Control pull-request operation
ledger. Migration 038 requires migration 012. Reserved migration 034 remains
outside the plan.
Migrations 036 and 037 depend on the immutable-row trigger function from 021.
Migration 037 also references run, session, grant, and policy-receipt records
already owned by Orchestrator. An image that would apply a version outside the
plan fails before it connects.
Readiness accepts 036 only when its ledger row and table are both absent, or
when the migration is recorded and the table has every admission column, its
request primary key, and an enabled unconditional immutability trigger.
Readiness accepts 037 only when its ledger row and both tables are absent, or
when the migration is recorded and both evidence tables have their expected
columns, keys, foreign keys, and enabled immutable-row triggers. The runtime
role receives exactly `SELECT, INSERT` on both 037 tables.
Readiness accepts 038 only when its ledger row and operation table are both
absent, or when the migration is recorded and the operation table has its
expected columns and constraints. The runtime role receives exactly
`SELECT, INSERT, UPDATE` on the 038 table.
Readiness ordering retains the exact supported Core set so 034 is never treated
as compatible between 033 and 035.

## What one run does

| Step | What happens | Safe to repeat because |
| --- | --- | --- |
| Source check | Requires a clean checkout whose commit is on `origin/v1`. | Read-only. |
| Locked restore | Restores the persistence, Broker, and relay projects with `--locked-mode`. | Read-only. |
| Plan check | Requires the committed plan file, the plan generator, and the pinned hash to agree. | Read-only. |
| Cluster access | Checks the Azure account, gets a private kubeconfig for `agentweaver-aks`, and checks Kubernetes permissions. | Uses a temporary file that is always removed. |
| Broker egress check | Compares the live Broker migration egress policy with the repository and reports any DNS name that differs. | Read-only. |
| Setup check | Reads the migration identity, its federated credential, and the recorded handover. A workload identity that would need either one stops here and names the setup command. | Read-only. |
| Service image check | In normal maintenance mode, reads running and target schema ranges and chooses deployment order. Skipped by `--migrations-only`. | Read-only. |
| Lock | Creates the Lease `p1-db-maintenance-lock`. A second run fails here. | Released at the end of every run. |
| Migration identity | Uses the existing identity when it and its federated credential match. Otherwise a run signed in as the PostgreSQL Entra administrator deploys `infra/bicep/p1-postgres-migration-identity.bicep`. | The deployment is declarative. |
| ServiceAccount and egress | Applies `p1-postgres-migration` and its egress policy with server-side apply. | Server-side apply. |
| Ownership handover | Makes the migration identity the owner of the `environment`, `orchestrator`, and `events_sessions` schemas. | Recorded in the ConfigMap `p1-db-maintenance-state`; later runs skip it. |
| Connectivity probes | A short Job under each migration egress policy resolves the PostgreSQL name, including its private link name, and opens port 5432. It requests at least 25m CPU and 32Mi memory to satisfy the P1 namespace minimum. | Uniquely named and removed. |
| Migration Jobs | Runs the Broker, Environment, Orchestrator, and Events & Sessions Jobs on the pinned digests, one at a time. | Migrators skip applied versions. |
| Grants check | Requires one grants marker per P1 Job whose versions equal the plan exactly. | Read-only. |
| Services | Normal maintenance mode can roll mismatched Deployments to the same digests when asked, then waits for readiness. `--migrations-only` never reads or changes application Deployments. | Matching images are not changed. |
| Cleanup and receipt | Deletes the run's Jobs and pods, releases the lock, removes temporary files, and writes a receipt. | Runs on success and on failure. |

Without `--execute`, the runner stops after its mode-specific preflight. It
prints the steps an execute run would take and changes nothing.

## Automatic migration before deployment

The supported v1 workload path runs migrations and Projects & Config
runtime-grant reconciliation before applying services:

```powershell
npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --execute
```

The deployment excludes Namespace objects, runs migration Jobs first, then
applies Kustomize and waits for nine rollouts. The one-time Projects & Config
identity/ownership bootstrap is separate; on a guard failure, stop for
authorized role inspection and never loosen the guard or broaden the role.
See the [release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md#deploy-workloads-locally)
for the local workload deployment procedure.

A successful migration is not itself a successful deployment. The runtime
checks its database privileges at startup, and a failed startup guard causes
the Deployment rollout check to fail. The Projects & Config migration
automatically reconciles runtime grants to the reviewed allow-listed plan.
Changes to role attributes or ownership, and privileges outside that plan,
require separate authorization.

## Before you begin

You need:

- A clean checkout of `v1` at a commit that is already on `origin/v1`.
- Node.js 24, the .NET SDK from `global.json`, Azure CLI, `kubectl`, and
  `kubelogin`.
- An Azure CLI sign-in on the Agentweaver subscription and tenant with
  access to the `agentweaver-aks` cluster.
- The image digests of a published release for Orchestrator and
  Environment, Events & Sessions, and for the Identity Broker if it has new
  migrations. Read them from the release's runtime manifest.

The P1 migration Jobs require images built from source that prints the grants
marker. Older images fail the grants check after they migrate. Use
`--apps-only` to move services onto an older release.

The Events & Sessions Job also sets the full Sessions and Messaging provider
configuration from its runtime map. The host validates those settings before
it enters `--migrate`. Orchestrator handles `--migrate` before loading normal
runtime provider settings.

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
| Workflow identity | `infra/bicep/db-maintenance-ci-identity.bicep` creates `agentweaver-id-db-maintenance`, its federated credential for `v1-db-maintenance`, Reader on the resource group, and AKS Cluster User on the cluster. Setup applies a Kubernetes Role and RoleBinding in the `agentweaver` namespace. |
| GitHub environment | The environment `v1-db-maintenance`, limited to the `v1` branch, and its variable `AZURE_DB_MAINTENANCE_CLIENT_ID`. |

The AKS template enables both Azure RBAC and Kubernetes RBAC, and setup checks
that both are enabled before it continues. The built-in AKS RBAC Writer role
covers standard resources such as Jobs, Deployments, pod logs, and ConfigMaps,
but it also grants access to Secrets and lets the identity run Pods as any
ServiceAccount in the namespace. It also does not grant access to the Cilium
network-policy resource that the runner checks. Instead, setup applies a
Kubernetes Role with only the verbs the runner needs, including access to that
Cilium resource. Deployment access is limited by name to `environment`,
`orchestrator`, and `events-and-sessions`. The RoleBinding names the workflow identity's object ID.
This uses both AKS authorization models without creating an Azure custom role
definition. Setup reads the Role and RoleBinding back to verify their exact
permissions and identity. It never removes the workflow identity, Azure
assignments, Role, or RoleBinding, so a failed setup can be checked and safely
rerun.

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
  --environment-digest sha256:<environment> `
  --orchestrator-digest sha256:<orchestrator> `
  --events-digest sha256:<events-and-sessions> `
  --broker-digest sha256:<identity-broker>
```

Then apply, and roll the services to the same digests:

```powershell
npm run db:maintenance -- --execute --deploy-apps `
  --environment-digest sha256:<environment> `
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
  -f environment_digest=sha256:<environment> `
  -f events_digest=sha256:<events-and-sessions> `
  -f services=deploy `
  -f execute=true
```

Only one workflow run executes at a time.

## Keep services ready

The Environment, Orchestrator, and EventsAndSessions services check their schema version at
startup and on `/health/ready`. Each image accepts only a range of schema
versions. If the database moves outside that range, the service becomes
unready.

The runner reads the running images before it changes anything. It compares
each image's accepted range with the current schema and the target schema, and
picks one order:

| Order | When | What the runner does |
| --- | --- | --- |
| Migrate first | The running image accepts the target schema. | Migrates. With `--deploy-apps`, then sets the new image. |
| Deploy first | The new image accepts the current schema. | Sets the new image and waits until it is ready, then migrates. Needs `--deploy-apps`. |
| Planned readiness gap | Neither image accepts the other's schema. | Migrates, then sets the new image in the same run and waits until it is ready. Needs `--deploy-apps`. |

The options are:

| Option | Workflow `services` | Behavior |
| --- | --- | --- |
| None | `block` | Stops before any change when a service would need a new image to stay ready. |
| `--deploy-apps` | `deploy` | Sets the given digests in the order above and waits until all three services are ready. |
| `--apps-only` | `apps-only` | Skips every database step. Only rolls the three Deployments to the given digests under the lock and waits until they are ready. |

### Schema ranges

The runner reads each image's accepted range from the image itself. There is
no list to update when you publish an image. A schema version is the highest
applied migration of that service.

For every target image and each different running image, the runner:

1. Reads the image's `org.opencontainers.image.revision` label from the
   registry. Every published service image has this label. It holds the commit
   the image was built from.
2. Reads the service's migrator source at that commit with git. It fetches the
   commit from `origin` if the clone does not have it.
3. Reads the range from the readiness check in that source. The highest
   version the check requires is the lowest accepted version. The highest
   version it allows is the highest accepted version. For example, the current
   Orchestrator image accepts schema versions 31 through 38. It accepts the
   complete schema before and after migrations 032, 033, 035, 036, 037, and 038.
   The root model-turn admissions table is optional until 036 is applied, and
   the child-spawn authorization tables are optional until 037 is applied.
   The pull-request operation ledger is optional until 038 is applied.
   Reserved migration 034 is not accepted by this source revision.

Environment uses EF Core timestamp migration IDs. The source assigns their
catalog order to schema versions 1 through 11, and startup rejects pending
migrations, so the runner treats an Environment image as accepting its exact
catalog version.

If the runner cannot read the range of a new image, the run stops before any
change. If the new image does not accept the target schema of the reviewed
plan, the digest was built from other source, and the run also stops. This check
also applies when the Deployment already runs that digest or does not exist. If it
cannot read the range of a running image, for example a tag or an image from
another registry, it assumes nothing about that image, so the run takes the
planned-gap path. The runner records each schema version in its state after a
run, and uses it to decide whether a new image accepts the current schema.

For the Core 031 to 038 upgrade, the old owner accepts only 31 to 32. The new
owner accepts 31 to 38, so it can become ready before 032, 033, and 035 to 038
are applied.
When the state records schema 31, `--deploy-apps` deploys the new owner first,
waits for readiness, then applies the planned migrations. If no schema version
is recorded, the runner cannot prove that order is safe and uses a planned
readiness gap instead. Both paths keep 017 out of the migration plan.

### Planned readiness gap

Some migrations have no order that keeps a service ready. For example, when the
current images reject the new schema and the new images reject the old one,
the service is unready for a short time. This is expected, not an error. The
runner:

1. Logs the planned gap and records it in the receipt before it changes anything.
2. Refuses to migrate without `--deploy-apps`, because the matching images
   must follow in the same run.
3. Records the images it will deploy in its state, then runs the migration Jobs.
4. Sets the matching images right away and waits until each service is ready,
   within `--readiness-timeout-seconds` (default 600).
5. Writes the gap's start, end, and length in seconds to `readinessGap` in the
   receipt.

### Roll forward only

After the schema has moved, the old images cannot run on it. The runner never
sets an older image. If the deploy or the readiness wait fails, the run stops,
reports the failure, and keeps the gap open in the receipt (no end time). The
state still holds the images it was deploying.

Run the same command again to finish. The next run accepts only those same
digests. It refuses other digests, including the old ones, before it takes the
lock. When the services are ready, it clears the record.

When migrations have already been applied and services are unready, roll the
services to the release that matches the schema:

```powershell
npm run db:maintenance -- --execute --apps-only `
  --environment-digest sha256:<environment> `
  --orchestrator-digest sha256:<orchestrator> `
  --events-digest sha256:<events-and-sessions>
```

Only the container image changes. The runner never creates a Deployment, and it
fails if one is missing.

Apps-only maintenance updates only the selected P1 Deployments. For the full
v1 workload set, use `npm run deploy:kustomize`; it runs migrations first and
then applies the root Kustomize overlay without a deployment receipt gate.

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
before and after, the chosen service order, any planned readiness gap, the
cleanup result, and the error when the run fails.

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

- Apply Core migration 017 or any version outside the reviewed plan.
- Reset, reinitialize, or drop a database, schema, or role.
- Create Azure role assignments or change identities other than the P1
  migration identity. Only the setup command assigns roles, and only the ones
  in its template.
- Change any Deployment setting other than the service image.

These changes need their own review.

After the ownership handover, the operator no longer owns the P1 schemas.
`--apply-pending-migrations` of `bootstrap-p1-postgres.mjs` is limited to
operator-owned P1 schemas, so use this runner for later P1 migrations instead.
Its read-only `--inspect` mode reports live ownership and grants without
changing them. Projects & Config runtime grants are reconciled by its
dedicated-owner migration Job after EF migrations; the P1 operator initializer
does not grant access in that schema.

## Related information

- [Deploy Agentweaver to Azure](deploy-to-azure.md)
- [Azure foundation resources](../architecture/azure.md)
- [Runtime configuration reference](../reference/runtime-configuration.md#p1-migration-job-settings)
