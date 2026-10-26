/**
 * Writes a non-secret Broker run-binding descriptor from public certificate material in the P0 cluster.
 * The command only reads Kubernetes resources; release:deploy applies the corresponding runtime settings.
 */
import { createHash, X509Certificate } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateBrokerRunBinding } from './runtime-list.mjs';
import { run as runProcess } from '../azure/lib/exec.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const P0_CONTEXT = 'agentweaver-aks';
const P0_NAMESPACE = 'agentweaver';
const PROJECTS_HOSTNAME = 'projects-config.agentweaver.svc.cluster.local';
const PROJECTS_OWNER_ADDRESS = `https://${PROJECTS_HOSTNAME}:8443/`;
const PROJECTS_AUDIENCE = 'https://projects.agentweaver.io/run-binding';
const DEFAULT_OUTPUT = path.join('artifacts', 'release', 'broker-run-binding.json');

function fail(message) {
  throw new Error(`Broker run-binding preparation: ${message}`);
}

function parseArgs(argv = []) {
  const values = { outputPath: DEFAULT_OUTPUT, context: P0_CONTEXT, namespace: P0_NAMESPACE };
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    const option = ['--output', '--context', '--namespace']
      .find(name => arg === name || arg.startsWith(`${name}=`));
    if (!option) fail(`unknown argument "${arg}"`);
    const value = arg.startsWith(`${option}=`)
      ? arg.slice(option.length + 1)
      : argv[++index];
    if (!value || value.startsWith('--')) fail(`${option} requires an explicit value`);
    values[({ '--output': 'outputPath', '--context': 'context', '--namespace': 'namespace' })[option]] = value;
  }
  if (values.context !== P0_CONTEXT) fail(`--context must be ${P0_CONTEXT}`);
  if (values.namespace !== P0_NAMESPACE) fail(`--namespace must be ${P0_NAMESPACE}`);
  return values;
}

function parsePublicCertificates(pem, name) {
  const blocks = [...pem.matchAll(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g)]
    .map(([block]) => block);
  if (blocks.length === 0 ||
      pem.replace(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g, '').trim()) {
    fail(`${name} must contain only PEM certificates`);
  }
  try {
    return blocks.map(block => new X509Certificate(block));
  } catch (error) {
    fail(`${name} contains an invalid public certificate: ${error.message}`);
  }
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function readCaBundle({ root, run, context, namespace }) {
  const raw = run('kubectl', [
    '--context', context, 'get', 'configmap', 'service-backend-ca',
    '--namespace', namespace, '--output=json',
  ], { cwd: root });
  let resource;
  try {
    resource = JSON.parse(raw);
  } catch (error) {
    fail(`service-backend-ca is not valid JSON: ${error.message}`);
  }
  const caBundle = resource?.metadata?.name === 'service-backend-ca' &&
    resource.metadata.namespace === namespace && resource.data?.['ca.crt'];
  if (typeof caBundle !== 'string' || !caBundle) {
    fail('service-backend-ca must contain the public ca.crt in the exact P0 namespace');
  }
  const authorities = parsePublicCertificates(caBundle, 'service-backend-ca ca.crt');
  if (authorities.some(authority => !authority.ca)) {
    fail('service-backend-ca ca.crt must contain only CA certificates');
  }
  return Buffer.from(caBundle, 'utf8');
}

function readProjectsCertificate({ root, run, context, namespace }) {
  const encoded = run('kubectl', [
    '--context', context, 'get', 'secret', 'projects-config-tls',
    '--namespace', namespace, '--output=jsonpath={.data.tls\\.crt}',
  ], { cwd: root });
  const bytes = Buffer.from(encoded, 'base64');
  if (!encoded || bytes.toString('base64') !== encoded) {
    fail('projects-config-tls must contain a valid public tls.crt');
  }
  const pem = bytes.toString('utf8');
  const [leaf] = parsePublicCertificates(pem, 'projects-config-tls tls.crt');
  if (leaf.ca || leaf.checkHost(PROJECTS_HOSTNAME, { subject: 'never' }) !== PROJECTS_HOSTNAME ||
      Date.parse(leaf.validFrom) > Date.now() || Date.parse(leaf.validTo) <= Date.now() ||
      !leaf.keyUsage?.includes('1.3.6.1.5.5.7.3.1')) {
    fail(`projects-config-tls must contain a current serverAuth certificate for ${PROJECTS_HOSTNAME}`);
  }
  return leaf;
}

/** Reads only public certificate material and creates a descriptor accepted by both deploy paths. */
export function prepareBrokerRunBinding(options = {}, dependencies = {}) {
  const parsed = options.argv ? parseArgs(options.argv) : { ...options };
  const root = path.resolve(dependencies.root ?? repositoryRoot);
  const run = dependencies.run ?? ((file, args, opts) => runProcess(file, args, opts).stdout.trim());
  const writeFile = dependencies.writeFile ?? writeFileSync;
  const mkdir = dependencies.mkdir ?? mkdirSync;
  const context = parsed.context ?? P0_CONTEXT;
  const namespace = parsed.namespace ?? P0_NAMESPACE;
  const currentContext = run('kubectl', ['config', 'current-context'], { cwd: root });
  if (currentContext !== context) {
    fail(`current kubectl context must be ${context}, not ${currentContext || '(empty)'}`);
  }
  const caBytes = readCaBundle({ root, run, context, namespace });
  const certificate = readProjectsCertificate({ root, run, context, namespace });
  const binding = validateBrokerRunBinding({
    projectsOwnerAddress: PROJECTS_OWNER_ADDRESS,
    projectsAudience: PROJECTS_AUDIENCE,
    projectsServerCertificateSha256: sha256(certificate.raw),
    projectsTrustBundleSha256: sha256(caBytes),
  });
  const outputPath = path.resolve(root, parsed.outputPath ?? DEFAULT_OUTPUT);
  const relativeOutput = path.relative(root, outputPath);
  if (!relativeOutput || relativeOutput === '..' || relativeOutput.startsWith(`..${path.sep}`) ||
      path.isAbsolute(relativeOutput)) {
    fail('descriptor output must remain inside the repository');
  }
  mkdir(path.dirname(outputPath), { recursive: true });
  try {
    writeFile(outputPath, `${JSON.stringify(binding, null, 2)}\n`, { flag: 'wx' });
  } catch (error) {
    fail(`cannot create ${relativeOutput} without overwriting an existing file: ${error.message}`);
  }
  return { outputPath, binding };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const result = prepareBrokerRunBinding({ argv: process.argv.slice(2) });
    console.log(`Wrote non-secret Broker run-binding descriptor to ${path.relative(repositoryRoot, result.outputPath)}.`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
