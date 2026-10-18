import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** Validate Projects Config runtime data; Kustomize remains the only source of Kubernetes resources. */
const tenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47';
const clientId = 'CHANGEME-projects-config-client-id';
const postgresHost = 'agentweaver-pg.postgres.database.azure.com';
const fixedConfiguration = {
  ConnectionStrings__ProjectsConfig: `Host=${postgresHost};Port=5432;Database=agentweaver;Username=agentweaver-id-projects-config;SSL Mode=VerifyFull`,
  ProjectsConfig__Database__WorkloadIdentity__TenantId: tenantId,
  ProjectsConfig__Database__WorkloadIdentity__ClientId: clientId,
  ProjectsConfig__Database__WorkloadIdentity__TokenFilePath: '/var/run/secrets/azure/tokens/azure-identity-token',
};
const requiredConfiguration = [
  ...Object.keys(fixedConfiguration),
  'ProjectsConfig__Authentication__Authority',
  'ProjectsConfig__Authentication__Audience',
];

function exactFields(value, fields, location) {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error(`${location} must be an explicit object.`);
  for (const field of Object.keys(value)) {
    if (!fields.includes(field)) throw new Error(`${location}: unsupported field '${field}'.`);
  }
  for (const field of fields) {
    if (!Object.hasOwn(value, field)) throw new Error(`${location}: missing field '${field}'.`);
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

/** Validate the fixed P0 database and Broker settings without generating Kubernetes resources. */
export function renderProjectsProfile(input) {
  exactFields(input, ['configuration'], 'Projects profile');

  const data = input.configuration;
  exactFields(data, requiredConfiguration, 'Projects configuration');
  for (const key of requiredConfiguration) {
    if (typeof data[key] !== 'string' || !data[key].trim() ||
        data[key] !== data[key].trim() || /[\u0000-\u001f\u007f]/.test(data[key]))
      throw new Error(`Projects configuration: '${key}' requires an explicit nonempty string.`);
  }
  for (const [key, value] of Object.entries(fixedConfiguration)) {
    if (data[key] !== value) throw new Error(`Projects configuration: '${key}' differs from the admitted Projects runtime mapping.`);
  }
  issuerHost(data.ProjectsConfig__Authentication__Authority);
  return { data: { ...data } };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 3) throw new Error('Usage: node render.mjs <runtime-config-input.json>. This validates configuration and does not render Kubernetes resources.');
  const input = JSON.parse(readFileSync(process.argv[2], 'utf8'));
  console.log(JSON.stringify(renderProjectsProfile(input), null, 2));
}
