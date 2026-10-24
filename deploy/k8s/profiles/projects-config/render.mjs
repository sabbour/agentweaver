import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** Validate Projects Config runtime data; Kustomize remains the only source of Kubernetes resources. */
const tenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47';
const clientId = 'b65f060f-364b-45ea-adf4-f704c176f33e';
const postgresHost = 'agentweaver-pg.postgres.database.azure.com';
const knowledgeBaseAddress = 'https://knowledge.agentweaver.svc.cluster.local:8443/';
const fixedConfiguration = {
  ConnectionStrings__ProjectsConfig: `Host=${postgresHost};Port=5432;Database=agentweaver;Username=agentweaver-id-projects-config;SSL Mode=VerifyFull`,
  ProjectsConfig__Database__WorkloadIdentity__TenantId: tenantId,
  ProjectsConfig__Database__WorkloadIdentity__ClientId: clientId,
  ProjectsConfig__Database__WorkloadIdentity__TokenFilePath: '/var/run/secrets/azure/tokens/azure-identity-token',
  ProjectsConfig__Knowledge__BaseAddress: knowledgeBaseAddress,
};
const requiredConfiguration = [
  ...Object.keys(fixedConfiguration),
  'ProjectsConfig__Authentication__Authority',
  'ProjectsConfig__Authentication__Audience',
];
const privilegedConfigurationFields = [
  'ConnectionStrings__ProjectsConfigAuthority',
  'ProjectsConfig__AuthorityDatabase__WorkloadIdentity__TenantId',
  'ProjectsConfig__AuthorityDatabase__WorkloadIdentity__ClientId',
  'ProjectsConfig__AuthorityDatabase__WorkloadIdentity__TokenFilePath',
  'ProjectsConfig__PrivilegedSetup__ApprovedIssuer',
  'ProjectsConfig__PrivilegedSetup__ApprovedSubject',
  'ProjectsConfig__PrivilegedSetup__ApprovedTenantId',
];

// Projects Config receives only the eligibility fields of the AgentHost model bindings. Model IDs, provider
// URLs, headers and credential references stay with AgentHost, so those keys are rejected here. References
// cannot contain ':' or '__' because .NET reads both as configuration hierarchy separators.
const modelBindingRevisionKey = 'AgentHost__ModelBindingsRevision';
const modelBindingFieldPattern =
  /^AgentHost__ModelBindings__([A-Za-z0-9.-]+(?:_[A-Za-z0-9.-]+)*)__(SourceMode|Enabled|SupportedReasoningEfforts__(0|[1-9][0-9]*))$/;
const reasoningEfforts = ['low', 'medium', 'high', 'xhigh'];

/** Require the declared fields and allow only model-binding keys validated separately. */
function exactFields(value, fields, location, allowAdditionalField = () => false) {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error(`${location} must be an explicit object.`);
  for (const field of Object.keys(value)) {
    if (!fields.includes(field) && !allowAdditionalField(field))
      throw new Error(`${location}: unsupported field '${field}'.`);
  }
  for (const field of fields) {
    if (!Object.hasOwn(value, field)) throw new Error(`${location}: missing field '${field}'.`);
  }
}

/**
 * Validate the optional read-only model-binding copy. Absent keys mean model choices are reported as
 * unavailable; any partial or malformed copy fails here, matching the checks the service repeats at startup.
 */
function validateModelBindings(configuration) {
  const bindings = new Map();
  for (const [key, value] of Object.entries(configuration)) {
    if (!key.startsWith('AgentHost__ModelBindings__')) continue;
    const match = modelBindingFieldPattern.exec(key);
    if (!match) throw new Error(`Projects configuration: unsupported model binding field '${key}'.`);
    const [, reference, field, index] = match;
    const binding = bindings.get(reference) ?? { efforts: new Map() };
    if (index === undefined) binding[field] = value;
    else binding.efforts.set(Number(index), value);
    bindings.set(reference, binding);
  }
  const hasRevision = Object.hasOwn(configuration, modelBindingRevisionKey);
  if (!hasRevision && bindings.size === 0) return;
  if (bindings.size === 0)
    throw new Error('Projects configuration requires at least one model binding when a model-bindings revision is set.');
  if (typeof configuration[modelBindingRevisionKey] !== 'string' ||
      !/^[A-Za-z0-9._:-]{1,256}$/.test(configuration[modelBindingRevisionKey]))
    throw new Error('Projects configuration requires a valid model-bindings revision.');
  for (const [reference, binding] of bindings) {
    // Effort indexes must be 0..n-1 so a gap cannot hide a configured value.
    const ordered = [...binding.efforts.entries()].sort(([left], [right]) => left - right);
    const efforts = ordered.every(([index], position) => index === position)
      ? ordered.map(([, value]) => value) : null;
    if (!['HostedCopilot', 'Byok'].includes(binding.SourceMode) ||
        !['true', 'false'].includes(binding.Enabled) ||
        reference.length > 256 ||
        efforts === null ||
        efforts.some(effort => !reasoningEfforts.includes(effort)) ||
        new Set(efforts).size !== efforts.length)
      throw new Error(`Projects configuration: model binding '${reference}' is incomplete or invalid.`);
  }
}

/** Require the managed Broker authority to use the approved HTTPS root hostname. */
function issuerHost(value) {
  if (!/^https:\/\/agentweaver\.[a-z0-9.-]+\.aksapp\.io\/$/.test(value))
    throw new Error('Projects authority requires the existing managed Broker HTTPS root on port 443.');
  let uri;
  try { uri = new URL(value); } catch {
    throw new Error('Projects authority must be a valid HTTPS root URI.');
  }
  return uri.hostname;
}

/** Require Knowledge calls to use the pinned in-cluster HTTPS owner root. */
function validateKnowledgeOwnerAddress(value) {
  let uri;
  try { uri = new URL(value); } catch {
    throw new Error('Projects Knowledge address must be a valid HTTPS owner root.');
  }
  if (uri.protocol !== 'https:' ||
      uri.hostname !== 'knowledge.agentweaver.svc.cluster.local' ||
      uri.port !== '8443' ||
      uri.pathname !== '/' ||
      uri.username ||
      uri.password ||
      uri.search ||
      uri.hash)
    throw new Error('Projects Knowledge address requires the in-cluster HTTPS owner root on port 8443.');
}

/** Validate explicitly approved setup settings without elevating or reusing the runtime principal. */
function privilegedConfiguration(input, runtime) {
  exactFields(input, privilegedConfigurationFields, 'Privileged setup configuration');
  for (const key of privilegedConfigurationFields) {
    if (typeof input[key] !== 'string' || !input[key].trim() ||
        input[key] !== input[key].trim() || /[\u0000-\u001f\u007f]/.test(input[key]))
      throw new Error(`Privileged setup configuration: '${key}' requires an explicit nonempty string.`);
  }
  const connection = input.ConnectionStrings__ProjectsConfigAuthority;
  const role = /^Host=[^;]+;Port=5432;Database=agentweaver;Username=([a-z_][a-z0-9_-]{0,62});SSL Mode=VerifyFull$/.exec(connection)?.[1];
  if (!role || role === 'postgres' || /migration/i.test(role) ||
      connection !== fixedConfiguration.ConnectionStrings__ProjectsConfig.replace(
        'Username=agentweaver-id-projects-config;', `Username=${role};`) ||
      role === 'agentweaver-id-projects-config')
    throw new Error('Privileged setup requires the same database, VerifyFull, and a separate approved password-free authority role.');
  const identity = 'ProjectsConfig__AuthorityDatabase__WorkloadIdentity__';
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (!guid.test(input[`${identity}ClientId`]) ||
      /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(input[`${identity}ClientId`]) ||
      input[`${identity}ClientId`].toLowerCase() === clientId ||
      input[`${identity}TenantId`] !== tenantId ||
      input[`${identity}TokenFilePath`] !== runtime.ProjectsConfig__Database__WorkloadIdentity__TokenFilePath)
    throw new Error('Privileged setup requires a separate approved workload identity in the admitted tenant with the projected token path.');
  if (input.ProjectsConfig__PrivilegedSetup__ApprovedIssuer !== runtime.ProjectsConfig__Authentication__Authority ||
      input.ProjectsConfig__PrivilegedSetup__ApprovedTenantId !== tenantId ||
      !/^[a-z0-9._:-]{1,256}$/i.test(input.ProjectsConfig__PrivilegedSetup__ApprovedSubject))
    throw new Error('Privileged setup requires the exact Broker issuer, admitted tenant, and an approved opaque subject.');
  return input;
}

/** Validate the fixed P0 database and Broker settings without generating Kubernetes resources. */
export function renderProjectsProfile(input) {
  const hasPrivilegedSetup = Object.hasOwn(input ?? {}, 'privilegedSetupConfiguration');
  exactFields(input, ['configuration', ...(hasPrivilegedSetup ? ['privilegedSetupConfiguration'] : [])], 'Projects profile');

  const data = input.configuration;
  exactFields(data, requiredConfiguration, 'Projects configuration',
    key => key === modelBindingRevisionKey || key.startsWith('AgentHost__ModelBindings__'));
  for (const key of requiredConfiguration) {
    if (typeof data[key] !== 'string' || !data[key].trim() ||
        data[key] !== data[key].trim() || /[\u0000-\u001f\u007f]/.test(data[key]))
      throw new Error(`Projects configuration: '${key}' requires an explicit nonempty string.`);
  }
  validateKnowledgeOwnerAddress(data.ProjectsConfig__Knowledge__BaseAddress);
  for (const [key, value] of Object.entries(fixedConfiguration)) {
    if (data[key] !== value) throw new Error(`Projects configuration: '${key}' differs from the admitted Projects runtime mapping.`);
  }
  issuerHost(data.ProjectsConfig__Authentication__Authority);
  validateModelBindings(data);
  return { data: {
    ...data,
    ...(hasPrivilegedSetup ? privilegedConfiguration(input.privilegedSetupConfiguration, data) : {}),
  } };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 3) throw new Error('Usage: node render.mjs <runtime-config-input.json>. This validates configuration and does not render Kubernetes resources.');
  const input = JSON.parse(readFileSync(process.argv[2], 'utf8'));
  console.log(JSON.stringify(renderProjectsProfile(input), null, 2));
}
