import { randomUUID } from 'node:crypto';
import {
  mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync,
} from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import path from 'node:path';
import { parse as parseYaml } from 'yaml';
import { verifyServiceRegistryImage } from '../../release/registry.mjs';
import { DB_MAINTENANCE_TARGET, LOCK_NAME, dbMaintenanceTarget } from './db-maintenance.mjs';
import { installTarget } from './targets.mjs';
import { applyTargetValues, targetValuePairs } from '../../release/target-values.mjs';
import { isOutsideCheckout } from './checkout-paths.mjs';
import { run, redact } from './exec.mjs';
import { withP0UserKubeconfig } from './namespace-bootstrap.mjs';

const NAMESPACE = DB_MAINTENANCE_TARGET.namespace;
const DATABASE_NAME = 'agentweaver';
const JOB_TEMPLATE = path.join('deploy', 'k8s', 'migrations', 'projects-config', 'job.yaml');
const RUN_LABEL = 'agentweaver.io/db-maintenance-run';
const SHA = /^[a-f0-9]{40}$/;
const JOB_TIMEOUT_SECONDS = 900;
const RECEIPT_DIRECTORY = path.join(homedir(), '.agentweaver', 'db-maintenance');

const TERMINAL_CONDITIONS = new Set(['Complete', 'Failed']);
const FAILED_PULL_REASONS = new Set([
  'ErrImagePull', 'ImagePullBackOff', 'InvalidImageName', 'CreateContainerConfigError',
]);

export const PROJECTS_CONFIG_MIGRATION_USAGE = `Usage: npm run db:maintenance:projects-config -- --image <ghcr-image@sha256:digest> [options]

Without --execute the runner checks the image digest, target access, and migration lock, then writes a plan receipt.
Run it from a clean checkout of the fetched origin/v1 tip.

  --image <reference>        Immutable Projects & Config image reference. Required.
  --bootstrap-first-platform-admin
                             Run the approved one-shot PlatformAdmin bootstrap instead of migrations and grants.
  --bootstrap-first-tenant-admin
                             Run the approved one-shot TenantAdmin bootstrap instead of migrations and grants.
  --inspect-first-platform-admin
                             Read back the approved PlatformAdmin state and matching audit rows. This flag
                             runs the read-only inspection Job directly; --execute is neither needed nor allowed.
  --execute                  Execute migration or bootstrap. Migration mode creates a credential Secret;
                             bootstrap modes use the verified migration Workload Identity.
  --receipt-dir <directory>  Receipt directory outside the checkout. Default: ~/.agentweaver/db-maintenance.
  --resource-group <name>    Registered install target. Default: agentweaver.
  --help                     Show this help.

Execution and inspection read these process environment variables and never print their values:
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

function requireAncestor(exec, root, sourceSha, description) {
  if (exec('git', ['merge-base', '--is-ancestor', sourceSha, 'origin/v1'],
    { cwd: root, check: false }).status !== 0) {
    fail(`${description} ${sourceSha} is not an ancestor of origin/v1.`);
  }
}

export function parseProjectsConfigMigrationArgs(argv = []) {
  const options = {
    image: undefined, execute: false, bootstrapFirstTenantAdmin: false, bootstrapFirstPlatformAdmin: false,
    inspectFirstPlatformAdmin: false,
    receiptDir: RECEIPT_DIRECTORY, resourceGroup: 'agentweaver', help: false,
  };
  const valued = new Map([['--image', 'image'], ['--receipt-dir', 'receiptDir'], ['--resource-group', 'resourceGroup']]);
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--help' || arg === '-h') options.help = true;
    else if (arg === '--execute') options.execute = true;
    else if (arg === '--bootstrap-first-tenant-admin') options.bootstrapFirstTenantAdmin = true;
    else if (arg === '--bootstrap-first-platform-admin') options.bootstrapFirstPlatformAdmin = true;
    else if (arg === '--inspect-first-platform-admin') options.inspectFirstPlatformAdmin = true;
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
  if (!installTarget(options.resourceGroup)) fail('--resource-group must name a registered install target.');
  if (typeof options.image !== 'string' || !options.image.trim() || options.image.trim() !== options.image) {
    fail('--image is required and must not contain surrounding whitespace.');
  }
  if (!/^ghcr\.io\/sabbour\/agentweaver\.projects\.config@sha256:[a-f0-9]{64}$/.test(options.image)) {
    fail('--image must be an immutable Projects & Config GHCR reference.');
  }
  if ([options.bootstrapFirstTenantAdmin, options.bootstrapFirstPlatformAdmin, options.inspectFirstPlatformAdmin]
    .filter(Boolean).length > 1) {
    fail('TenantAdmin bootstrap, PlatformAdmin bootstrap, and PlatformAdmin inspection modes are mutually exclusive.');
  }
  if (options.inspectFirstPlatformAdmin && options.execute) {
    fail('--inspect-first-platform-admin runs directly and must not be combined with --execute.');
  }
  return options;
}

export async function resolveProjectsConfigImage(image, {
  verifyImage = verifyServiceRegistryImage,
} = {}) {
  const match = /^ghcr\.io\/sabbour\/agentweaver\.projects\.config@(sha256:[a-f0-9]{64})$/.exec(image ?? '');
  if (!match) fail('image must be an immutable Projects & Config GHCR reference.');
  const verified = await verifyImage(image);
  if (!verified || verified.digest !== match[1]) fail('Projects & Config image digest could not be verified.');
  return { image, digest: match[1] };
}

function readCredentials({ inspectFirstPlatformAdmin = false, postgresHost = DB_MAINTENANCE_TARGET.postgresHost } = {}) {
  const connection = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION?.trim();
  const accessToken = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN?.trim();
  if (!connection || !accessToken) {
    fail('no approved Projects & Config schema-owner migration identity is configured. The runtime identity cannot run DDL; configure an existing schema-owner migration identity before deployment.');
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
  if (fields.get('host') !== postgresHost ||
      fields.get('database') !== DATABASE_NAME ||
      !fields.get('username') ||
      (fields.has('port') && fields.get('port') !== '5432') ||
      fields.get('ssl mode')?.toLowerCase() !== 'verifyfull') {
    fail('migration connection must be password-free and target the approved database with SSL Mode=VerifyFull.');
  }
  // Inspection already requires verified TLS, so skip optional GSS encryption probing in its minimal image.
  const inspectionConnection = inspectFirstPlatformAdmin
    ? `${connection.replace(/;+$/, '')};GSS Encryption Mode=Disable`
    : connection;
  return { connection: inspectionConnection, accessToken };
}

const PLATFORM_ADMIN_BOOTSTRAP_CONFIG = [
  ['ProjectsConfig__Authentication__Authority', 'ProjectsConfig__Authentication__Authority'],
  ['ProjectsConfig__PrivilegedSetup__ApprovedIssuer', 'ProjectsConfig__PrivilegedSetup__ApprovedIssuer'],
  ['ProjectsConfig__PrivilegedSetup__ApprovedSubject', 'ProjectsConfig__PrivilegedSetup__ApprovedSubject'],
  ['ProjectsConfig__PrivilegedSetup__ApprovedTenantId', 'ProjectsConfig__PrivilegedSetup__ApprovedTenantId'],
];
const TENANT_ADMIN_BOOTSTRAP_MESSAGES = new Map([
  ['The first TenantAdmin was created.', 'created'],
  ['The approved TenantAdmin already exists; no changes were made.', 'already-present'],
  ['First TenantAdmin bootstrap refused because another active TenantAdmin exists for this tenant.', 'conflict'],
  ['First TenantAdmin bootstrap failed. Check the approved identity, migration settings, and database connectivity.', 'failed'],
]);
const PLATFORM_ADMIN_BOOTSTRAP_MESSAGES = new Map([
  ['The first PlatformAdmin was created.', 'created'],
  ['The approved PlatformAdmin already exists; no changes were made.', 'already-present'],
  ['First PlatformAdmin bootstrap refused because another active PlatformAdmin exists.', 'conflict'],
  ['First PlatformAdmin bootstrap failed. Check the approved identity, migration settings, and database connectivity.', 'failed'],
]);
const PLATFORM_ADMIN_BOOTSTRAP_DIAGNOSTIC_PREFIX = 'PlatformAdmin bootstrap diagnostic: ';
const PLATFORM_ADMIN_BOOTSTRAP_DIAGNOSTIC_PATTERN =
  /^ExceptionType=[A-Za-z][A-Za-z0-9]{0,127}(?:; InnerExceptionType=[A-Za-z][A-Za-z0-9]{0,127})?(?:; (?:Category=invalid-bootstrap-configuration|SqlState=(?:unknown|[0-9A-Z]{5}); ConstraintName=(?:unknown|[A-Za-z_][A-Za-z0-9_.$-]{0,127}); TableName=(?:unknown|[A-Za-z_][A-Za-z0-9_.$-]{0,127})))?$/;
const TENANT_ADMIN_BOOTSTRAP_DIAGNOSTIC_PREFIX = 'TenantAdmin bootstrap diagnostic: ';
const PLATFORM_ADMIN_INSPECTION_FAILURE =
  'First PlatformAdmin inspection failed. Check the approved identity, migration settings, and database connectivity.';
const PLATFORM_ADMIN_INSPECTION_DIAGNOSTIC_PREFIX = 'PlatformAdmin inspection diagnostic: ';
const GUID_PATTERN = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const UTC_TIMESTAMP_PATTERN = '\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{6}Z';

function prepareJob(template, {
  name, image, runId, secretName, bootstrapFirstTenantAdmin = false,
  bootstrapFirstPlatformAdmin = false, inspectFirstPlatformAdmin = false, migrationClientId,
}) {
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
  if (bootstrapFirstTenantAdmin || bootstrapFirstPlatformAdmin || inspectFirstPlatformAdmin) {
    // Reuse the hardened migration Job template while changing only its command and approved identity inputs.
    container.command = [
      'dotnet',
      'Agentweaver.Projects.Config.dll',
      bootstrapFirstTenantAdmin ? '--bootstrap-first-tenant-admin'
        : inspectFirstPlatformAdmin ? '--inspect-first-platform-admin' : '--bootstrap-first-platform-admin',
    ];
    container.env ??= [];
    const workloadClientId = container.env.find(entry =>
      entry.name === 'ProjectsConfig__Migration__WorkloadIdentity__ClientId');
    if (!workloadClientId || !migrationClientId) {
      fail('Projects & Config bootstrap Job has no verified workload identity client ID.');
    }
    workloadClientId.value = migrationClientId;
    if (inspectFirstPlatformAdmin) {
      const connection = container.env.find(entry => entry.name === 'ConnectionStrings__ProjectsConfigMigration');
      if (typeof connection?.value !== 'string' || !connection.value.trim()) {
        fail('Projects & Config inspection Job has no migration connection string.');
      }
      connection.value = `${connection.value.replace(/;+$/, '')};GSS Encryption Mode=Disable`;
    }
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
  if (expected.migrationClientId &&
      job.spec?.template?.spec?.serviceAccountName !== 'projects-config-migration') {
    fail('Projects & Config bootstrap Job does not use the verified migration ServiceAccount.');
  }
  if (expected.bootstrapFirstTenantAdmin || expected.bootstrapFirstPlatformAdmin ||
      expected.inspectFirstPlatformAdmin) {
    const command = expected.bootstrapFirstTenantAdmin
      ? '--bootstrap-first-tenant-admin'
      : expected.inspectFirstPlatformAdmin
        ? '--inspect-first-platform-admin'
        : '--bootstrap-first-platform-admin';
    const container = job.spec?.template?.spec?.containers?.[0];
    const configured = new Map((container?.env ?? []).map(entry => [entry.name, entry.valueFrom?.configMapKeyRef]));
    const workloadIdentity = new Map((container?.env ?? []).map(entry => [entry.name, entry.value]));
    if (container?.image !== expected.image || JSON.stringify(container?.command) !== JSON.stringify([
      'dotnet', 'Agentweaver.Projects.Config.dll', command,
    ]) || job.spec?.template?.metadata?.labels?.['azure.workload.identity/use'] !== 'true' ||
      container?.env?.some(entry => entry.name === 'ProjectsConfig__Migration__AccessToken') ||
      workloadIdentity.get('ProjectsConfig__Migration__WorkloadIdentity__ClientId') !== expected.migrationClientId ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
        workloadIdentity.get('ProjectsConfig__Migration__WorkloadIdentity__TenantId') ?? '') ||
      workloadIdentity.get('ProjectsConfig__Migration__WorkloadIdentity__TokenFilePath') !==
        '/var/run/secrets/azure/tokens/azure-identity-token' ||
      (expected.inspectFirstPlatformAdmin &&
        !container?.env?.find(entry => entry.name === 'ConnectionStrings__ProjectsConfigMigration')
          ?.value?.endsWith(';GSS Encryption Mode=Disable')) ||
      PLATFORM_ADMIN_BOOTSTRAP_CONFIG.some(([name, key]) => {
        const reference = configured.get(name);
        return reference?.name !== 'projects-config-runtime-config' || reference?.key !== key;
      })) {
      fail('Projects & Config bootstrap Job readback has an unexpected command or identity source.');
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

function parseTenantAdminBootstrapMessage(log) {
  const lines = log.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  const outcomes = lines.map(line => TENANT_ADMIN_BOOTSTRAP_MESSAGES.get(line)).filter(Boolean);
  if (outcomes.length !== 1) {
    fail('TenantAdmin bootstrap Job did not return exactly one stable outcome message.');
  }
  const diagnostics = lines
    .filter(line => line.startsWith(TENANT_ADMIN_BOOTSTRAP_DIAGNOSTIC_PREFIX))
    .map(line => line.slice(TENANT_ADMIN_BOOTSTRAP_DIAGNOSTIC_PREFIX.length));
  if (diagnostics.length > 1 || (diagnostics.length === 1 &&
      (outcomes[0] !== 'failed' || !PLATFORM_ADMIN_BOOTSTRAP_DIAGNOSTIC_PATTERN.test(diagnostics[0])))) {
    fail('TenantAdmin bootstrap Job returned unexpected diagnostic output.');
  }
  return { outcome: outcomes[0], diagnostic: diagnostics[0] };
}

function stableOutputLines(log, operation) {
  const lines = log.split(/\r?\n/);
  if (lines.at(-1) === '') lines.pop();
  if (lines.length === 0 || lines.some(line => line.length === 0)) {
    fail(`PlatformAdmin ${operation} Job returned empty or ambiguous output.`);
  }
  return lines;
}

function parseInspectionCount(line, prefix) {
  const match = new RegExp(`^${prefix}\\|(0|[1-9][0-9]*)$`).exec(line);
  const count = match ? Number(match[1]) : Number.NaN;
  if (!Number.isSafeInteger(count)) fail('PlatformAdmin inspection Job returned invalid count output.');
  return count;
}

function isValidInspectionTimestamp(value) {
  const time = Date.parse(value);
  return Number.isFinite(time) && new Date(time).toISOString().slice(0, 19) === value.slice(0, 19);
}

function parsePlatformAdminInspectionOutput(log) {
  const lines = stableOutputLines(log, 'inspection');
  if (lines[0] === PLATFORM_ADMIN_INSPECTION_FAILURE) {
    if (lines.length !== 2 || !lines[1].startsWith(PLATFORM_ADMIN_INSPECTION_DIAGNOSTIC_PREFIX)) {
      fail('PlatformAdmin inspection Job returned unexpected failure output.');
    }
    const diagnostic = lines[1].slice(PLATFORM_ADMIN_INSPECTION_DIAGNOSTIC_PREFIX.length);
    if (!PLATFORM_ADMIN_BOOTSTRAP_DIAGNOSTIC_PATTERN.test(diagnostic)) {
      fail('PlatformAdmin inspection Job returned unexpected diagnostic output.');
    }
    return { outcome: 'failed', diagnostic };
  }

  let index = 0;
  if (lines[index++] !== 'PLATFORM_ADMIN_INSPECTION_V1') {
    fail('PlatformAdmin inspection Job did not return the expected versioned output.');
  }
  const membershipMatch = /^MEMBERSHIP\|(Absent)\|-\|0$/.exec(lines[index]) ??
    new RegExp(`^MEMBERSHIP\\|(Active)\\|(${GUID_PATTERN})\\|([1-9][0-9]*)$`).exec(lines[index]);
  if (!membershipMatch) fail('PlatformAdmin inspection Job returned invalid membership output.');
  index += 1;
  const membership = membershipMatch[1] === 'Absent'
    ? { state: 'absent' }
    : { state: 'active', membershipId: membershipMatch[2], revision: Number(membershipMatch[3]) };
  if (membership.state === 'active' && !Number.isSafeInteger(membership.revision)) {
    fail('PlatformAdmin inspection Job returned an invalid membership revision.');
  }

  const assignmentCount = parseInspectionCount(lines[index++], 'ACTIVE_ASSIGNMENT_COUNT');
  const assignments = [];
  const assignmentPattern = new RegExp(
    `^ASSIGNMENT\\|(${GUID_PATTERN})\\|([1-9][0-9]*)\\|(Active)\\|(Platform)\\|(default)\\|(PlatformAdmin)\\|(${GUID_PATTERN})$`);
  for (let row = 0; row < assignmentCount; row += 1) {
    const match = assignmentPattern.exec(lines[index++]);
    if (!match || !Number.isSafeInteger(Number(match[2]))) {
      fail('PlatformAdmin inspection Job returned invalid assignment output.');
    }
    assignments.push({
      assignmentId: match[1], revision: Number(match[2]), state: 'active',
      resourceType: match[4], resourceId: match[5], role: match[6], membershipId: match[7],
    });
  }

  const membershipAuditCount = parseInspectionCount(lines[index++], 'MEMBERSHIP_AUDIT_COUNT');
  const membershipAudits = [];
  const membershipAuditPattern = new RegExp(
    `^AUDIT\\|(membership_granted)\\|(-)\\|(-)\\|(${UTC_TIMESTAMP_PATTERN})\\|(${GUID_PATTERN})$`);
  for (let row = 0; row < membershipAuditCount; row += 1) {
    const match = membershipAuditPattern.exec(lines[index++]);
    if (!match || !isValidInspectionTimestamp(match[4])) {
      fail('PlatformAdmin inspection Job returned invalid membership audit output.');
    }
    membershipAudits.push({
      eventType: match[1], resourceType: null, resourceId: null,
      timestamp: match[4], membershipId: match[5],
    });
  }

  const roleAuditCount = parseInspectionCount(lines[index++], 'ROLE_ASSIGNMENT_AUDIT_COUNT');
  const roleAssignmentAudits = [];
  const roleAuditPattern = new RegExp(
    `^AUDIT\\|(role_assigned)\\|(Platform)\\|(default)\\|(PlatformAdmin)\\|(${UTC_TIMESTAMP_PATTERN})\\|(${GUID_PATTERN})\\|(${GUID_PATTERN})$`);
  for (let row = 0; row < roleAuditCount; row += 1) {
    const match = roleAuditPattern.exec(lines[index++]);
    if (!match || !isValidInspectionTimestamp(match[5])) {
      fail('PlatformAdmin inspection Job returned invalid role-assignment audit output.');
    }
    roleAssignmentAudits.push({
      eventType: match[1], resourceType: match[2], resourceId: match[3],
      role: match[4], timestamp: match[5], membershipId: match[6], assignmentId: match[7],
    });
  }

  const otherActivePlatformAdminCount = parseInspectionCount(
    lines[index++], 'OTHER_ACTIVE_PLATFORM_ADMIN_COUNT');
  if (index !== lines.length) fail('PlatformAdmin inspection Job returned extra output.');
  if (membership.state === 'absent' &&
      (assignments.length > 0 || membershipAudits.length > 0 || roleAssignmentAudits.length > 0)) {
    fail('PlatformAdmin inspection Job returned authority rows without an active approved membership.');
  }
  if (membership.state === 'active') {
    if (assignments.some(item => item.membershipId !== membership.membershipId) ||
        membershipAudits.some(item => item.membershipId !== membership.membershipId) ||
        roleAssignmentAudits.some(item => item.membershipId !== membership.membershipId ||
          !assignments.some(assignment => assignment.assignmentId === item.assignmentId))) {
      fail('PlatformAdmin inspection Job returned audit rows that do not match the approved membership.');
    }
    if (membershipAudits.length > 1 ||
        new Set(assignments.map(item => item.assignmentId)).size !== assignments.length ||
        new Set(roleAssignmentAudits.map(item => item.assignmentId)).size !== roleAssignmentAudits.length) {
      fail('PlatformAdmin inspection Job returned ambiguous duplicate authority rows.');
    }
  }
  return {
    outcome: 'inspected',
    membership,
    activeAssignments: assignments,
    membershipAudits,
    roleAssignmentAudits,
    otherActivePlatformAdminCount,
  };
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
    log: message => console.log(message),
  };
}

export async function runProjectsConfigMigration(options, overrides = {}) {
  const deps = { ...defaultDependencies(), ...overrides };
  const root = path.resolve(deps.root ?? process.cwd());
  const target = dbMaintenanceTarget(options.resourceGroup ?? 'agentweaver');
  const runId = deps.newRunId();
  const receiptDirectory = path.resolve(root, options.receiptDir ?? RECEIPT_DIRECTORY);
  // Receipts inside the checkout would make it dirty, and the next maintenance or rollout step
  // refuses a dirty checkout. Reject that before any receipt is written.
  if (!isOutsideCheckout(root, receiptDirectory)) {
    fail('--receipt-dir must be outside the source checkout.');
  }
  const bootstrapFirstTenantAdmin = options.bootstrapFirstTenantAdmin === true;
  const bootstrapFirstPlatformAdmin = options.bootstrapFirstPlatformAdmin === true;
  const inspectFirstPlatformAdmin = options.inspectFirstPlatformAdmin === true;
  if ([bootstrapFirstTenantAdmin, bootstrapFirstPlatformAdmin, inspectFirstPlatformAdmin].filter(Boolean).length > 1) {
    fail('TenantAdmin bootstrap, PlatformAdmin bootstrap, and PlatformAdmin inspection modes are mutually exclusive.');
  }
  if (inspectFirstPlatformAdmin && options.execute) {
    fail('PlatformAdmin inspection runs directly and must not be combined with --execute.');
  }
  const bootstrapFirstAdmin = bootstrapFirstTenantAdmin || bootstrapFirstPlatformAdmin;
  const usesMigrationWorkloadIdentity = bootstrapFirstAdmin || inspectFirstPlatformAdmin;
  const adminOperation = bootstrapFirstAdmin || inspectFirstPlatformAdmin;
  const executeJob = options.execute || inspectFirstPlatformAdmin;
  const jobPrefix = bootstrapFirstTenantAdmin
    ? 'projects-config-tenant-admin-bootstrap'
    : bootstrapFirstPlatformAdmin
      ? 'projects-config-platform-admin-bootstrap'
    : inspectFirstPlatformAdmin
      ? 'projects-config-platform-admin-inspection'
      : 'projects-config-migration';
  const receiptPath = path.join(receiptDirectory, `${jobPrefix}-${runId}.json`);
  const receipt = {
    // Keep migration receipts at v2 for rollout consumers; bootstrap receipts have a separate v1 contract.
    schemaVersion: adminOperation ? 1 : 2,
    runId,
    mode: inspectFirstPlatformAdmin ? 'read-only-inspection' : options.execute ? 'execute' : 'dry-run',
    operation: bootstrapFirstTenantAdmin ? 'bootstrap-first-tenant-admin'
      : bootstrapFirstPlatformAdmin ? 'bootstrap-first-platform-admin'
      : inspectFirstPlatformAdmin ? 'inspect-first-platform-admin' : 'migration',
    result: 'running',
    sourceSha: null,
    image: null,
    target: {
      subscriptionId: target.subscriptionId,
      resourceGroup: target.resourceGroup,
      clusterName: target.clusterName,
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
    // Migration plans and grants come from the current approved v1 source.
    requireAncestor(deps.exec, root, head, 'deployment-tool source');
    // The runtime grant step runs only from the admitted v1 tip. Check that here, before any lock,
    // Secret, or Job exists, so a run never migrates and then stops short of applying the grants.
    const admitted = must(deps.exec('git', ['rev-parse', 'origin/v1'], { cwd: root }),
      'Could not read origin/v1').stdout.trim();
    if (head !== admitted) fail(`the source checkout must be the fetched origin/v1 tip ${admitted}, not ${head}.`);
    const image = await resolveProjectsConfigImage(options.image, { verifyImage: deps.verifyImage });
    receipt.sourceSha = head;
    receipt.image = image.image;
    record('image-verified', { image: image.image, digest: image.digest });

    const jobText = applyTargetValues(deps.readFile(path.resolve(root, JOB_TEMPLATE), 'utf8'),
      targetValuePairs(root, target.resourceGroup));
    const jobTemplate = parseYaml(jobText);
    if (jobTemplate.kind !== 'Job' || jobTemplate.metadata?.name !== 'projects-config-migration') {
      fail('Projects & Config migration Job template has an unexpected identity.');
    }
    if (executeJob && !usesMigrationWorkloadIdentity) {
      const credentials = readCredentials({ inspectFirstPlatformAdmin, postgresHost: target.postgresHost });
      secrets.push(credentials.connection, credentials.accessToken);
      receipt.plannedSteps = ['lock', 'create-credential-secret', 'create-migration-job',
        'verify-job-completion', 'delete-job-and-secret', 'release-lock', 'receipt'];
    } else if (usesMigrationWorkloadIdentity) {
      receipt.plannedSteps = inspectFirstPlatformAdmin
        ? ['lock', 'verify-approved-identity-config', 'verify-migration-workload-identity',
          'create-platform-admin-inspection-job', 'verify-inspection-result',
          'delete-job', 'release-lock', 'receipt']
        : bootstrapFirstTenantAdmin
          ? ['lock', 'verify-approved-identity-config', 'verify-migration-workload-identity',
            'create-tenant-admin-bootstrap-job', 'verify-bootstrap-result',
            'delete-job', 'release-lock', 'receipt']
          : ['lock', 'verify-approved-identity-config', 'verify-migration-workload-identity',
            'create-platform-admin-bootstrap-job', 'verify-bootstrap-result',
            'delete-job', 'release-lock', 'receipt'];
    } else {
      receipt.plannedSteps = bootstrapFirstTenantAdmin
        ? ['verify-migration-lock', 'verify-approved-identity-config', 'verify-migration-workload-identity',
          'create-tenant-admin-bootstrap-job', 'verify-bootstrap-result', 'delete-job', 'release-lock', 'receipt']
        : bootstrapFirstPlatformAdmin
          ? ['verify-migration-lock', 'verify-approved-identity-config', 'verify-migration-workload-identity',
            'create-platform-admin-bootstrap-job', 'verify-bootstrap-result', 'delete-job', 'release-lock', 'receipt']
          : ['verify-migration-lock', 'create-credential-secret', 'create-migration-job',
            'verify-job-completion', 'delete-job-and-secret', 'release-lock', 'receipt'];
    }

    await deps.withKubeconfig({
      resourceGroup: target.resourceGroup,
      subscriptionId: target.subscriptionId,
      clusterName: target.clusterName,
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
        let migrationClientId;
        if (adminOperation) {
          // Check the immutable approved identity source before planning or creating privileged work.
          const configMap = parseJson(
            kubectl(['get', 'configmap', 'projects-config-runtime-config', '--output=json']).stdout,
            'Projects & Config runtime ConfigMap');
          verifyPlatformAdminBootstrapConfig(configMap);
          record('bootstrap-identity-config-verified');
          const serviceAccount = parseJson(
            kubectl(['get', 'serviceaccount', 'projects-config-migration', '--output=json']).stdout,
            'Projects & Config migration ServiceAccount');
          const serviceAccountClientId =
            serviceAccount.metadata?.annotations?.['azure.workload.identity/client-id'];
          const expectedClientId = must(deps.exec('az', [
            'identity', 'show', '--subscription', target.subscriptionId,
            '--resource-group', target.resourceGroup,
            '--name', `${target.resourceGroup}-id-projects-config-migration`,
            '--query', 'clientId', '-o', 'tsv',
          ], { cwd: root, check: false }), 'Could not verify the Projects & Config migration identity').stdout.trim();
          if (serviceAccount.kind !== 'ServiceAccount' ||
              serviceAccount.metadata?.name !== 'projects-config-migration' ||
              serviceAccount.metadata?.namespace !== NAMESPACE ||
              !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(expectedClientId) ||
              serviceAccountClientId !== expectedClientId) {
            fail('ServiceAccount/projects-config-migration does not name the approved migration identity.');
          }
          migrationClientId = serviceAccountClientId;
          record('migration-workload-identity-verified');
        }
        if (!executeJob) {
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
        if (!usesMigrationWorkloadIdentity) {
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
        }
        const name = `${jobPrefix}-${runId}`;
        const job = prepareJob(jobTemplate, {
          name, image: image.image, runId, secretName, migrationClientId,
          bootstrapFirstTenantAdmin, bootstrapFirstPlatformAdmin, inspectFirstPlatformAdmin,
        });
        validateJob(job, {
          name, image: image.image, bootstrapFirstTenantAdmin, bootstrapFirstPlatformAdmin,
          inspectFirstPlatformAdmin, migrationClientId,
        });
        kubectl(['create', '--filename', '-'], { input: JSON.stringify(job) });
        jobCreated = true;
        record(
          bootstrapFirstPlatformAdmin ? 'platform-admin-bootstrap-job-created'
            : inspectFirstPlatformAdmin ? 'platform-admin-inspection-job-created' : 'migration-job-created',
          { job: name, image: image.image });
        if (bootstrapFirstTenantAdmin) {
          const jobStatus = await waitForJob(kubectl, deps, name, {
            returnFailure: true,
            description: 'TenantAdmin bootstrap',
            bootstrapFirstTenantAdmin: true,
            image: image.image,
            migrationClientId,
          });
          const { outcome, diagnostic } = parseTenantAdminBootstrapMessage(
            kubectl(['logs', `job/${name}`, '--tail=20']).stdout);
          receipt.bootstrap = { outcome };
          if (diagnostic) receipt.bootstrap.diagnostic = safeDiagnostic(diagnostic, secrets);
          record('tenant-admin-bootstrap-result', { outcome });
          if (outcome === 'failed') {
            fail('First TenantAdmin bootstrap command failed; review the sanitized diagnostic in the receipt.');
          }
          if (outcome === 'conflict') {
            fail('First TenantAdmin bootstrap was refused because another active TenantAdmin exists.');
          }
          if (jobStatus !== 'Complete' || !['created', 'already-present'].includes(outcome)) {
            receipt.bootstrap.outcome = 'failed';
            fail('TenantAdmin bootstrap Job did not complete with a successful stable outcome.');
          }
          record('tenant-admin-bootstrap-job-completed', { job: name, image: image.image });
          receipt.result = 'succeeded';
        } else if (bootstrapFirstPlatformAdmin) {
          const jobStatus = await waitForJob(kubectl, deps, name, {
            returnFailure: true,
            description: 'PlatformAdmin bootstrap',
            bootstrapFirstPlatformAdmin: true,
            image: image.image,
            migrationClientId,
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
        } else if (inspectFirstPlatformAdmin) {
          const jobStatus = await waitForJob(kubectl, deps, name, {
            returnFailure: true,
            description: 'PlatformAdmin inspection',
            inspectFirstPlatformAdmin: true,
            image: image.image,
            migrationClientId,
          });
          const inspection = parsePlatformAdminInspectionOutput(
            kubectl(['logs', `job/${name}`, '--tail=20']).stdout);
          receipt.platformAdminInspection = inspection;
          record('platform-admin-inspection-result', { outcome: inspection.outcome });
          if (inspection.outcome === 'failed') {
            fail('First PlatformAdmin inspection command failed; review the sanitized diagnostic in the receipt.');
          }
          if (jobStatus !== 'Complete' || inspection.outcome !== 'inspected') {
            receipt.platformAdminInspection.outcome = 'failed';
            fail('PlatformAdmin inspection Job did not complete with a valid read-only result.');
          }
          record('platform-admin-inspection-job-completed', { job: name, image: image.image });
          receipt.result = 'succeeded';
        } else {
          await waitForJob(kubectl, deps, name);
          record('migration-job-completed', { job: name, image: image.image });
        }

        if (!adminOperation) receipt.result = 'succeeded';
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
        ...(adminOperation ? [['get', 'configmaps', NAMESPACE]] : []),
        ...(executeJob ? [
          ['create', 'leases.coordination.k8s.io', NAMESPACE],
          ['delete', 'leases.coordination.k8s.io', NAMESPACE],
          ...(!usesMigrationWorkloadIdentity ? [
            ['create', 'secrets', NAMESPACE],
            ['delete', 'secrets', NAMESPACE],
          ] : []),
          ['create', 'jobs', NAMESPACE],
          ['get', 'jobs', NAMESPACE],
          ['delete', 'jobs', NAMESPACE],
          ['list', 'pods', NAMESPACE],
          ...(adminOperation ? [
            ['get', 'serviceaccounts', NAMESPACE],
            ['get', 'pods', NAMESPACE, 'log'],
          ] : []),
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
  bootstrapFirstTenantAdmin = false,
  bootstrapFirstPlatformAdmin = false,
  inspectFirstPlatformAdmin = false,
  migrationClientId,
  image,
} = {}) {
  const deadline = deps.now().getTime() + JOB_TIMEOUT_SECONDS * 1000;
  for (let attempt = 0; ; attempt += 1) {
    const job = parseJson(kubectl(['get', 'job', name, '--output=json']).stdout, 'migration Job readback');
    validateJob(job, {
      name, image, bootstrapFirstTenantAdmin, bootstrapFirstPlatformAdmin,
      inspectFirstPlatformAdmin, migrationClientId,
    });
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
