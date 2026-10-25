import { randomUUID } from 'node:crypto';
import {
  mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync,
} from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import path from 'node:path';
import { parse as parseYaml } from 'yaml';
import { selectPublishedComponentImage } from '../../release/publication-image.mjs';
import { verifyServiceRegistryImage } from '../../release/registry.mjs';
import { bootstrapP1Postgres } from '../bootstrap-p1-postgres.mjs';
import { DB_MAINTENANCE_TARGET, LOCK_NAME } from './db-maintenance.mjs';
import { collectGrantStageReceipts, isOutsideCheckout } from './grant-stage-receipts.mjs';
import { run, redact } from './exec.mjs';
import { withP0UserKubeconfig } from './namespace-bootstrap.mjs';

const NAMESPACE = DB_MAINTENANCE_TARGET.namespace;
const COMPONENT = 'Agentweaver.Projects.Config';
const DATABASE_NAME = 'agentweaver';
const JOB_TEMPLATE = path.join('deploy', 'k8s', 'migrations', 'projects-config', 'job.yaml');
const RUN_LABEL = 'agentweaver.io/db-maintenance-run';
const SHA = /^[a-f0-9]{40}$/;
const DIGEST = /^sha256:[a-f0-9]{64}$/;
const PLAN_HASH = /^[a-f0-9]{64}$/;
const JOB_TIMEOUT_SECONDS = 900;
const RECEIPT_DIRECTORY = path.join(homedir(), '.agentweaver', 'db-maintenance');

export { collectGrantStageReceipts };
const TERMINAL_CONDITIONS = new Set(['Complete', 'Failed']);
const FAILED_PULL_REASONS = new Set([
  'ErrImagePull', 'ImagePullBackOff', 'InvalidImageName', 'CreateContainerConfigError',
]);

export const PROJECTS_CONFIG_MIGRATION_USAGE = `Usage: npm run db:maintenance:projects-config -- --publication <receipt.json> [options]

Without --execute the runner checks the publication, target access, and migration lock, then writes a plan receipt.
Run it from a clean checkout of the fetched origin/v1 tip.

  --publication <file>       Published release receipt containing the Projects & Config image. Required.
  --bootstrap-first-platform-admin
                             Run the approved one-shot PlatformAdmin bootstrap instead of migrations and grants.
  --execute                  Create a short-lived credential Secret and one Job. Migration mode applies
                             the planned runtime grants; bootstrap mode creates the first PlatformAdmin.
  --receipt-dir <directory>  Receipt directory outside the checkout. Default: ~/.agentweaver/db-maintenance.
  --help                     Show this help.

Execute reads these process environment variables and never prints their values:
  AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION
  AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN
`;

function fail(message) {
  throw new Error(`Projects & Config migration: ${message}`);
}

function safeDiagnostic(error, secrets = []) {
  let message = redact(error instanceof Error ? error.message : String(error));
  for (const secret of secrets) {
    if (typeof secret === 'string' && secret.length > 0) message = message.replaceAll(secret, '[redacted]');
  }
  return message;
}

function parseJson(text, name) {
  try {
    return JSON.parse(text);
  } catch (error) {
    fail(`${name} is not valid JSON: ${error.message}`);
  }
}

function git(exec, root, args, action) {
  const result = exec('git', args, { cwd: root, check: false });
  if (result.status !== 0) fail(`${action}: ${(result.stderr ?? '').trim()}`);
  return result.stdout;
}

function requireAncestor(exec, root, sourceSha, description) {
  if (exec('git', ['merge-base', '--is-ancestor', sourceSha, 'origin/v1'],
    { cwd: root, check: false }).status !== 0) {
    fail(`${description} ${sourceSha} is not an ancestor of origin/v1.`);
  }
}

export function parseProjectsConfigMigrationArgs(argv = []) {
  const options = {
    publicationPath: undefined, execute: false, bootstrapFirstPlatformAdmin: false,
    receiptDir: RECEIPT_DIRECTORY, help: false,
  };
  const valued = new Map([['--publication', 'publicationPath'], ['--receipt-dir', 'receiptDir']]);
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--help' || arg === '-h') options.help = true;
    else if (arg === '--execute') options.execute = true;
    else if (arg === '--bootstrap-first-platform-admin') options.bootstrapFirstPlatformAdmin = true;
    else {
      const separator = arg.indexOf('=');
      const name = separator < 0 ? arg : arg.slice(0, separator);
      if (!valued.has(name)) fail(`unknown argument "${arg}".`);
      const value = separator < 0 ? argv[++index] : arg.slice(separator + 1);
      if (typeof value !== 'string' || !value || value.startsWith('--')) fail(`${name} requires a value.`);
      options[valued.get(name)] = value;
    }
  }
  if (options.help) return options;
  if (typeof options.publicationPath !== 'string' || !options.publicationPath.trim() ||
      options.publicationPath.trim() !== options.publicationPath) {
    fail('--publication is required and must not contain surrounding whitespace.');
  }
  return options;
}

export async function resolveProjectsConfigPublicationImage(publication, {
  root, exec, verifyImage = verifyServiceRegistryImage,
}) {
  if (!publication || publication.status !== 'published' || !SHA.test(publication.sourceSha ?? '')) {
    fail('publication receipt is not a complete published receipt.');
  }
  requireAncestor(exec, root, publication.sourceSha, 'publication source');
  const manifest = parseJson(git(exec, root, [
    'show', `${publication.sourceSha}:releases/foundation.json`,
  ], 'could not read the source-bound release manifest'), 'source-bound release manifest');
  const selected = selectPublishedComponentImage(publication, manifest, COMPONENT, {
    root,
    label: 'projects-config',
    exec,
    git: (...args) => git(exec, root, args, `Git ${args.join(' ')}`),
  });
  requireAncestor(exec, root, selected.imageSourceSha, 'Projects & Config image source');
  const verified = await verifyImage(selected.image);
  if (!verified || !DIGEST.test(verified.digest ?? '') || verified.digest !== selected.digest ||
      verified.sourceSha !== selected.imageSourceSha || verified.sourceTree !== selected.imageSourceTree ||
      verified.version !== selected.version) {
    fail('Projects & Config image digest or source metadata differs from the publication receipt.');
  }
  return {
    publicationSourceSha: selected.sourceSha,
    imageSourceSha: selected.imageSourceSha,
    image: selected.image,
    digest: selected.digest,
    version: selected.version,
  };
}

function readCredentials() {
  const connection = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION?.trim();
  const accessToken = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN?.trim();
  if (!connection || !accessToken) {
    fail('execute requires the migration connection and access token in the documented environment variables.');
  }
  const allowedFields = new Set(['host', 'port', 'database', 'username', 'ssl mode']);
  const fields = new Map();
  for (const part of connection.split(';')) {
    if (!part.trim()) continue;
    const separator = part.indexOf('=');
    const key = separator < 0 ? '' : part.slice(0, separator).trim().toLowerCase();
    const value = separator < 0 ? '' : part.slice(separator + 1).trim();
    // Only canonical, unique fields are passed to Npgsql; aliases and TLS overrides are rejected.
    if (!allowedFields.has(key) || !value || fields.has(key)) {
      fail('migration connection may contain only unique Host, Port, Database, Username, and SSL Mode fields.');
    }
    fields.set(key, value);
  }
  if (fields.get('host') !== DB_MAINTENANCE_TARGET.postgresHost ||
      fields.get('database') !== DATABASE_NAME ||
      !fields.get('username') ||
      (fields.has('port') && fields.get('port') !== '5432') ||
      fields.get('ssl mode')?.toLowerCase() !== 'verifyfull') {
    fail('migration connection must be password-free and target the approved database with SSL Mode=VerifyFull.');
  }
  return { connection, accessToken };
}

const PLATFORM_ADMIN_BOOTSTRAP_CONFIG = [
  ['ProjectsConfig__Authentication__Authority', 'ProjectsConfig__Authentication__Authority'],
  ['ProjectsConfig__PrivilegedSetup__ApprovedIssuer', 'ProjectsConfig__PrivilegedSetup__ApprovedIssuer'],
  ['ProjectsConfig__PrivilegedSetup__ApprovedSubject', 'ProjectsConfig__PrivilegedSetup__ApprovedSubject'],
  ['ProjectsConfig__PrivilegedSetup__ApprovedTenantId', 'ProjectsConfig__PrivilegedSetup__ApprovedTenantId'],
];
const PLATFORM_ADMIN_BOOTSTRAP_MESSAGES = new Map([
  ['The first PlatformAdmin was created.', 'created'],
  ['The approved PlatformAdmin already exists; no changes were made.', 'already-present'],
  ['First PlatformAdmin bootstrap refused because another active PlatformAdmin exists.', 'conflict'],
  ['First PlatformAdmin bootstrap failed. Check the approved identity, migration settings, and database connectivity.', 'failed'],
]);
const PLATFORM_ADMIN_BOOTSTRAP_DIAGNOSTIC_PREFIX = 'PlatformAdmin bootstrap diagnostic: ';
const PLATFORM_ADMIN_BOOTSTRAP_DIAGNOSTIC_PATTERN =
  /^ExceptionType=[A-Za-z][A-Za-z0-9]{0,127}(?:; InnerExceptionType=[A-Za-z][A-Za-z0-9]{0,127})?(?:; (?:Category=invalid-bootstrap-configuration|SqlState=(?:unknown|[0-9A-Z]{5}); ConstraintName=(?:unknown|[A-Za-z_][A-Za-z0-9_.$-]{0,127}); TableName=(?:unknown|[A-Za-z_][A-Za-z0-9_.$-]{0,127})))?$/;

function prepareJob(template, { name, image, runId, secretName, bootstrapFirstPlatformAdmin = false }) {
  const job = structuredClone(template);
  job.metadata.name = name;
  job.metadata.namespace = NAMESPACE;
  job.metadata.labels = { ...job.metadata.labels, [RUN_LABEL]: runId };
  job.spec.template.metadata.labels = { ...job.spec.template.metadata.labels, [RUN_LABEL]: runId };
  const container = job.spec.template.spec.containers[0];
  container.image = image;
  for (const entry of container.env ?? []) {
    if (entry.valueFrom?.secretKeyRef) entry.valueFrom.secretKeyRef.name = secretName;
  }
  if (bootstrapFirstPlatformAdmin) {
    // Reuse the hardened migration Job template while changing only its command and approved identity inputs.
    container.command = ['dotnet', 'Agentweaver.Projects.Config.dll', '--bootstrap-first-platform-admin'];
    container.env ??= [];
    for (const [name, key] of PLATFORM_ADMIN_BOOTSTRAP_CONFIG) {
      container.env.push({
        name,
        valueFrom: {
          configMapKeyRef: { name: 'projects-config-runtime-config', key },
        },
      });
    }
  }
  return job;
}

function validateJob(job, expected) {
  if (job.kind !== 'Job' || job.metadata?.name !== expected.name ||
      job.metadata?.namespace !== NAMESPACE || job.spec?.template?.spec?.automountServiceAccountToken !== false) {
    fail('migration Job readback has an unexpected identity or service-account setting.');
  }
  if (expected.bootstrapFirstPlatformAdmin) {
    // Bind the privileged command to the pinned image and ConfigMap-only approved identity inputs.
    const container = job.spec?.template?.spec?.containers?.[0];
    const configured = new Map((container?.env ?? []).map(entry => [entry.name, entry.valueFrom?.configMapKeyRef]));
    if (container?.image !== expected.image || JSON.stringify(container?.command) !== JSON.stringify([
      'dotnet', 'Agentweaver.Projects.Config.dll', '--bootstrap-first-platform-admin',
    ]) || PLATFORM_ADMIN_BOOTSTRAP_CONFIG.some(([name, key]) => {
      const reference = configured.get(name);
      return reference?.name !== 'projects-config-runtime-config' || reference?.key !== key;
    })) {
      fail('PlatformAdmin bootstrap Job readback has an unexpected command or identity source.');
    }
  }
}

function verifyPlatformAdminBootstrapConfig(configMap) {
  // Refuse mutable or incomplete setup sources before creating a credential Secret or privileged Job.
  if (configMap?.kind !== 'ConfigMap' || configMap.metadata?.name !== 'projects-config-runtime-config' ||
      configMap.metadata?.namespace !== NAMESPACE || !configMap.immutable) {
    fail('the Projects & Config runtime ConfigMap has an unexpected identity or mutability setting.');
  }
  const values = new Map(PLATFORM_ADMIN_BOOTSTRAP_CONFIG.map(([, key]) => [key, configMap.data?.[key]]));
  if ([...values.values()].some(value => typeof value !== 'string' || !value.trim())) {
    fail('the Projects & Config runtime ConfigMap is missing approved bootstrap settings.');
  }
  const authority = values.get('ProjectsConfig__Authentication__Authority');
  const issuer = values.get('ProjectsConfig__PrivilegedSetup__ApprovedIssuer');
  if (authority !== issuer) fail('the approved bootstrap issuer does not match the authentication authority.');
  for (const setting of [
    'ProjectsConfig__PrivilegedSetup__ApprovedSubject',
    'ProjectsConfig__PrivilegedSetup__ApprovedTenantId',
  ]) {
    const value = values.get(setting);
    if (!/^[A-Za-z0-9._:-]{1,256}$/.test(value)) {
      fail(`the approved bootstrap setting ${setting} is invalid.`);
    }
  }
  try {
    const authorityUrl = new URL(authority);
    if (authorityUrl.protocol !== 'https:' || authorityUrl.username || authorityUrl.password ||
        authorityUrl.search || authorityUrl.hash) {
      fail('the approved bootstrap issuer must be an absolute HTTPS authority.');
    }
  } catch (error) {
    if (error.message.startsWith('Projects & Config migration:')) throw error;
    fail('the approved bootstrap issuer must be an absolute HTTPS authority.');
  }
}

function parsePlatformAdminBootstrapMessage(log) {
  // Keep arbitrary process output out of receipts by accepting only the formatter's metadata fields.
  const lines = log.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  const outcomes = lines.map(line => PLATFORM_ADMIN_BOOTSTRAP_MESSAGES.get(line)).filter(Boolean);
  if (outcomes.length !== 1) {
    fail('PlatformAdmin bootstrap Job did not return exactly one stable outcome message.');
  }
  const diagnostics = lines
    .filter(line => line.startsWith(PLATFORM_ADMIN_BOOTSTRAP_DIAGNOSTIC_PREFIX))
    .map(line => line.slice(PLATFORM_ADMIN_BOOTSTRAP_DIAGNOSTIC_PREFIX.length));
  if (diagnostics.length > 1 || (diagnostics.length === 1 &&
      (outcomes[0] !== 'failed' || !PLATFORM_ADMIN_BOOTSTRAP_DIAGNOSTIC_PATTERN.test(diagnostics[0])))) {
    fail('PlatformAdmin bootstrap Job returned unexpected diagnostic output.');
  }
  return { outcome: outcomes[0], diagnostic: diagnostics[0] };
}

function defaultDependencies() {
  return {
    exec: (command, args, options = {}) => run(command, args, { check: false, timeout: 120_000, ...options }),
    withKubeconfig: withP0UserKubeconfig,
    readFile: readFileSync,
    writeReceipt: (file, receipt) => writeFileSync(file, `${JSON.stringify(receipt, null, 2)}\n`),
    now: () => new Date(),
    sleep: milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)),
    newRunId: () => randomUUID().replaceAll('-', '').slice(0, 12),
    verifyImage: image => verifyServiceRegistryImage(image),
    applyRuntimeGrants: root => bootstrapP1Postgres({
      repoRoot: root, execute: true, mode: 'projects-config-grants',
    }),
    collectGrantStageReceipts,
    log: message => console.log(message),
  };
}

export async function runProjectsConfigMigration(options, overrides = {}) {
  const deps = { ...defaultDependencies(), ...overrides };
  const root = path.resolve(deps.root ?? process.cwd());
  const runId = deps.newRunId();
  const receiptDirectory = path.resolve(root, options.receiptDir ?? RECEIPT_DIRECTORY);
  // Receipts inside the checkout would make it dirty, and the next maintenance or rollout step
  // refuses a dirty checkout. Reject that before any receipt is written.
  if (!isOutsideCheckout(root, receiptDirectory)) {
    fail('--receipt-dir must be outside the source checkout.');
  }
  const bootstrapFirstPlatformAdmin = options.bootstrapFirstPlatformAdmin === true;
  const jobPrefix = bootstrapFirstPlatformAdmin
    ? 'projects-config-platform-admin-bootstrap'
    : 'projects-config-migration';
  const receiptPath = path.join(receiptDirectory, `${jobPrefix}-${runId}.json`);
  const receipt = {
    // Keep migration receipts at v2 for rollout consumers; bootstrap receipts have a separate v1 contract.
    schemaVersion: bootstrapFirstPlatformAdmin ? 1 : 2,
    runId,
    mode: options.execute ? 'execute' : 'dry-run',
    operation: bootstrapFirstPlatformAdmin ? 'bootstrap-first-platform-admin' : 'migration',
    result: 'running',
    sourceSha: null,
    publicationSourceSha: null,
    image: null,
    target: {
      subscriptionId: DB_MAINTENANCE_TARGET.subscriptionId,
      resourceGroup: DB_MAINTENANCE_TARGET.resourceGroup,
      clusterName: DB_MAINTENANCE_TARGET.clusterName,
      namespace: NAMESPACE,
    },
    steps: [],
    cleanup: {},
    startedAt: deps.now().toISOString(),
    receiptPath,
  };
  const secrets = [];
  let primaryError;
  let lockHeld = false;
  let secretCreated = false;
  let jobCreated = false;
  let grantStageStarted = false;

  const record = (name, detail = {}) => {
    // The step name goes last so a detail field can never replace it; the rollout reads step names.
    receipt.steps.push({ at: deps.now().toISOString(), ...detail, name });
    deps.log(`- ${name}`);
  };
  const must = (result, action) => {
    if (result.status !== 0) {
      throw new Error(`${action}: ${safeDiagnostic(
        new Error(result.stderr || result.stdout || `exit ${result.status}`), secrets,
      )}`);
    }
    return result;
  };

  try {
    const status = must(deps.exec('git', ['status', '--porcelain', '--untracked-files=all'],
      { cwd: root }), 'Could not inspect the source checkout');
    if (status.stdout.trim()) fail('the source checkout must be clean.');
    const head = must(deps.exec('git', ['rev-parse', 'HEAD'], { cwd: root }),
      'Could not read the source checkout').stdout.trim();
    if (!SHA.test(head)) fail('the source checkout HEAD is not a full Git commit SHA.');
    must(deps.exec('git', ['fetch', '--quiet', 'origin', 'v1'], { cwd: root }),
      'Could not refresh origin/v1');
    // A later maintenance script can operate on an earlier publication only when both are on approved v1.
    requireAncestor(deps.exec, root, head, 'deployment-tool source');
    // The runtime grant step runs only from the admitted v1 tip. Check that here, before any lock,
    // Secret, or Job exists, so a run never migrates and then stops short of applying the grants.
    const admitted = must(deps.exec('git', ['rev-parse', 'origin/v1'], { cwd: root }),
      'Could not read origin/v1').stdout.trim();
    if (head !== admitted) fail(`the source checkout must be the fetched origin/v1 tip ${admitted}, not ${head}.`);
    const publication = parseJson(deps.readFile(path.resolve(root, options.publicationPath), 'utf8'),
      'publication receipt');
    const image = await resolveProjectsConfigPublicationImage(publication, {
      root, exec: deps.exec, verifyImage: deps.verifyImage,
    });
    receipt.sourceSha = head;
    receipt.publicationSourceSha = image.publicationSourceSha;
    receipt.imageSourceSha = image.imageSourceSha;
    receipt.image = image.image;
    record('publication-image-verified', {
      publicationSourceSha: image.publicationSourceSha,
      imageSourceSha: image.imageSourceSha,
      image: image.image,
    });

    const jobText = deps.readFile(path.resolve(root, JOB_TEMPLATE), 'utf8');
    const jobTemplate = parseYaml(jobText);
    if (jobTemplate.kind !== 'Job' || jobTemplate.metadata?.name !== 'projects-config-migration') {
      fail('Projects & Config migration Job template has an unexpected identity.');
    }
    if (options.execute) {
      const credentials = readCredentials();
      secrets.push(credentials.connection, credentials.accessToken);
      receipt.plannedSteps = bootstrapFirstPlatformAdmin
        ? ['lock', 'verify-approved-identity-config', 'create-credential-secret',
          'create-platform-admin-bootstrap-job', 'verify-bootstrap-result',
          'delete-job-and-secret', 'release-lock', 'receipt']
        : ['lock', 'create-credential-secret', 'create-migration-job',
          'verify-job-completion', 'apply-runtime-grants', 'delete-job-and-secret', 'release-lock', 'receipt'];
    } else {
      receipt.plannedSteps = bootstrapFirstPlatformAdmin
        ? ['verify-migration-lock', 'verify-approved-identity-config', 'create-credential-secret',
          'create-platform-admin-bootstrap-job', 'verify-bootstrap-result',
          'delete-job-and-secret', 'release-lock', 'receipt']
        : ['verify-migration-lock', 'create-credential-secret', 'create-migration-job',
          'verify-job-completion', 'apply-runtime-grants', 'delete-job-and-secret', 'release-lock', 'receipt'];
    }

    await deps.withKubeconfig({
      resourceGroup: DB_MAINTENANCE_TARGET.resourceGroup,
      subscriptionId: DB_MAINTENANCE_TARGET.subscriptionId,
      clusterName: DB_MAINTENANCE_TARGET.clusterName,
    }, async baseArgs => {
      const kubectl = (args, settings = {}) => {
        const result = deps.exec('kubectl', [
          ...baseArgs, '--namespace', NAMESPACE, ...args,
        ], { cwd: root, check: false, timeout: 120_000, ...settings });
        if (result.status !== 0) {
          throw new Error(`kubectl ${args.join(' ')} failed: ${safeDiagnostic(
            new Error(result.stderr || result.stdout || `exit ${result.status}`), secrets,
          )}`);
        }
        return result;
      };
      let actionError;
      try {
        const lockRead = deps.exec('kubectl', [
          ...baseArgs, '--namespace', NAMESPACE, 'get', 'lease', LOCK_NAME, '--output=json',
        ], { cwd: root, check: false });
        if (lockRead.status === 0) {
          const lease = parseJson(lockRead.stdout, 'database maintenance lock');
          fail(`database maintenance lock is held by ${lease.spec?.holderIdentity ?? 'an unknown run'}.`);
        }
        if (!/NotFound/i.test(lockRead.stderr ?? '')) {
          fail(`could not verify the database maintenance lock: ${safeDiagnostic(new Error(lockRead.stderr ?? ''), secrets)}`);
        }
        record('migration-lock-checked');
        if (bootstrapFirstPlatformAdmin) {
          // Check the immutable approved identity source before planning or creating privileged work.
          const configMap = parseJson(
            kubectl(['get', 'configmap', 'projects-config-runtime-config', '--output=json']).stdout,
            'Projects & Config runtime ConfigMap');
          verifyPlatformAdminBootstrapConfig(configMap);
          record('bootstrap-identity-config-verified');
        }
        if (!options.execute) {
          receipt.result = 'dry-run-passed';
          return;
        }

        const lease = {
          apiVersion: 'coordination.k8s.io/v1',
          kind: 'Lease',
          metadata: { name: LOCK_NAME, namespace: NAMESPACE, labels: { [RUN_LABEL]: runId } },
          spec: { holderIdentity: runId, acquireTime: deps.now().toISOString().replace(/\.\d+Z$/, '.000000Z') },
        };
        kubectl(['create', '--filename', '-'], { input: JSON.stringify(lease) });
        lockHeld = true;
        record('migration-lock-acquired', { runId });

        const secretName = `${jobPrefix}-${runId}`;
        const secret = {
          apiVersion: 'v1',
          kind: 'Secret',
          metadata: { name: secretName, namespace: NAMESPACE, labels: { [RUN_LABEL]: runId } },
          type: 'Opaque',
          stringData: {
            'migration-connection': secrets[0],
            'access-token': secrets[1],
          },
        };
        kubectl(['create', '--filename', '-'], { input: JSON.stringify(secret) });
        secretCreated = true;
        record('credential-secret-created', { secret: secretName });

        const name = `${jobPrefix}-${runId}`;
        const job = prepareJob(jobTemplate, {
          name, image: image.image, runId, secretName, bootstrapFirstPlatformAdmin,
        });
        validateJob(job, { name, image: image.image, bootstrapFirstPlatformAdmin });
        kubectl(['create', '--filename', '-'], { input: JSON.stringify(job) });
        jobCreated = true;
        record(
          bootstrapFirstPlatformAdmin ? 'platform-admin-bootstrap-job-created' : 'migration-job-created',
          { job: name, image: image.image });
        if (bootstrapFirstPlatformAdmin) {
          const jobStatus = await waitForJob(kubectl, deps, name, {
            returnFailure: true,
            description: 'PlatformAdmin bootstrap',
            bootstrapFirstPlatformAdmin: true,
            image: image.image,
          });
          // Bootstrap command output is limited to stable result messages; raw pod logs are never persisted.
          const { outcome, diagnostic } = parsePlatformAdminBootstrapMessage(
            kubectl(['logs', `job/${name}`, '--tail=20']).stdout);
          receipt.bootstrap = { outcome };
          if (diagnostic) receipt.bootstrap.diagnostic = safeDiagnostic(diagnostic, secrets);
          record('platform-admin-bootstrap-result', { outcome });
          if (outcome === 'failed') {
            fail('First PlatformAdmin bootstrap command failed; review the sanitized diagnostic in the receipt.');
          }
          if (outcome === 'conflict') {
            fail('First PlatformAdmin bootstrap was refused because another active PlatformAdmin exists.');
          }
          if (jobStatus !== 'Complete' || !['created', 'already-present'].includes(outcome)) {
            receipt.bootstrap.outcome = 'failed';
            fail('PlatformAdmin bootstrap Job did not complete with a successful stable outcome.');
          }
          record('platform-admin-bootstrap-job-completed', { job: name, image: image.image });
          receipt.result = 'succeeded';
        } else {
          await waitForJob(kubectl, deps, name);
          record('migration-job-completed', { job: name, image: image.image });
        }

        if (!bootstrapFirstPlatformAdmin) {
          // The runtime role needs these grants after migration; keep them under the same lock and read back exact evidence.
          grantStageStarted = true;
          const grants = await deps.applyRuntimeGrants(root);
          const evidence = grants?.receipt?.p1Initializer;
          // Accept only the exact grant mode, run from this same source, with a readback marker and plan hash.
          if (grants?.executed !== true || grants.mode !== 'projects-config-grants' || grants.sourceSha !== head ||
              evidence?.verification !== 'projects-config-grants-applied' ||
              !PLAN_HASH.test(evidence.planSha256 ?? '') || evidence.planSha256 !== grants.planSha256) {
            fail('the runtime grant step did not prove exact Projects & Config grants for this source.');
          }
          receipt.grants = { status: 'exact', sourceSha: head, planSha256: evidence.planSha256 };
          record('runtime-grants-applied', { planSha256: evidence.planSha256 });
          receipt.result = 'succeeded';
        }
      } catch (error) {
        actionError = error;
      } finally {
        const cleanupErrors = [];
        if (jobCreated) {
          const name = `${jobPrefix}-${runId}`;
          const removed = deps.exec('kubectl', [
            ...baseArgs, '--namespace', NAMESPACE, 'delete', 'job', name,
            '--cascade=foreground', '--ignore-not-found', '--wait=true', '--timeout=120s',
          ], { cwd: root, check: false });
          receipt.cleanup.job = removed.status === 0;
          if (!receipt.cleanup.job) cleanupErrors.push('migration Job');
        }
        if (secretCreated) {
          const name = `${jobPrefix}-${runId}`;
          const removed = deps.exec('kubectl', [
            ...baseArgs, '--namespace', NAMESPACE, 'delete', 'secret', name, '--ignore-not-found',
          ], { cwd: root, check: false });
          receipt.cleanup.secret = removed.status === 0;
          if (!receipt.cleanup.secret) cleanupErrors.push('credential Secret');
        }
        if (lockHeld) {
          const current = deps.exec('kubectl', [
            ...baseArgs, '--namespace', NAMESPACE, 'get', 'lease', LOCK_NAME,
            '--output=jsonpath={.spec.holderIdentity}',
          ], { cwd: root, check: false });
          const released = current.status === 0 && current.stdout.trim() === runId &&
            deps.exec('kubectl', [
              ...baseArgs, '--namespace', NAMESPACE, 'delete', 'lease', LOCK_NAME,
            ], { cwd: root, check: false }).status === 0;
          receipt.cleanup.lock = released;
          if (!released) cleanupErrors.push('database maintenance lock');
        }
        if (grantStageStarted) {
          // The grant stage writes its receipt under the checkout's Temp directory, even when it fails.
          // Move it next to this receipt, then prove the checkout is clean again. Otherwise a rerun or
          // the whole-release rollout from this same checkout would refuse to start.
          try {
            receipt.grantStageReceipts = deps.collectGrantStageReceipts(root, receiptDirectory, runId);
            receipt.cleanup.grantStageReceipts = true;
          } catch (error) {
            receipt.cleanup.grantStageReceipts = false;
            cleanupErrors.push(`grant stage receipt (${safeDiagnostic(error, secrets)})`);
          }
          const status = deps.exec('git', ['status', '--porcelain', '--untracked-files=all'], { cwd: root });
          receipt.cleanup.sourceCheckout = status.status === 0 && !status.stdout.trim();
          if (!receipt.cleanup.sourceCheckout) cleanupErrors.push('source checkout left dirty');
        }
        if (cleanupErrors.length) {
          receipt.cleanupErrors = cleanupErrors;
          receipt.result = 'failed';
          const cleanupError = new Error(`Cleanup failed for ${cleanupErrors.join(', ')}.`);
          actionError = actionError
            ? new AggregateError([actionError, cleanupError], `${actionError.message}; ${cleanupError.message}`)
            : cleanupError;
        }
      }
      if (actionError) throw actionError;
    }, {
      requiredPermissions: [
        ['get', `namespace/${NAMESPACE}`],
        ['get', 'leases.coordination.k8s.io', NAMESPACE],
        ...(bootstrapFirstPlatformAdmin ? [['get', 'configmaps', NAMESPACE]] : []),
        ...(options.execute ? [
          ['create', 'leases.coordination.k8s.io', NAMESPACE],
          ['delete', 'leases.coordination.k8s.io', NAMESPACE],
          ['create', 'secrets', NAMESPACE],
          ['delete', 'secrets', NAMESPACE],
          ['create', 'jobs', NAMESPACE],
          ['get', 'jobs', NAMESPACE],
          ['delete', 'jobs', NAMESPACE],
          ['list', 'pods', NAMESPACE],
          ...(bootstrapFirstPlatformAdmin ? [['get', 'pods', NAMESPACE, 'log']] : []),
        ] : []),
      ],
      permissionContext: 'Projects & Config database migration',
      execKubectl: (args, settings) => deps.exec('kubectl', args, settings),
    });
  } catch (error) {
    primaryError = error;
    receipt.result = 'failed';
    receipt.error = safeDiagnostic(error, secrets);
  } finally {
    receipt.completedAt = deps.now().toISOString();
    try {
      mkdirSync(receiptDirectory, { recursive: true });
      deps.writeReceipt(receiptPath, receipt);
    } catch (error) {
      if (primaryError) {
        primaryError = new AggregateError([primaryError, error], 'Migration failed and its receipt could not be written.');
      } else {
        primaryError = error;
      }
    }
  }
  if (primaryError) throw new Error(
    `Projects & Config migration ${receipt.result}; receipt: ${receiptPath}; ${safeDiagnostic(primaryError, secrets)}`,
    { cause: primaryError },
  );
  deps.log(`Receipt: ${receiptPath} (${receipt.result}).`);
  return receipt;
}

async function waitForJob(kubectl, deps, name, {
  returnFailure = false,
  description = 'migration',
  bootstrapFirstPlatformAdmin = false,
  image,
} = {}) {
  const deadline = deps.now().getTime() + JOB_TIMEOUT_SECONDS * 1000;
  for (let attempt = 0; ; attempt += 1) {
    const job = parseJson(kubectl(['get', 'job', name, '--output=json']).stdout, 'migration Job readback');
    validateJob(job, { name, image, bootstrapFirstPlatformAdmin });
    const conditions = job.status?.conditions ?? [];
    const terminal = conditions.find(condition =>
      TERMINAL_CONDITIONS.has(condition.type) && condition.status === 'True');
    if (terminal?.type === 'Complete') return 'Complete';
    if (terminal?.type === 'Failed') {
      if (returnFailure) return 'Failed';
      // Migration logs can expose database diagnostics, so preserve its existing no-log behavior.
      fail(`migration Job ${name} failed; no credential or Job log was written.`);
    }
    const pods = parseJson(kubectl([
      'get', 'pods', '--selector', `job-name=${name}`, '--output=json',
    ]).stdout, 'migration Pod readback');
    if (pods.kind !== 'List' || !Array.isArray(pods.items)) fail('migration Pod inventory is invalid.');
    const pullFailure = pods.items.flatMap(pod => pod.status?.containerStatuses ?? [])
      .map(status => status.state?.waiting?.reason).find(reason => FAILED_PULL_REASONS.has(reason));
    if (pullFailure) fail(`migration Job ${name} could not start its container (${pullFailure}).`);
    if (deps.now().getTime() >= deadline) {
      fail(`${description} Job ${name} did not finish within ${JOB_TIMEOUT_SECONDS} seconds.`);
    }
    await deps.sleep(Math.min(5000 * (attempt + 1), 15_000));
  }
}
