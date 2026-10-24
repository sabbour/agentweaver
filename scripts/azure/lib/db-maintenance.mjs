/**
 * Runs the v1 P1 database maintenance path end to end: preflight, lock, migration identity,
 * ServiceAccount and egress, schema ownership handover, migration Jobs, verification, cleanup,
 * and a receipt. A person runs it with `npm run db:maintenance`; GitHub Actions runs the same
 * command from the v1-db-maintenance workflow. Every step is safe to repeat.
 */
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { run, redact } from './exec.mjs';
import { p1PostgresPlanHash } from './p1-postgres-plan.mjs';
import { bootstrapIdentityPostgres } from './identity-postgres-bootstrap.mjs';
import { parseKustomizeResources } from '../../release/kustomize-runtime.mjs';
import { componentImageRepository } from '../../release/pack.mjs';
import { verifyServiceRegistryImage } from '../../release/registry.mjs';

/** The only environment this runner can touch. Every value is checked against the live account. */
export const DB_MAINTENANCE_TARGET = Object.freeze({
  subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
  tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
  resourceGroup: 'agentweaver',
  clusterName: 'agentweaver-aks',
  namespace: 'agentweaver',
  postgresHost: 'agentweaver-pg.postgres.database.azure.com',
  registry: 'ghcr.io/sabbour',
});
/** The reviewed plan binds Environment, Events, Core, and Projects & Config runtime grants. */
export const REVIEWED_PLAN_SHA256 = 'd707f9e1af3c55e5da496e4185b5a64f9d2d0a594f00a5f7c6d2a505a86e1383';
export const LOCK_NAME = 'p1-db-maintenance-lock';
export const STATE_NAME = 'p1-db-maintenance-state';
export const MIGRATION_IDENTITY = 'agentweaver-id-p1-postgres-migration';
export const GRANTS_MARKER = 'P1_POSTGRES_RUNTIME_GRANTS_VERIFIED';
export const CONNECTIVITY_MARKER = 'DB_MAINTENANCE_CONNECTIVITY_OK';
// Small Microsoft-published busybox image for the connectivity probe, pinned by digest.
export const PROBE_IMAGE = 'mcr.microsoft.com/azurelinux/busybox:1.36.1-azl3.0.20261015@sha256:7c62d6f72fa83c79971a75c923af642701e46b3405b1a0b2002fd066d63a531b';
const RUN_LABEL = 'agentweaver.io/db-maintenance-run';
const P1_DIRECTORY = join('deploy', 'k8s', 'migrations', 'p1-postgres');
const BROKER_DIRECTORY = join('deploy', 'k8s', 'migrations', 'identity-broker');
const IDENTITY_TEMPLATE = join('infra', 'bicep', 'p1-postgres-migration-identity.bicep');
const PLAN_FILE = join('scripts', 'azure', 'lib', 'p1-postgres-plan.json');
// Each owner's readiness check and the migration table it reads. The runner reads this file at
// the commit an image was built from to learn which schema versions that image accepts. Git
// paths always use forward slashes.
const OWNER_SOURCES = Object.freeze({
  'Agentweaver.Environment': {
    file: 'services/environment/Agentweaver.Environment/EnvironmentMigrator.cs',
  },
  'Agentweaver.Orchestrator': {
    file: 'services/orchestrator/Agentweaver.Orchestrator/CoordinationOwnerMigrator.cs',
    table: 'coordination_schema_migrations',
  },
  'Agentweaver.EventsAndSessions': {
    file: 'services/events-and-sessions/Agentweaver.EventsAndSessions/EventsAndSessionsMigrator.cs',
    table: 'sessions_schema_migrations',
  },
});
// Projects whose locked restore must succeed before anything runs: the shared persistence code
// in the Jobs and the handover, the Broker verifier, and the relay used by the handover.
const LOCKED_PROJECTS = [
  join('packages', 'Agentweaver.Persistence.Postgres', 'Agentweaver.Persistence.Postgres.csproj'),
  join('services', 'identity', 'Agentweaver.Identity.Broker', 'Agentweaver.Identity.Broker.csproj'),
  join('tools', 'IdentityPostgresTcpProxy', 'IdentityPostgresTcpProxy.csproj'),
];
const PERMISSIONS = [
  ['create', 'jobs'], ['get', 'jobs'], ['delete', 'jobs'], ['list', 'jobs'], ['watch', 'jobs'],
  ['get', 'pods'], ['list', 'pods'], ['get', 'pods', 'log'],
  ['create', 'leases.coordination.k8s.io'], ['get', 'leases.coordination.k8s.io'],
  ['delete', 'leases.coordination.k8s.io'],
  ['get', 'configmaps'], ['create', 'configmaps'], ['patch', 'configmaps'],
  ['get', 'serviceaccounts'], ['patch', 'serviceaccounts'], ['create', 'serviceaccounts'],
  ['get', 'ciliumnetworkpolicies.cilium.io'], ['patch', 'ciliumnetworkpolicies.cilium.io'],
  ['create', 'ciliumnetworkpolicies.cilium.io'],
  ...['environment', 'orchestrator', 'events-and-sessions'].flatMap(name => [
    ['get', 'deployments.apps', undefined, name], ['list', 'deployments.apps', undefined, name],
    ['watch', 'deployments.apps', undefined, name],
  ]),
];
// Apps-only needs the lock, the two Deployments, and the state ConfigMap for roll-forward.
const APP_PERMISSIONS = [
  ['create', 'leases.coordination.k8s.io'], ['get', 'leases.coordination.k8s.io'],
  ['delete', 'leases.coordination.k8s.io'],
  ...['environment', 'orchestrator', 'events-and-sessions'].flatMap(name => [
    ['get', 'deployments.apps', undefined, name], ['list', 'deployments.apps', undefined, name],
    ['watch', 'deployments.apps', undefined, name], ['patch', 'deployments.apps', undefined, name],
  ]),
  ['get', 'configmaps'], ['create', 'configmaps'], ['patch', 'configmaps'],
];
// Each Job: its kustomize name, the image, and the plan field whose versions its marker must report.
const P1_JOBS = [
  { key: 'environment', jobName: 'environment-migration', component: 'Agentweaver.Environment',
    digestOption: 'environmentDigest', schema: 'environment', planVersions: 'environmentVersions' },
  { key: 'orchestrator', jobName: 'orchestrator-migration', component: 'Agentweaver.Orchestrator',
    digestOption: 'orchestratorDigest', schema: 'orchestrator', planVersions: 'coreVersions' },
  { key: 'events-and-sessions', jobName: 'events-and-sessions-migration', component: 'Agentweaver.EventsAndSessions',
    digestOption: 'eventsDigest', schema: 'events_sessions', planVersions: 'eventsVersions' },
];
const DIGEST = /^sha256:[0-9a-f]{64}$/;
const GUID = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const FAILED_PULL = new Set(['ErrImagePull', 'ImagePullBackOff', 'InvalidImageName', 'CreateContainerConfigError']);

export const USAGE = `Usage: npm run db:maintenance -- [options]

Without --execute the runner only checks preconditions and prints what it would do.

  --execute                     Apply the identity, ServiceAccount, egress, handover, and Jobs.
  --environment-digest <d>     Published Environment image digest (sha256:...). Required.
  --orchestrator-digest <d>     Published Orchestrator image digest (sha256:...). Required.
  --events-digest <d>           Published EventsAndSessions image digest (sha256:...). Required.
  --broker-digest <d>           Published Identity Broker image digest. Omit to skip the Broker Job.
  --handover auto|always|skip   Schema ownership handover. auto runs it once and records it. Default auto.
  --postgres-admin <upn>        Entra PostgreSQL administrator for the handover. Default: signed-in user.
  --reconcile-broker-egress     Re-apply the repository Broker egress policy when it has drifted.
  --deploy-apps                 Roll the Environment, Orchestrator, and EventsAndSessions Deployments to the same
                                digests in this run, before or after the migrations as the schema
                                support of each image requires, and wait until they are ready.
  --apps-only                   Skip identity, handover, and migration Jobs. Only roll the three
                                Deployments to the given digests under the lock and wait until they
                                are ready. Use it when the schema is already migrated and services are
                                unready because they still run older images.
  --readiness-timeout-seconds <n>  Wait for each service rollout. Default 600.
  --prepare-only                Stop after the identity, ServiceAccount, egress, and handover. No
                                digests, probes, or migration Jobs. The setup command uses it.
  --release-lock <runId>        Remove a lock left by a crashed run with that exact run ID, then exit.
  --receipt-dir <path>          Receipt directory. Default ~/.agentweaver/db-maintenance (outside the checkout).
  --job-timeout-seconds <n>     Per-Job wait. Default 900.
`;

/** Parses flags. Unknown or malformed flags stop the run before anything else happens. */
export function parseDbMaintenanceArgs(argv) {
  const options = { execute: false, handover: 'auto', reconcileBrokerEgress: false, deployApps: false, appsOnly: false, prepareOnly: false,
    receiptDir: join(homedir(), '.agentweaver', 'db-maintenance'), jobTimeoutSeconds: 900, readinessTimeoutSeconds: 600, help: false };
  const valued = new Map([
    ['--environment-digest', 'environmentDigest'], ['--orchestrator-digest', 'orchestratorDigest'],
    ['--events-digest', 'eventsDigest'],
    ['--broker-digest', 'brokerDigest'], ['--handover', 'handover'], ['--postgres-admin', 'postgresAdmin'],
    ['--release-lock', 'releaseLock'], ['--receipt-dir', 'receiptDir'], ['--job-timeout-seconds', 'jobTimeoutSeconds'],
    ['--readiness-timeout-seconds', 'readinessTimeoutSeconds'],
  ]);
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    if (arg === '--help' || arg === '-h') options.help = true;
    else if (arg === '--execute') options.execute = true;
    else if (arg === '--reconcile-broker-egress') options.reconcileBrokerEgress = true;
    else if (arg === '--deploy-apps') options.deployApps = true;
    else if (arg === '--apps-only') options.appsOnly = true;
    else if (arg === '--prepare-only') options.prepareOnly = true;
    else {
      // Accept both "--name value" and "--name=value"; a missing value is an error, never a default.
      const [name, inline] = arg.includes('=') ? [arg.slice(0, arg.indexOf('=')), arg.slice(arg.indexOf('=') + 1)] : [arg];
      if (!valued.has(name)) throw new Error(`Unknown argument "${arg}".`);
      const value = inline ?? argv[++index];
      if (!value || value.startsWith('--')) throw new Error(`${name} requires a value.`);
      options[valued.get(name)] = value;
    }
  }
  if (options.help) return options;
  options.jobTimeoutSeconds = Number(options.jobTimeoutSeconds);
  if (!Number.isInteger(options.jobTimeoutSeconds) || options.jobTimeoutSeconds < 60 || options.jobTimeoutSeconds > 3600)
    throw new Error('--job-timeout-seconds must be an integer from 60 to 3600.');
  options.readinessTimeoutSeconds = Number(options.readinessTimeoutSeconds);
  if (!Number.isInteger(options.readinessTimeoutSeconds) || options.readinessTimeoutSeconds < 60 || options.readinessTimeoutSeconds > 3600)
    throw new Error('--readiness-timeout-seconds must be an integer from 60 to 3600.');
  if (!['auto', 'always', 'skip'].includes(options.handover))
    throw new Error('--handover must be auto, always, or skip.');
  if (options.releaseLock !== undefined) {
    // Lock release is its own mode so it can never be mixed with a write run.
    if (!/^[0-9a-f]{12}$/.test(options.releaseLock)) throw new Error('--release-lock needs the 12-character run ID from the lock.');
    return options;
  }
  if (options.prepareOnly) {
    // Prepare-only never runs a Job or touches a service, so image options have no meaning.
    const conflicting = [
      [options.environmentDigest !== undefined, '--environment-digest'],
      [options.orchestratorDigest !== undefined, '--orchestrator-digest'], [options.eventsDigest !== undefined, '--events-digest'],
      [options.brokerDigest !== undefined, '--broker-digest'], [options.deployApps, '--deploy-apps'],
      [options.appsOnly, '--apps-only'],
    ].filter(([present]) => present).map(([, flag]) => flag);
    if (conflicting.length) throw new Error(`--prepare-only cannot be combined with ${conflicting.join(', ')}.`);
    return options;
  }
  for (const [option, key] of [['--environment-digest', 'environmentDigest'],
    ['--orchestrator-digest', 'orchestratorDigest'], ['--events-digest', 'eventsDigest']])
    if (!DIGEST.test(options[key] ?? '')) throw new Error(`${option} must be an exact sha256 image digest.`);
  if (options.brokerDigest !== undefined && !DIGEST.test(options.brokerDigest))
    throw new Error('--broker-digest must be an exact sha256 image digest.');
  // Apps-only touches nothing in the database or its network path, so every flag that would
  // reach those is rejected rather than silently ignored.
  if (options.appsOnly) {
    const conflicting = [
      [options.brokerDigest !== undefined, '--broker-digest'], [options.reconcileBrokerEgress, '--reconcile-broker-egress'],
      [options.deployApps, '--deploy-apps'],
      [argv.some(arg => arg === '--handover' || arg.startsWith('--handover=')), '--handover'],
      [options.postgresAdmin !== undefined, '--postgres-admin'],
    ].filter(([present]) => present).map(([, flag]) => flag);
    if (conflicting.length) throw new Error(`--apps-only cannot be combined with ${conflicting.join(', ')}.`);
  }
  return options;
}

/** Builds the pinned image reference for a published component digest. */
export function imageReference(component, digest) {
  return `${DB_MAINTENANCE_TARGET.registry}/${componentImageRepository(component)}@${digest}`;
}

/**
 * Parses a migration Job log. Success needs exactly one grants marker for the expected service,
 * schema, role, and plan, and a ledger equal to the reviewed versions. Anything else fails.
 */
export function verifyGrantsMarker(log, { service, schema, versions }) {
  const lines = String(log).split(/\r?\n/).filter(line => line.startsWith(`${GRANTS_MARKER} `));
  if (lines.length !== 1) throw new Error(`${service} Job printed ${lines.length} grant markers; exactly one is required.`);
  const fields = Object.fromEntries(lines[0].slice(GRANTS_MARKER.length + 1).split(' ').map(part => {
    const at = part.indexOf('=');
    return [part.slice(0, at), part.slice(at + 1)];
  }));
  const expectedRole = `agentweaver-id-${service}`;
  if (fields.service !== service || fields.schema !== schema || fields.role !== expectedRole ||
      fields.planSha256 !== REVIEWED_PLAN_SHA256)
    throw new Error(`${service} Job marker does not match the reviewed plan: ${lines[0]}`);
  // The ledger must equal the plan exactly. A missing version means an incomplete run; an extra
  // version means the database moved past the reviewed plan.
  if (fields.versions !== versions.join(','))
    throw new Error(`${service} ledger [${fields.versions}] differs from the reviewed plan [${versions.join(',')}].`);
  return lines[0];
}

/**
 * Compares a live egress spec with the repository spec. Returns undefined when they match, or a
 * short report: whether the policy is missing, and the DNS names only one side allows.
 */
export function describeEgressDrift(live, desired) {
  if (live === undefined) return { missing: true };
  // Key order differs between the API server and the rendered file, so compare sorted JSON.
  const canonical = value => JSON.stringify(value, (_key, item) => item && typeof item === 'object' && !Array.isArray(item)
    ? Object.fromEntries(Object.entries(item).sort(([left], [right]) => left.localeCompare(right))) : item);
  if (canonical(live) === canonical(desired)) return undefined;
  const names = spec => new Set(JSON.stringify(spec).match(/"match(?:Name|Pattern)":"[^"]+"/g) ?? []);
  const liveNames = names(live);
  const desiredNames = names(desired);
  const value = entry => entry.slice(entry.indexOf(':') + 2, -1);
  return {
    extraDnsNames: [...liveNames].filter(entry => !desiredNames.has(entry)).map(value).sort(),
    missingDnsNames: [...desiredNames].filter(entry => !liveNames.has(entry)).map(value).sort(),
  };
}

/** Reads the image of each P1 service Deployment and compares it with the digest being migrated to. */
function checkAppImages(kubectl, options) {
  return P1_JOBS.map(job => {
    const expected = imageReference(job.component, options[job.digestOption]);
    const result = kubectl(['get', 'deployment', job.key, '-o', 'jsonpath={.spec.template.spec.containers[0].image}']);
    if (result.status !== 0 && !/NotFound/i.test(result.stderr))
      throw new Error(`Could not read Deployment ${job.key}: ${result.stderr}`);
    const running = result.status === 0 ? result.stdout.trim() || undefined : undefined;
    // A missing Deployment has no pods whose readiness a schema move could break.
    const status = running === undefined ? 'absent' : running === expected ? 'match' : 'mismatch';
    return { deployment: job.key, component: job.component, expected, running, status };
  });
}

/**
 * Reads the schema range a service accepts from its migrator source. A schema version is the
 * highest applied migration of that service. The readiness check rejects any version outside its
 * `NOT IN (...)` list, so the highest listed version is the most the image accepts. It also
 * requires each version it checks with `WHERE version = N)`, except versions it also accepts as
 * absent (`= 0`), such as Orchestrator 032, 033, and 035. Migration catalogs are explicit and may
 * contain gaps; an absent version is never inferred from the numeric range. The highest required
 * version is the least the image accepts. Returns undefined when the source does not have that
 * shape, so an unexpected file never produces a guessed range.
 */
export function schemaRangeFromSource(source, component) {
  const owner = OWNER_SOURCES[component];
  if (!owner || typeof source !== 'string') return undefined;
  if (component === 'Agentweaver.Environment') {
    // EF migration IDs map to ordered numeric versions, and startup rejects pending migrations.
    const current = source.match(/private const int CurrentSchemaVersion = (\d+);/);
    if (!current || !source.includes('pending.Length > 0')) return undefined;
    const version = Number(current[1]);
    return Number.isSafeInteger(version) && version > 0
      ? { minSchema: version, maxSchema: version }
      : undefined;
  }
  // Exactly one allow-list is expected; zero or several means the shape changed.
  const allowLists = [...source.matchAll(new RegExp(`${owner.table} WHERE version NOT IN \\(([\\d, ]+)\\)`, 'g'))];
  if (allowLists.length !== 1) return undefined;
  const allowed = allowLists[0][1].split(',').map(Number);
  const checked = new Set([...source.matchAll(new RegExp(`${owner.table} WHERE version = (\\d+)\\)`, 'g'))]
    .map(match => Number(match[1])));
  const optional = new Set([...source.matchAll(new RegExp(`${owner.table} WHERE version = (\\d+)\\) = 0`, 'g'))]
    .map(match => Number(match[1])));
  const required = [...checked].filter(version => !optional.has(version));
  if (!required.length) return undefined;
  const range = { minSchema: Math.max(...required), maxSchema: Math.max(...allowed) };
  // The least accepted version must itself be allowed, or the two rules contradict each other.
  return allowed.includes(range.minSchema) && range.minSchema <= range.maxSchema ? range : undefined;
}

/**
 * Finds the schema range of a published owner image from the image itself. Every published
 * service image carries its source commit in the `org.opencontainers.image.revision` label. The
 * runner reads that label from the registry, reads the owner's migrator source at that commit
 * with git, and parses the range (see schemaRangeFromSource). Nothing is maintained by hand.
 * Returns `{ minSchema, maxSchema, sourceSha }`, or `{ unknown: reason }` when any step fails;
 * the caller decides whether unknown is acceptable.
 */
export async function readImageSchemaRange({ image, component, readImage, exec, repoRoot }) {
  const owner = OWNER_SOURCES[component];
  // Only digest references to the target registry have a label the runner can trust.
  if (!owner || typeof image !== 'string' || !image.startsWith(`${DB_MAINTENANCE_TARGET.registry}/`) ||
      !DIGEST.test(image.slice(image.indexOf('@') + 1)))
    return { unknown: `${image} is not a published ${DB_MAINTENANCE_TARGET.registry} digest reference` };
  let sourceSha;
  try {
    ({ sourceSha } = await readImage(image));
  } catch (error) {
    return { unknown: `could not read the image labels: ${redact(error.message)}` };
  }
  if (!/^[0-9a-f]{40}$/.test(sourceSha ?? '')) return { unknown: 'the image has no source commit label' };
  // The commit is on v1, which the preflight fetched. Fetch it by name only when it is missing,
  // for example in a shallow clone.
  const hasCommit = () => exec('git', ['cat-file', '-e', `${sourceSha}^{commit}`], { cwd: repoRoot }).status === 0;
  if (!hasCommit()) exec('git', ['fetch', '--quiet', '--no-tags', 'origin', sourceSha], { cwd: repoRoot });
  const shown = exec('git', ['show', `${sourceSha}:${owner.file}`], { cwd: repoRoot });
  if (shown.status !== 0) return { unknown: `could not read ${owner.file} at ${sourceSha}`, sourceSha };
  const range = schemaRangeFromSource(shown.stdout, component);
  return range ? { ...range, sourceSha } : { unknown: `could not parse the readiness check at ${sourceSha}`, sourceSha };
}

/**
 * Chooses, for each service on another image, when to deploy its target image so it stays ready:
 * - migrate-first: the running image accepts the target schema, so the migration does not affect
 *   it, and the deploy (if any) can follow.
 * - deploy-first: the target image accepts the current schema, so deploying before the migration
 *   keeps the service ready on both sides.
 * - gap: neither image accepts the other's schema. The service is unready from the migration
 *   until its target image is ready. This is a planned readiness gap, not an error.
 * An image whose range is unknown accepts nothing as far as this function knows. The runner
 * never passes an unknown range for a target image (see runDbMaintenance), even when it already
 * runs that digest. Only a running image can be unknown. The current schema comes from the state
 * ConfigMap; when it was never recorded, deploy-first is not chosen.
 */
export function planServiceOrder({ appImages, ranges, targetSchema, currentSchema }) {
  const within = (range, version) => range !== undefined && version !== undefined &&
    version >= range.minSchema && version <= range.maxSchema;
  const services = appImages.map(app => {
    const target = targetSchema[app.deployment];
    const current = currentSchema[app.deployment];
    const targetSupports = ranges[app.expected];
    // A target image that rejects the plan's own schema was built from other source: wrong digest.
    if (!within(targetSupports, target))
      throw new Error(`${app.deployment} image ${app.expected} accepts schema ` +
        `${targetSupports ? `${targetSupports.minSchema}-${targetSupports.maxSchema}` : 'unknown'}, ` +
        `not ${target} from the reviewed plan. Pass the digest built from the reviewed source.`);
    const runningSupports = app.running ? ranges[app.running] : undefined;
    const action = app.status !== 'mismatch' ? 'none'
      : within(runningSupports, target) ? 'migrate-first'
        : within(targetSupports, current) ? 'deploy-first'
          : 'gap';
    return { deployment: app.deployment, action, currentSchema: current ?? null, targetSchema: target,
      runningSupports: runningSupports ?? null, targetSupports: targetSupports ?? null };
  });
  const order = services.some(service => service.action === 'gap') ? 'planned-readiness-gap'
    : services.some(service => service.action === 'deploy-first') ? 'deploy-first' : 'migrate-first';
  return { order, services };
}

/**
 * Builds a short probe Job that carries a migration workload label, so the same egress policy
 * applies to it as to the migration Job. It resolves the PostgreSQL name (printing any CNAME such
 * as the private link name) and sends a PostgreSQL SSL request to port 5432. Any reply proves the
 * DNS answer and the TCP path are allowed; anything else fails the probe.
 */
export function connectivityProbeJob({ name, workload, runId, host, namespace }) {
  const script = [
    'set -u',
    'host="$1"',
    'out=$(nslookup "$host" 2>&1) || { echo "$out"; echo "PROBE_DNS_FAILED host=$host"; exit 1; }',
    'echo "$out"',
    'command -v nc >/dev/null 2>&1 || { echo "PROBE_TOOL_MISSING nc"; exit 1; }',
    // Bytes of the PostgreSQL SSLRequest message. The server answers with one byte, S or N.
    'reply=$(printf \'\\000\\000\\000\\010\\004\\322\\026\\057\' | nc -w 5 "$host" 5432 2>/dev/null | head -c 1)',
    'case "$reply" in S|N) echo "' + CONNECTIVITY_MARKER + ' host=$host port=5432";; ' +
      '*) echo "PROBE_TCP_FAILED host=$host port=5432"; exit 1;; esac',
  ].join('\n');
  const labels = { 'agentweaver.io/workload': workload, [RUN_LABEL]: runId };
  return {
    apiVersion: 'batch/v1', kind: 'Job',
    metadata: { name, namespace, labels },
    spec: {
      backoffLimit: 0, activeDeadlineSeconds: 120, ttlSecondsAfterFinished: 600,
      template: {
        metadata: { labels },
        spec: {
          restartPolicy: 'Never', automountServiceAccountToken: false,
          securityContext: { runAsNonRoot: true, runAsUser: 65534, runAsGroup: 65534, seccompProfile: { type: 'RuntimeDefault' } },
          containers: [{
            name: 'probe', image: PROBE_IMAGE, command: ['sh', '-c', script, 'probe', host],
            resources: { requests: { cpu: '25m', memory: '32Mi' }, limits: { cpu: '100m', memory: '32Mi' } },
            securityContext: { allowPrivilegeEscalation: false, readOnlyRootFilesystem: true, capabilities: { drop: ['ALL'] } },
          }],
        },
      },
    },
  };
}

/** Renders a kustomize directory to plain resources. */
/**
 * Reads the P1 migration identity and its AKS federated credential without changing anything.
 * ready is true only when the identity exists and one credential binds it to the exact
 * ServiceAccount through this cluster's OIDC issuer. Any read failure other than "not found"
 * stops the run, because guessing would let a CI run try a deployment it has no rights for.
 */
export function readMigrationIdentity(exec, target) {
  const shown = exec('az', ['identity', 'show', '--resource-group', target.resourceGroup,
    '--name', MIGRATION_IDENTITY, '-o', 'json']);
  if (shown.status !== 0) {
    if (/not ?found|could not be found/i.test(shown.stderr ?? '')) return { exists: false, ready: false };
    throw new Error(`Could not read ${MIGRATION_IDENTITY}: ${redact((shown.stderr ?? '').trim())}`);
  }
  const identity = JSON.parse(shown.stdout);
  if (!GUID.test(identity.clientId ?? '') || !GUID.test(identity.principalId ?? ''))
    throw new Error(`${MIGRATION_IDENTITY} has no client or principal ID.`);
  const issuer = exec('az', ['aks', 'show', '--resource-group', target.resourceGroup, '--name', target.clusterName,
    '--query', 'oidcIssuerProfile.issuerUrl', '-o', 'tsv']);
  if (issuer.status !== 0 || !issuer.stdout.trim()) throw new Error('Could not read the AKS OIDC issuer.');
  const credentials = exec('az', ['identity', 'federated-credential', 'list', '--resource-group', target.resourceGroup,
    '--identity-name', MIGRATION_IDENTITY, '-o', 'json']);
  if (credentials.status !== 0) throw new Error(`Could not read the federated credentials of ${MIGRATION_IDENTITY}.`);
  // The issuer is compared without a trailing slash, since Azure may store it either way.
  const trim = value => (value ?? '').replace(/\/+$/, '');
  const subject = `system:serviceaccount:${target.namespace}:p1-postgres-migration`;
  const federated = JSON.parse(credentials.stdout || '[]').some(credential =>
    trim(credential.issuer) === trim(issuer.stdout.trim()) && credential.subject === subject &&
    (credential.audiences ?? []).includes('api://AzureADTokenExchange'));
  return { exists: true, ready: federated, clientId: identity.clientId, principalObjectId: identity.principalId };
}

function render(exec, repoRoot, directory) {
  const result = exec('kubectl', ['kustomize', directory], { cwd: repoRoot });
  if (result.status !== 0) throw new Error(`Could not render ${directory}: ${result.stderr || result.stdout}`);
  return parseKustomizeResources(result.stdout).items;
}

/**
 * Prepares one uniquely named Job from a rendered template: pinned image, run label, and, for
 * P1 Jobs, the migration client ID. The template itself is never applied, so a placeholder
 * image or client ID cannot reach the cluster.
 */
export function prepareJob(template, { name, image, runId, clientId }) {
  const job = structuredClone(template);
  job.metadata.name = name;
  job.metadata.labels = { ...job.metadata.labels, [RUN_LABEL]: runId };
  job.spec.template.metadata.labels = { ...job.spec.template.metadata.labels, [RUN_LABEL]: runId };
  // The Broker Job has no TTL in its documented manifest; give every runner Job the same backstop.
  job.spec.ttlSecondsAfterFinished ??= 3600;
  const container = job.spec.template.spec.containers[0];
  container.image = image;
  if (clientId) {
    for (const entry of container.env ?? [])
      if (entry.name.endsWith('__WorkloadIdentity__ClientId')) entry.value = clientId;
  }
  return job;
}

/** Creates the default dependencies. Tests replace them to run fully offline. */
function defaultDependencies() {
  return {
    exec: (command, args, options = {}) => run(command, args, { check: false, timeout: 120_000, ...options }),
    sleep: milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)),
    now: () => new Date(),
    newRunId: () => randomUUID().replaceAll('-', '').slice(0, 12),
    relay: bootstrapIdentityPostgres,
    writeReceipt: (path, receipt) => writeFileSync(path, `${JSON.stringify(receipt, null, 2)}\n`),
    log: message => console.log(message),
  };
}

/**
 * Runs the maintenance path. Returns the receipt. Throws after writing a failed receipt when any
 * step fails; cleanup always runs first.
 */
export async function runDbMaintenance(options, overrides = {}) {
  const deps = { ...defaultDependencies(), ...overrides };
  const repoRoot = deps.repoRoot ?? process.cwd();
  const { exec, log } = deps;
  // Reads an image's accepted schema range from its source label and the source at that commit.
  // Tests replace it, or only the registry read, to stay offline.
  deps.imageSchemaRange ??= (image, component) => readImageSchemaRange({ image, component,
    readImage: deps.readImage ?? (reference => verifyServiceRegistryImage(reference)), exec, repoRoot });
  const target = DB_MAINTENANCE_TARGET;
  const runId = deps.newRunId();
  const receipt = {
    runId, mode: options.releaseLock ? 'release-lock' : options.execute ? 'execute' : 'dry-run',
    scope: options.appsOnly ? 'apps-only' : options.prepareOnly ? 'prepare' : 'migrations',
    startedAt: deps.now().toISOString(), target: { ...target }, planSha256: REVIEWED_PLAN_SHA256,
    steps: [], cleanup: {}, installation: { status: 'partial' }, result: 'running',
  };
  const step = (name, detail = {}) => { receipt.steps.push({ name, at: deps.now().toISOString(), ...detail }); log(`- ${name}`); };
  const directory = mkdtempSync(join(tmpdir(), 'agentweaver-db-maintenance-'));
  const kubeconfig = join(directory, 'kubeconfig');
  const kubectl = (args, extra = {}) => exec('kubectl', ['--kubeconfig', kubeconfig, '--namespace', target.namespace, ...args], extra);
  const must = (result, action) => {
    if (result.status !== 0) throw new Error(`${action}: ${redact((result.stderr || result.stdout || `exit ${result.status}`).trim())}`);
    return result;
  };
  let lockHeld = false;
  const createdJobs = [];
  let primaryError;

  try {
    // ---- Preflight. Everything here is read-only; the first failure stops the run. ----
    // 1. Source: a clean tree at a commit already on v1, so the run applies only merged code.
    must(exec('git', ['diff', '--quiet', 'HEAD', '--'], { cwd: repoRoot }), 'The working tree has uncommitted changes');
    const untracked = exec('git', ['ls-files', '--others', '--exclude-standard'], { cwd: repoRoot });
    if (untracked.status !== 0 || untracked.stdout.trim())
      throw new Error('The working tree has untracked files. Run from a clean checkout of v1.');
    const head = must(exec('git', ['rev-parse', 'HEAD'], { cwd: repoRoot }), 'Could not read HEAD').stdout.trim();
    must(exec('git', ['fetch', '--quiet', 'origin', 'v1'], { cwd: repoRoot }), 'Could not fetch origin/v1');
    must(exec('git', ['merge-base', '--is-ancestor', head, 'origin/v1'], { cwd: repoRoot }),
      `HEAD ${head} is not on origin/v1. Only merged v1 code may run`);
    receipt.sourceSha = head;
    step('source-verified', { sourceSha: head });

    // 2. Locked restore: the exact package graph in the lock files, or nothing.
    for (const project of LOCKED_PROJECTS)
      must(exec('dotnet', ['restore', project, '--locked-mode', '--verbosity', 'quiet'], { cwd: repoRoot, timeout: 600_000 }),
        `Locked restore failed for ${project}`);
    step('locked-restore-verified', { projects: LOCKED_PROJECTS });

    // 3. Plan: the committed plan bytes, the generator, and the pinned hash must all agree.
    const committedPlan = JSON.parse(readFileSync(join(repoRoot, PLAN_FILE), 'utf8'));
    const committedPlanHash = p1PostgresPlanHash(committedPlan);
    if (committedPlanHash !== REVIEWED_PLAN_SHA256 || p1PostgresPlanHash() !== REVIEWED_PLAN_SHA256)
      throw new Error('The committed P1 plan differs from the reviewed plan hash.');
    step('plan-verified');

    // 4. Azure account and cluster access, through a private temporary kubeconfig.
    const account = JSON.parse(must(exec('az', ['account', 'show', '-o', 'json']), 'Azure account lookup').stdout);
    if (account.id !== target.subscriptionId || account.tenantId !== target.tenantId)
      throw new Error('The signed-in Azure account is not on the approved subscription and tenant.');
    receipt.operator = account.user?.name;
    must(exec('az', ['aks', 'get-credentials', '--resource-group', target.resourceGroup, '--name', target.clusterName,
      '--subscription', target.subscriptionId, '--file', kubeconfig]), 'Could not get AKS credentials');
    // The kubelogin login mode follows the Azure CLI sign-in, which works for a person and for
    // the workflow's federated login alike.
    must(exec('kubelogin', ['convert-kubeconfig', '--login', 'azurecli', '--kubeconfig', kubeconfig]),
      'Could not configure kubelogin');
    // The Role restricts Deployment access by name, so preflight each target with the same name.
    const required = options.appsOnly ? APP_PERMISSIONS
      : options.deployApps ? [...PERMISSIONS, ...['environment', 'orchestrator', 'events-and-sessions']
        .map(name => ['patch', 'deployments.apps', undefined, name])] : PERMISSIONS;
    const missing = required.filter(([verb, resource, sub, resourceName]) => {
      const target = sub ? `${resource}/${sub}` : resource;
      // kubectl auth can-i uses TYPE/NAME to match the Role's resourceNames rule.
      const args = ['auth', 'can-i', verb, resourceName ? `${target}/${resourceName}` : target];
      return kubectl(args).stdout.trim() !== 'yes';
    });
    if (missing.length)
      throw new Error(`Missing Kubernetes permissions: ${missing.map(([verb, resource, sub, resourceName]) =>
        `${verb} ${resourceName ? `${resource}/${resourceName}` : sub ? `${resource}/${sub}` : resource}`).join(', ')}`);
    step('cluster-access-verified');

    // 5. Templates render and match the reviewed shape.
    const p1Resources = render(exec, repoRoot, P1_DIRECTORY);
    const brokerResources = render(exec, repoRoot, BROKER_DIRECTORY);
    const pick = (resources, kind, name) => {
      const found = resources.find(item => item.kind === kind && item.metadata?.name === name);
      if (!found) throw new Error(`Rendered manifests are missing ${kind} ${name}.`);
      return found;
    };
    const serviceAccount = pick(p1Resources, 'ServiceAccount', 'p1-postgres-migration');
    const p1Egress = pick(p1Resources, 'CiliumNetworkPolicy', 'p1-postgres-migration-egress');
    const brokerEgress = pick(brokerResources, 'CiliumNetworkPolicy', 'identity-broker-migration-egress');
    const brokerJobTemplate = pick(brokerResources, 'Job', 'identity-broker-migration');

    // 6. Broker egress drift: report it, and fix it only on request. The live spec is compared as
    // JSON rather than with kubectl diff, which needs an external diff program that Windows lacks.
    // Apps-only never reaches the database network path, so it skips steps 6 and 7.
    if (!options.appsOnly) {
      const liveEgress = kubectl(['get', 'ciliumnetworkpolicy', brokerEgress.metadata.name, '-o', 'json']);
      if (liveEgress.status !== 0 && !/NotFound/i.test(liveEgress.stderr))
        throw new Error(`Could not read the Broker egress policy: ${liveEgress.stderr}`);
      const liveSpec = liveEgress.status === 0 ? JSON.parse(liveEgress.stdout).spec : undefined;
      const egressDrift = describeEgressDrift(liveSpec, brokerEgress.spec);
      receipt.brokerEgressDrift = egressDrift !== undefined;
      if (egressDrift) {
        receipt.brokerEgressDriftDetail = egressDrift;
        log(`Broker migration egress differs from the repository: ${JSON.stringify(egressDrift)}`);
        log('Reconcile it with: npm run db:maintenance -- --execute --reconcile-broker-egress <digest options>');
      }
      step('broker-egress-checked', { drift: receipt.brokerEgressDrift });

      // 7. The Broker Job reads live settings from this ConfigMap. Without it the Job cannot run.
      if (options.brokerDigest)
        must(kubectl(['get', 'configmap', 'identity-broker-migration-config', '-o', 'name']),
          'The Broker migration ConfigMap identity-broker-migration-config is missing');
    }

    // Lock release is a separate mode: remove only a lock held by the named run, then stop.
    if (options.releaseLock) {
      const lease = JSON.parse(must(kubectl(['get', 'lease', LOCK_NAME, '-o', 'json']), 'No lock to release').stdout);
      if (lease.spec?.holderIdentity !== options.releaseLock)
        throw new Error(`The lock is held by ${lease.spec?.holderIdentity}, not ${options.releaseLock}.`);
      must(kubectl(['delete', 'lease', LOCK_NAME]), 'Could not release the lock');
      step('stale-lock-released', { holder: options.releaseLock });
      receipt.result = 'succeeded';
      return receipt;
    }

    // 8. State ConfigMap: the recorded handover principal, the schema versions recorded by earlier
    // runs, and any roll-forward a failed run left behind. Read once, here, for every mode.
    const stateRead = kubectl(['get', 'configmap', STATE_NAME, '-o', 'json']);
    if (stateRead.status !== 0 && !/NotFound/i.test(stateRead.stderr ?? ''))
      throw new Error(`Could not read ${STATE_NAME}: ${redact((stateRead.stderr ?? '').trim())}`);
    const stateData = stateRead.status === 0 ? JSON.parse(stateRead.stdout).data ?? {} : {};

    // Migration identity and handover state, read-only. A person can create the identity and
    // run the handover; a workload identity, such as the workflow's, cannot. The handover needs the
    // PostgreSQL Entra administrator, because only that role can create the PostgreSQL principal
    // and only the current owner may give its objects away. So a run that would need either step
    // and is not signed in as a user stops here, before the lock, and names the setup command.
    let identityState;
    let handoverNeeded = false;
    if (!options.appsOnly) {
      identityState = readMigrationIdentity(exec, target);
      const recorded = stateData.handoverPrincipalObjectId;
      handoverNeeded = options.handover === 'always' ||
        (options.handover === 'auto' && (!identityState.exists || recorded !== identityState.principalObjectId));
      const signedInAsUser = account.user?.type === 'user';
      const missing = [...(identityState.ready ? [] : ['the migration identity']), ...(handoverNeeded ? ['the ownership handover'] : [])];
      if (missing.length && !signedInAsUser)
        throw new Error(`This run needs ${missing.join(' and ')}, which needs the PostgreSQL Entra administrator sign-in. ` +
          'Run once with that Azure CLI sign-in: npm run db:maintenance:setup -- --execute');
      step('migration-identity-checked', { ready: identityState.ready, handoverNeeded });
    }

    // 9. Running services and the order of deploy and migrate. Each service's readiness check
    // accepts only a range of schema versions, so the runner compares the running image, the
    // target image, the current schema, and the target schema, and picks the order that keeps
    // each service ready (see planServiceOrder). Prepare-only does not move the schema.
    receipt.appImages = options.prepareOnly ? [] : checkAppImages(kubectl, options);
    for (const app of receipt.appImages.filter(entry => entry.status === 'mismatch'))
      log(`Deployment ${app.deployment} runs ${app.running}, not ${app.expected}.`);

    // Roll-forward guard: a run that moved the schema and then failed to bring the services up
    // recorded the images it was deploying. Until they are ready, only those images may be
    // deployed. Older images reject the new schema, so rolling back is never an option.
    const pendingImages = Object.fromEntries(P1_JOBS.filter(job => stateData[`rollForward.${job.key}`])
      .map(job => [job.key, stateData[`rollForward.${job.key}`]]));
    if (!options.prepareOnly && Object.keys(pendingImages).length) {
      const differing = receipt.appImages.filter(app => pendingImages[app.deployment] && pendingImages[app.deployment] !== app.expected);
      if (differing.length)
        throw new Error(`Run ${stateData['rollForward.runId']} migrated the schema and must roll forward. ` +
          `Rerun with the same digests: ${differing.map(app => `${app.deployment}=${pendingImages[app.deployment]}`).join(', ')}. ` +
          'Rolling back to older images is not supported.');
      receipt.rollForward = { pendingFromRun: stateData['rollForward.runId'], images: pendingImages };
    }

    const plan = JSON.parse(readFileSync(join(repoRoot, PLAN_FILE), 'utf8'));
    let ordering = { order: 'migrate-first', services: [] };
    if (options.appsOnly) {
      // Apps-only rolls existing Deployments; it never creates one, so a missing one is an error.
      const absent = receipt.appImages.filter(entry => entry.status === 'absent');
      if (absent.length)
        throw new Error(`Deployment ${absent.map(app => app.deployment).join(', ')} does not exist. ` +
          'Install the services with the release deploy path first.');
    } else if (!options.prepareOnly) {
      // Read the accepted schema range of every target image from the image's own source label.
      // Matching digests still need this gate: an unchanged image can reject a newer schema.
      // A target image must have a known range: an unreadable label or source stops the run, because guessing could
      // pick an order that leaves a service unready. A running image may be unknown (for example
      // a tag or a foreign registry); then nothing is assumed about it.
      const ranges = {};
      for (const app of receipt.appImages) {
        const targetRange = await deps.imageSchemaRange(app.expected, app.component);
        if (targetRange.unknown)
          throw new Error(`Could not read the schema range of ${app.deployment} image ${app.expected}: ${targetRange.unknown}.`);
        ranges[app.expected] = { minSchema: targetRange.minSchema, maxSchema: targetRange.maxSchema };
        if (!app.running || app.running === app.expected) continue;
        const runningRange = await deps.imageSchemaRange(app.running, app.component);
        if (runningRange.unknown) log(`Schema range of running ${app.deployment} image is unknown: ${runningRange.unknown}.`);
        else ranges[app.running] = { minSchema: runningRange.minSchema, maxSchema: runningRange.maxSchema };
      }
      const targetSchema = Object.fromEntries(P1_JOBS.map(job => [job.key, Math.max(...plan[job.planVersions])]));
      // Unrecorded or malformed versions count as unknown, which only removes the deploy-first choice.
      const currentSchema = Object.fromEntries(P1_JOBS.map(job => {
        const value = Number(stateData[`${job.key}.schema`]);
        return [job.key, Number.isInteger(value) && value > 0 ? value : undefined];
      }));
      ordering = planServiceOrder({ appImages: receipt.appImages, ranges, targetSchema, currentSchema });
      receipt.serviceOrder = ordering;
      // Deploy-first and a planned gap both depend on deploying the target images in this run.
      // Without --deploy-apps the run would leave a service unready, so it stops here.
      const needsDeploy = ordering.services.filter(service => ['deploy-first', 'gap'].includes(service.action));
      if (needsDeploy.length && !options.deployApps)
        throw new Error(`Running services use other images (${needsDeploy.map(service => service.deployment).join(', ')}) ` +
          'that do not accept the target schema. ' +
          (ordering.order === 'planned-readiness-gap'
            ? 'This run has a planned readiness gap, so it must deploy the matching digests right after the migrations. '
            : 'The target images accept the current schema, so this run must deploy them before the migrations. ') +
          'Rerun with --deploy-apps.');
      if (ordering.order === 'planned-readiness-gap')
        log(`Planned readiness gap: ${ordering.services.filter(service => service.action === 'gap')
          .map(service => service.deployment).join(', ')} will be unready from the migration until the matching images are ready.`);
    }
    step('app-images-checked', { order: ordering.order,
      mismatched: receipt.appImages.filter(entry => entry.status === 'mismatch').map(app => app.deployment) });
    const deployFirst = ordering.services.filter(service => service.action === 'deploy-first').map(service => service.deployment);
    const deployAfter = options.deployApps
      ? ordering.services.filter(service => ['migrate-first', 'gap'].includes(service.action)).map(service => service.deployment) : [];

    if (!options.execute) {
      // Dry run ends here, with the plan of record for an execute run.
      const mismatched = receipt.appImages.filter(entry => entry.status === 'mismatch');
      receipt.plannedSteps = options.appsOnly
        ? ['lock', ...(mismatched.length ? ['deploy-apps'] : []), 'readiness', 'cleanup', 'receipt']
        : options.prepareOnly
          ? ['lock', `migration-identity-${identityState.ready ? 'verified' : 'deploy'}`, 'serviceaccount-and-egress',
            ...(receipt.brokerEgressDrift && options.reconcileBrokerEgress ? ['broker-egress-reconcile'] : []),
            `handover-${handoverNeeded ? 'run' : 'skip'}`, 'cleanup', 'receipt']
          : [
          'lock', `migration-identity-${identityState.ready ? 'verified' : 'deploy'}`, 'serviceaccount-and-egress',
          ...(receipt.brokerEgressDrift && options.reconcileBrokerEgress ? ['broker-egress-reconcile'] : []),
          `handover-${handoverNeeded ? 'run' : 'skip'}`, 'connectivity-probes',
          ...(deployFirst.length ? ['deploy-apps-before-migrations'] : []),
            ...(options.brokerDigest ? ['broker-job'] : []), 'environment-job', 'orchestrator-job', 'events-and-sessions-job',
          ...(deployAfter.length ? ['deploy-apps-after-migrations'] : []), 'readiness', 'cleanup', 'receipt',
        ];
      log(`Dry run passed. With --execute the runner would: ${receipt.plannedSteps.join(', ')}.`);
      receipt.result = 'dry-run-passed';
      return receipt;
    }

    // ---- Execute. ----
    // Lock: creating a Lease is atomic, so a second run fails here before it changes anything.
    const lease = {
      apiVersion: 'coordination.k8s.io/v1', kind: 'Lease',
      metadata: { name: LOCK_NAME, namespace: target.namespace, labels: { [RUN_LABEL]: runId } },
      spec: { holderIdentity: runId, acquireTime: deps.now().toISOString().replace(/\.\d+Z$/, '.000000Z') },
    };
    const lock = kubectl(['create', '-f', '-'], { input: JSON.stringify(lease) });
    if (lock.status !== 0) {
      const holder = kubectl(['get', 'lease', LOCK_NAME, '-o', 'jsonpath={.spec.holderIdentity} {.spec.acquireTime}']);
      throw new Error(`Another maintenance run holds ${LOCK_NAME} (${holder.stdout.trim() || 'unknown holder'}). ` +
        'Wait for it, or if that run crashed, run with --release-lock <its run ID>.');
    }
    lockHeld = true;
    step('lock-acquired', { lease: LOCK_NAME });

    // Merges keys into the state ConfigMap, creating it when missing. A null value removes a key.
    // A merge patch keeps every key this call does not name, so the handover record, the schema
    // versions, and the roll-forward record never overwrite one another.
    const updateState = (data, action) => {
      const existing = kubectl(['get', 'configmap', STATE_NAME, '-o', 'name']);
      if (existing.status === 0) {
        must(kubectl(['patch', 'configmap', STATE_NAME, '--type', 'merge', '-p', JSON.stringify({ data })]), action);
        return;
      }
      if (!/NotFound/i.test(existing.stderr ?? '')) throw new Error(`${action}: ${redact((existing.stderr ?? '').trim())}`);
      must(kubectl(['create', '-f', '-'], { input: JSON.stringify({ apiVersion: 'v1', kind: 'ConfigMap',
        metadata: { name: STATE_NAME, namespace: target.namespace },
        data: Object.fromEntries(Object.entries(data).filter(([, value]) => value !== null)) }) }), action);
    };
    const clearRollForward = () => {
      updateState(Object.fromEntries([...P1_JOBS.map(job => `rollForward.${job.key}`), 'rollForward.runId', 'rollForward.since']
        .map(key => [key, null])), 'Could not clear the roll-forward record');
      step('roll-forward-cleared');
    };

    // Sets the pinned image on the named Deployments. Only the service container, which is named
    // after its Deployment, changes; a future sidecar or init container keeps its own image.
    const deployImages = deployments => {
      for (const app of receipt.appImages.filter(entry => deployments.includes(entry.deployment))) {
        must(kubectl(['set', 'image', `deployment/${app.deployment}`, `${app.deployment}=${app.expected}`]),
          `Could not set the image of Deployment ${app.deployment}`);
        step('app-image-set', { deployment: app.deployment, from: app.running, to: app.expected });
        app.deployed = true;
      }
    };
    // Waits, within the readiness time limit, until every existing service is ready on the image
    // it should run: the pinned digest when this run deployed it, otherwise the image it ran.
    // Readiness is the end-to-end check, because each readiness probe verifies its schema.
    // After the schema moved, a failure tells the operator to roll forward, never back.
    const waitReady = ({ afterMigration }) => {
      for (const app of receipt.appImages.filter(entry => entry.status !== 'absent')) {
        const wanted = app.deployed || app.status === 'match' ? app.expected : app.running;
        const rollout = kubectl(['rollout', 'status', `deployment/${app.deployment}`, `--timeout=${options.readinessTimeoutSeconds}s`]);
        if (rollout.status !== 0)
          throw new Error(`Deployment ${app.deployment} did not become ready within ${options.readinessTimeoutSeconds} seconds: ` +
            `${redact((rollout.stderr || rollout.stdout || '').trim())}` + (afterMigration
            ? '. The schema is already migrated. Do not roll back; rerun the same command to roll forward to the same digests.' : ''));
        const image = must(kubectl(['get', 'deployment', app.deployment, '-o',
          'jsonpath={.spec.template.spec.containers[0].image}']), `Could not read Deployment ${app.deployment}`).stdout.trim();
        if (image !== wanted) throw new Error(`Deployment ${app.deployment} runs ${image}, not ${wanted}.`);
        step('app-ready', { deployment: app.deployment, image });
      }
    };

    // Apps-only: the schema is already where these images expect it, so only the services move.
    // A rerun on matching images just re-checks readiness. Success clears any roll-forward record.
    if (options.appsOnly) {
      deployImages(receipt.appImages.filter(entry => entry.status === 'mismatch').map(app => app.deployment));
      waitReady({ afterMigration: Object.keys(pendingImages).length > 0 });
      if (Object.keys(pendingImages).length) clearRollForward();
      receipt.result = 'succeeded';
      return receipt;
    }

    // Migration identity and federated credential. When preflight found both in place, the run
    // uses them as they are, so a workload identity needs no Azure deployment rights. Otherwise a
    // person deploys the declarative template; a rerun of it changes nothing. A brand-new
    // federated credential needs time before AKS tokens are accepted.
    let deployment;
    if (identityState.ready) {
      deployment = { name: MIGRATION_IDENTITY, clientId: identityState.clientId,
        principalObjectId: identityState.principalObjectId, serviceAccount: 'p1-postgres-migration' };
    } else {
      const tags = JSON.parse(must(exec('az', ['group', 'show', '--name', target.resourceGroup,
        '--query', 'tags', '-o', 'json']), 'Could not read resource group tags').stdout || '{}') ?? {};
      deployment = JSON.parse(must(exec('az', ['deployment', 'group', 'create',
        '--resource-group', target.resourceGroup, '--name', 'p1-postgres-migration-identity',
        '--template-file', IDENTITY_TEMPLATE, '--parameters', `tags=${JSON.stringify(tags)}`, `sourceSha=${head}`,
        '--query', 'properties.outputs.migrationIdentity.value', '-o', 'json'], { cwd: repoRoot, timeout: 600_000 }),
      'Migration identity deployment failed').stdout);
    }
    if (deployment.name !== MIGRATION_IDENTITY || !GUID.test(deployment.clientId ?? '') ||
        !GUID.test(deployment.principalObjectId ?? '') || deployment.serviceAccount !== 'p1-postgres-migration')
      throw new Error('The migration identity deployment returned an unexpected identity.');
    receipt.migrationIdentity = { clientId: deployment.clientId, principalObjectId: deployment.principalObjectId };
    step('migration-identity-ready', { created: !identityState.exists, deployed: !identityState.ready });
    if (!identityState.ready) await deps.sleep(60_000);

    // ServiceAccount with the real client ID, and the P1 egress. Server-side apply is idempotent.
    const migrationAccount = structuredClone(serviceAccount);
    migrationAccount.metadata.annotations['azure.workload.identity/client-id'] = deployment.clientId;
    for (const [resource, label] of [[migrationAccount, 'ServiceAccount'], [p1Egress, 'egress policy']])
      must(kubectl(['apply', '--server-side', '--field-manager', 'agentweaver-db-maintenance', '-f', '-'],
        { input: JSON.stringify(resource) }), `Could not apply the P1 migration ${label}`);
    step('serviceaccount-and-egress-applied');

    if (receipt.brokerEgressDrift && options.reconcileBrokerEgress) {
      must(kubectl(['apply', '--server-side', '--force-conflicts', '--field-manager', 'agentweaver-db-maintenance', '-f', '-'],
        { input: JSON.stringify(brokerEgress) }), 'Could not reconcile the Broker egress policy');
      step('broker-egress-reconciled');
    }

    // Ownership handover. auto runs it once per migration principal and records that in a
    // ConfigMap; the Jobs still prove ownership because they fail without it. Preflight decided
    // whether it is needed and that the signed-in account can run it.
    if (handoverNeeded) {
      const principal = name => {
        const value = must(exec('az', ['identity', 'show', '--resource-group', target.resourceGroup, '--name', name,
          '--query', 'principalId', '-o', 'tsv']), `Could not read ${name}`).stdout.trim();
        if (!GUID.test(value)) throw new Error(`${name} has no principal ID.`);
        return value;
      };
      // The relay creates its own pod and ConfigMap, verifies the Broker bootstrap read-only,
      // runs the handover, and always removes what it created.
      const relay = await deps.relay({
        repoRoot, resourceGroup: target.resourceGroup, subscriptionId: target.subscriptionId,
        tenantId: target.tenantId, clusterName: target.clusterName, postgresHost: target.postgresHost,
        adminUsername: options.postgresAdmin ?? account.user?.name,
        runtimePrincipalObjectId: principal('agentweaver-id-identity-broker'),
        migrationPrincipalObjectId: principal('agentweaver-id-identity-broker-migration'),
        verifyOnly: true,
        p1OwnershipHandover: { migrationPrincipalObjectId: deployment.principalObjectId },
      });
      receipt.handover = { readback: relay.p1OwnershipHandover?.readback, relayCleanup: relay.cleanup };
      updateState({ handoverPrincipalObjectId: deployment.principalObjectId, handoverRunId: runId,
        handoverAt: deps.now().toISOString() }, 'Could not record the handover state');
      step('ownership-handover-verified');
    } else {
      step('ownership-handover-skipped', { reason: options.handover === 'skip' ? 'requested' : 'already recorded' });
    }

    // Prepare-only ends once the identity, its network path, and schema ownership are in place.
    if (options.prepareOnly) {
      receipt.result = 'succeeded';
      return receipt;
    }

    // Connectivity probes: before any migration Job, prove that each egress policy in use admits
    // DNS for the PostgreSQL name (with its CNAME chain) and TCP 5432. A policy change since the
    // last good run shows up here, not as a half-finished migration.
    const workloads = [...(options.brokerDigest ? ['identity-broker-migration'] : []), 'p1-postgres-migration'];
    for (const workload of workloads) {
      const name = `${workload}-probe-${runId}`;
      must(kubectl(['create', '-f', '-'], { input: JSON.stringify(connectivityProbeJob({
        name, workload, runId, host: target.postgresHost, namespace: target.namespace })) }),
      `Could not create probe Job ${name}`);
      createdJobs.push(name);
      const outcome = await waitForJob(kubectl, deps, name, 180);
      const logs = kubectl(['logs', `job/${name}`, '--tail=-1']).stdout ?? '';
      if (outcome !== 'complete' || !logs.includes(`${CONNECTIVITY_MARKER} host=${target.postgresHost}`))
        throw new Error(`Connectivity probe under ${workload} egress failed (${outcome}):\n${redact(logs.trim())}`);
      // Keep the resolved CNAME chain, such as the private link name, as evidence.
      const canonicalNames = [...logs.matchAll(/canonical name = (\S+?)\.?$/gm)].map(match => match[1]);
      step('connectivity-verified', { workload, canonicalNames });
    }

    // Deploy-first services move to images that accept the current schema, so they stay ready
    // through the migration. They must be ready before any Job runs; if they are not, nothing in
    // the database has changed yet, unless an earlier run already moved it (roll-forward record).
    if (deployFirst.length) {
      deployImages(deployFirst);
      waitReady({ afterMigration: Object.keys(pendingImages).length > 0 });
    }

    // Migration Jobs run one at a time, in prerequisite order: Broker, Environment, Core, Events.
    const jobs = [
      ...(options.brokerDigest ? [{ key: 'identity-broker', template: brokerJobTemplate,
        image: imageReference('Agentweaver.Identity.Broker', options.brokerDigest) }] : []),
      ...P1_JOBS.map(job => ({ ...job, template: pick(p1Resources, 'Job', job.jobName),
        image: imageReference(job.component, options[job.digestOption]), clientId: deployment.clientId,
        versions: plan[job.planVersions] })),
    ];
    for (const job of jobs) {
      if (job.key === P1_JOBS[0].key) {
        // Before the schema moves, record the images this run will deploy afterwards. If the run
        // then stops, the next run may only roll forward to these images.
        if (deployAfter.length)
          updateState({ ...Object.fromEntries(receipt.appImages.filter(app => deployAfter.includes(app.deployment))
            .map(app => [`rollForward.${app.deployment}`, app.expected])), 'rollForward.runId': runId,
          'rollForward.since': deps.now().toISOString() }, 'Could not record the roll-forward images');
        // A planned readiness gap starts when the owner schema starts to move.
        if (ordering.order === 'planned-readiness-gap')
          receipt.readinessGap = { planned: true, deployments: ordering.services.filter(service => service.action === 'gap')
            .map(service => service.deployment), startedAt: deps.now().toISOString() };
      }
      const name = `${job.template.metadata.name}-${runId}`;
      const manifest = prepareJob(job.template, { name, image: job.image, runId, clientId: job.clientId });
      must(kubectl(['create', '-f', '-'], { input: JSON.stringify(manifest) }), `Could not create Job ${name}`);
      createdJobs.push(name);
      const outcome = await waitForJob(kubectl, deps, name, options.jobTimeoutSeconds);
      const logs = kubectl(['logs', `job/${name}`, '--tail=-1']).stdout ?? '';
      if (outcome !== 'complete') throw new Error(`Job ${name} ${outcome}. Last log lines:\n${redact(logs.split(/\r?\n/).slice(-20).join('\n'))}`);
      // The Broker migrator has no marker; its zero exit is the documented success signal.
      const marker = job.versions ? verifyGrantsMarker(logs, { service: job.key, schema: job.schema, versions: job.versions }) : undefined;
      step(`${job.key}-migrated`, { job: name, image: job.image, ...(marker ? { marker } : {}) });
    }
    // The verified markers prove each schema now equals the plan, so record its version. Later
    // runs use it to tell whether a target image accepts the current schema.
    updateState(Object.fromEntries(P1_JOBS.map(job => [`${job.key}.schema`, String(Math.max(...plan[job.planVersions]))])),
      'Could not record the schema versions');

    // Services that follow the migration: those whose running image already accepts the new
    // schema (no gap) and those in a planned readiness gap. Then every service must be ready.
    deployImages(deployAfter);
    // A failure here leaves the gap open in the receipt (no end time) and the roll-forward record
    // in place, so the next run can only finish the same deploy.
    waitReady({ afterMigration: true });
    if (receipt.readinessGap) {
      receipt.readinessGap.endedAt = deps.now().toISOString();
      receipt.readinessGap.durationSeconds = (Date.parse(receipt.readinessGap.endedAt) - Date.parse(receipt.readinessGap.startedAt)) / 1000;
    }
    if (deployAfter.length || Object.keys(pendingImages).length) clearRollForward();
    receipt.result = 'succeeded';
  } catch (error) {
    primaryError = error;
    receipt.result = 'failed';
    receipt.error = redact(error.message);
  } finally {
    // Cleanup always runs: Jobs (with their pods) first, then the lock, then local files.
    // Each failure is recorded and reported, and never hides the original error.
    const cleanupErrors = [];
    for (const name of createdJobs) {
      const removed = kubectl(['delete', 'job', name, '--cascade=foreground', '--ignore-not-found', '--wait=true', '--timeout=120s']);
      receipt.cleanup[`job/${name}`] = removed.status === 0;
      if (removed.status !== 0) cleanupErrors.push(`job ${name}`);
    }
    if (lockHeld) {
      // Delete the Lease only while it still names this run.
      const holder = kubectl(['get', 'lease', LOCK_NAME, '-o', 'jsonpath={.spec.holderIdentity}']).stdout?.trim();
      const released = holder === runId && kubectl(['delete', 'lease', LOCK_NAME]).status === 0;
      receipt.cleanup.lock = released;
      if (!released) cleanupErrors.push(`lease ${LOCK_NAME}`);
    }
    try { rmSync(directory, { recursive: true, force: true }); receipt.cleanup.localFiles = true; }
    catch { cleanupErrors.push('local temporary files'); }
    if (cleanupErrors.length) {
      receipt.cleanupErrors = cleanupErrors;
      if (receipt.result === 'succeeded') receipt.result = 'failed';
    }
    receipt.finishedAt = deps.now().toISOString();
    // Database maintenance changes only two schema owners; it cannot prove that the other
    // services and the authenticated Gateway checks are current.
    receipt.installation = { status: 'partial' };
    // The receipt is written on every path, so a failed run leaves the same evidence as a good one.
    // Receipts live outside the checkout by default, so they never dirty the tree for the next run.
    const receiptDirectory = resolve(repoRoot, options.receiptDir);
    mkdirSync(receiptDirectory, { recursive: true });
    receipt.receiptPath = join(receiptDirectory, `${runId}.json`);
    deps.writeReceipt(receipt.receiptPath, receipt);
    log(`Receipt: ${receipt.receiptPath} (${receipt.result}); installation remains ${receipt.installation.status}`);
    if (!primaryError && cleanupErrors.length) primaryError = new Error(`Cleanup failed: ${cleanupErrors.join(', ')}`);
    else if (primaryError && cleanupErrors.length) primaryError.message += `; cleanup also failed: ${cleanupErrors.join(', ')}`;
  }
  if (primaryError) throw primaryError;
  return receipt;
}

/**
 * Polls a Job until it completes, fails, hits a pull error, or times out. A pull error ends the
 * wait at once, since a wrong digest never recovers.
 */
async function waitForJob(kubectl, deps, name, timeoutSeconds) {
  const deadline = deps.now().getTime() + timeoutSeconds * 1000;
  for (let attempt = 0; ; attempt++) {
    const job = JSON.parse(kubectl(['get', 'job', name, '-o', 'json']).stdout || '{}');
    const conditions = job.status?.conditions ?? [];
    if (conditions.some(c => c.type === 'Complete' && c.status === 'True')) return 'complete';
    if (conditions.some(c => c.type === 'Failed' && c.status === 'True')) return 'failed';
    const pods = JSON.parse(kubectl(['get', 'pods', '-l', `job-name=${name}`, '-o', 'json']).stdout || '{}');
    const waiting = (pods.items ?? []).flatMap(pod => pod.status?.containerStatuses ?? [])
      .map(status => status.state?.waiting?.reason).find(reason => FAILED_PULL.has(reason));
    if (waiting) return `could not start its container (${waiting})`;
    if (deps.now().getTime() > deadline) return `did not finish within ${timeoutSeconds} seconds`;
    await deps.sleep(Math.min(5000 * (attempt + 1), 15_000));
  }
}
