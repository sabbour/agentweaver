import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  PROVIDER_HOST_REQUEST_OVERLAY_KIND,
  applyProviderHostRequestOverlay,
  parseProviderHostRequestOverlay,
  requireFirstEnablementImages,
  requireLiveProviderHostRequestPreserved,
} from '../provider-host-request-overlay.mjs';
import { parseAllDocuments } from 'yaml';
import { deployKustomize, parseDeployKustomizeArgs } from '../deploy-kustomize.mjs';
import { parseDeployFromCommitArgs } from '../deploy-from-commit.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..', '..');

function loadBaseResources() {
  const kustomizeDir = path.join(repoRoot, 'deploy', 'k8s');
  const projectsConfigMap = JSON.parse(readFileSync(
    path.join(kustomizeDir, 'runtime-configs', 'projects-config-runtime-config.json'), 'utf8'));
  const environmentConfigMap = JSON.parse(readFileSync(
    path.join(kustomizeDir, 'runtime-configs', 'environment-runtime-config.json'), 'utf8'));
  const projectsDeployment = JSON.parse(readFileSync(
    path.join(kustomizeDir, 'base', 'workloads', 'projects-config-deployment.json'), 'utf8'));
  const environmentDeployment = JSON.parse(readFileSync(
    path.join(kustomizeDir, 'base', 'workloads', 'environment-deployment.json'), 'utf8'));
  const projectsService = JSON.parse(readFileSync(
    path.join(kustomizeDir, 'base', 'workloads', 'projects-config-service.json'), 'utf8'));
  const environmentService = JSON.parse(readFileSync(
    path.join(kustomizeDir, 'base', 'workloads', 'environment-service.json'), 'utf8'));
  return [
    projectsConfigMap,
    environmentConfigMap,
    projectsDeployment,
    environmentDeployment,
    projectsService,
    environmentService,
  ];
}

test('parseProviderHostRequestOverlay validates exact schema and distinct SHA-256 digests', () => {
  const valid = parseProviderHostRequestOverlay(JSON.stringify({
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256: 'a'.repeat(64),
    environmentCertificateSha256: 'b'.repeat(64),
  }));
  assert.equal(valid.projectsCertificateSha256, 'a'.repeat(64));
  assert.equal(valid.environmentCertificateSha256, 'b'.repeat(64));

  assert.throws(() => parseProviderHostRequestOverlay('{bad'), /valid JSON/);
  assert.throws(() => parseProviderHostRequestOverlay(JSON.stringify({
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256: 'a'.repeat(64),
  })), /must set "environmentCertificateSha256"/);
  assert.throws(() => parseProviderHostRequestOverlay(JSON.stringify({
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256: 'a'.repeat(64),
    environmentCertificateSha256: 'b'.repeat(64),
    privateKeyPem: 'secret',
  })), /unsupported field "privateKeyPem"/);
  assert.throws(() => parseProviderHostRequestOverlay(JSON.stringify({
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256: 'a'.repeat(64),
    environmentCertificateSha256: 'a'.repeat(64),
  })), /distinct certificate SHA-256 digests/);
  assert.throws(() => parseProviderHostRequestOverlay(JSON.stringify({
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256: 'A'.repeat(64),
    environmentCertificateSha256: 'b'.repeat(64),
  })), /lowercase SHA-256 hex digest/);
});

test('offline base resources keep private channel disabled by default and enable cleanly with overlay', () => {
  const disabled = loadBaseResources();
  const disabledProjects = disabled.find(r => r.metadata.name === 'projects-config-runtime-config');
  const disabledEnv = disabled.find(r => r.metadata.name === 'environment-runtime-config');
  assert.equal(disabledProjects.data.Kestrel__Endpoints__ProviderHostRequest__Url, undefined);
  assert.equal(disabledEnv.data.Kestrel__Endpoints__ProviderHostRequest__Url, undefined);
  assert.equal(disabledProjects.data.ServiceBackendCrl__LoopbackPort, undefined);
  assert.equal(disabledEnv.data.ServiceBackendCrl__LoopbackPort, undefined);
  const disabledProjectsDep = disabled.find(r => r.kind === 'Deployment' && r.metadata.name === 'projects-config');
  const disabledEnvDep = disabled.find(r => r.kind === 'Deployment' && r.metadata.name === 'environment');
  assert.equal(disabledProjectsDep.spec.template.spec.volumes.some(v => v.configMap?.name === 'service-backend-crl'), false);
  assert.equal(disabledEnvDep.spec.template.spec.volumes.some(v => v.configMap?.name === 'service-backend-crl'), false);

  const enabled = loadBaseResources();
  const overlay = parseProviderHostRequestOverlay(JSON.stringify({
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256: '1'.repeat(64),
    environmentCertificateSha256: '2'.repeat(64),
  }));
  applyProviderHostRequestOverlay(enabled, overlay);

  const projectsConfig = enabled.find(r => r.metadata.name === 'projects-config-runtime-config');
  const envConfig = enabled.find(r => r.metadata.name === 'environment-runtime-config');
  assert.equal(projectsConfig.data.Kestrel__Endpoints__ProviderHostRequest__Url, 'https://+:8444');
  assert.equal(projectsConfig.data.ProjectsConfig__ProviderHostRequest__Tls__ListenerPort, '8444');
  assert.equal(projectsConfig.data.ProjectsConfig__ProviderHostRequest__Tls__LocalCertificateSha256, '1'.repeat(64));
  assert.equal(projectsConfig.data.ProjectsConfig__ProviderHostRequest__Tls__PeerCertificateSha256Pins__0, '2'.repeat(64));
  assert.equal(projectsConfig.data.ProjectsConfig__ProviderHostRequest__Tls__PeerOwnerAddress,
    'https://environment.agentweaver.svc.cluster.local:8444/');

  assert.equal(envConfig.data.Kestrel__Endpoints__ProviderHostRequest__Url, 'https://+:8444');
  assert.equal(envConfig.data.Environment__ProviderHostRequest__Tls__ListenerPort, '8444');
  assert.equal(envConfig.data.Environment__ProviderHostRequest__Tls__LocalCertificateSha256, '2'.repeat(64));
  assert.equal(envConfig.data.Environment__ProviderHostRequest__Tls__PeerCertificateSha256Pins__0, '1'.repeat(64));
  assert.equal(envConfig.data.Environment__ProviderHostRequest__Tls__PeerOwnerAddress,
    'https://projects-config.agentweaver.svc.cluster.local:8444/');

  const projectsDep = enabled.find(r => r.kind === 'Deployment' && r.metadata.name === 'projects-config');
  const envDep = enabled.find(r => r.kind === 'Deployment' && r.metadata.name === 'environment');
  assert.ok(projectsDep.spec.template.spec.volumes.some(v =>
    v.secret?.secretName === 'projects-config-host-request-tls'));
  assert.ok(envDep.spec.template.spec.volumes.some(v =>
    v.secret?.secretName === 'environment-host-request-tls'));
  assert.ok(projectsDep.spec.template.spec.volumes.some(v =>
    v.configMap?.name === 'service-backend-crl' && v.configMap?.optional === false));
  assert.ok(envDep.spec.template.spec.volumes.some(v =>
    v.configMap?.name === 'service-backend-crl' && v.configMap?.optional === false));
  const projectsCrlMount = projectsDep.spec.template.spec.containers[0].volumeMounts.find(
    m => m.name === 'service-backend-crl');
  const envCrlMount = envDep.spec.template.spec.containers[0].volumeMounts.find(
    m => m.name === 'service-backend-crl');
  assert.equal(projectsCrlMount.mountPath, '/var/run/service-backend-crl');
  assert.equal(projectsCrlMount.readOnly, true);
  assert.equal(projectsCrlMount.subPath, undefined);
  assert.equal(envCrlMount.mountPath, '/var/run/service-backend-crl');
  assert.equal(envCrlMount.readOnly, true);
  assert.equal(envCrlMount.subPath, undefined);
  assert.equal(projectsConfig.data.ServiceBackendCrl__LoopbackPort, '8445');
  assert.equal(projectsConfig.data.ServiceBackendCrl__FilePath, '/var/run/service-backend-crl/ca.crl');
  assert.equal(envConfig.data.ServiceBackendCrl__LoopbackPort, '8445');
  assert.equal(envConfig.data.ServiceBackendCrl__FilePath, '/var/run/service-backend-crl/ca.crl');
  assert.ok(projectsDep.spec.template.spec.containers[0].ports.some(p => p.containerPort === 8444));
  assert.ok(envDep.spec.template.spec.containers[0].ports.some(p => p.containerPort === 8444));

  const projectsSvc = enabled.find(r => r.kind === 'Service' && r.metadata.name === 'projects-config');
  const envSvc = enabled.find(r => r.kind === 'Service' && r.metadata.name === 'environment');
  assert.ok(projectsSvc.spec.ports.some(p => p.port === 8444 && p.targetPort === 'host-request'));
  assert.ok(envSvc.spec.ports.some(p => p.port === 8444 && p.targetPort === 'host-request'));

  const policies = enabled.filter(r => r.kind === 'CiliumNetworkPolicy');
  assert.equal(policies.length, 2);
  for (const policy of policies) {
    assert.deepEqual(policy.spec.ingress[0].toPorts, [{ ports: [{ port: '8444', protocol: 'TCP' }] }]);
    assert.deepEqual(policy.spec.egress[0].toPorts, [{ ports: [{ port: '8444', protocol: 'TCP' }] }]);
  }
});

test('requireLiveProviderHostRequestPreserved stops before writes when redeploy omits --provider-host-request', () => {
  const live = loadBaseResources();
  applyProviderHostRequestOverlay(live, parseProviderHostRequestOverlay(JSON.stringify({
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256: '1'.repeat(64),
    environmentCertificateSha256: '2'.repeat(64),
  })));
  const liveProjects = live.find(r => r.metadata.name === 'projects-config-runtime-config');
  const liveEnv = live.find(r => r.metadata.name === 'environment-runtime-config');

  const disabled = loadBaseResources();
  const desiredProjects = disabled.find(r => r.metadata.name === 'projects-config-runtime-config');
  const desiredEnv = disabled.find(r => r.metadata.name === 'environment-runtime-config');

  assert.throws(() => requireLiveProviderHostRequestPreserved(
    liveProjects, liveEnv, desiredProjects, desiredEnv), /would remove it/);
  assert.doesNotThrow(() => requireLiveProviderHostRequestPreserved(
    liveProjects, liveEnv, liveProjects, liveEnv));
  assert.doesNotThrow(() => requireLiveProviderHostRequestPreserved(
    undefined, undefined, desiredProjects, desiredEnv));
});

test('deploy CLI parsers accept --provider-host-request and reject invalid usage', () => {
  const parsed = parseDeployKustomizeArgs([
    '--image-tag', `sha-${'a'.repeat(40)}`,
    '--provider-host-request', 'host-req.json',
    '--execute',
  ]);
  assert.equal(parsed.providerHostRequest, 'host-req.json');
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-tag', `sha-${'a'.repeat(40)}`,
    '--provider-host-request', '--execute',
  ]), /requires a file path/);

  const commitParsed = parseDeployFromCommitArgs([
    'b'.repeat(40),
    '--provider-host-request', 'host-req.json',
    '--execute',
  ]);
  assert.equal(commitParsed.providerHostRequest, path.resolve('host-req.json'));
});

test('actual offline kubectl kustomize render enables and preserves provider host-request channel', () => {
  const rendered = execFileSync('kubectl', ['kustomize', path.join(repoRoot, 'deploy', 'k8s')], {
    cwd: repoRoot,
    encoding: 'utf8',
  });
  const resources = parseAllDocuments(rendered).map(doc => doc.toJS());
  const projectsBefore = resources.find(r => r.kind === 'ConfigMap' && r.metadata?.name === 'projects-config-runtime-config');
  const envBefore = resources.find(r => r.kind === 'ConfigMap' && r.metadata?.name === 'environment-runtime-config');
  assert.equal(projectsBefore.data.Kestrel__Endpoints__ProviderHostRequest__Url, undefined);
  assert.equal(envBefore.data.Kestrel__Endpoints__ProviderHostRequest__Url, undefined);

  const overlay = parseProviderHostRequestOverlay(JSON.stringify({
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256: '3'.repeat(64),
    environmentCertificateSha256: '4'.repeat(64),
  }));
  applyProviderHostRequestOverlay(resources, overlay);
  assert.equal(projectsBefore.data.ProjectsConfig__ProviderHostRequest__Tls__LocalCertificateSha256, '3'.repeat(64));
  assert.equal(envBefore.data.Environment__ProviderHostRequest__Tls__LocalCertificateSha256, '4'.repeat(64));
});

test('requireFirstEnablementImages rejects partial manifest missing Projects or Environment on first enablement', () => {
  const overlay = parseProviderHostRequestOverlay(JSON.stringify({
    kind: PROVIDER_HOST_REQUEST_OVERLAY_KIND,
    schemaVersion: 1,
    projectsCertificateSha256: '1'.repeat(64),
    environmentCertificateSha256: '2'.repeat(64),
  }));
  const disabled = loadBaseResources();
  const disabledProjects = disabled.find(r => r.metadata.name === 'projects-config-runtime-config');
  const disabledEnv = disabled.find(r => r.metadata.name === 'environment-runtime-config');

  assert.throws(
    () => requireFirstEnablementImages(overlay, disabledProjects, disabledEnv, new Map([['agentweaver.gateway', 'sha256:' + '0'.repeat(64)]])),
    /agentweaver\.projects\.config and agentweaver\.environment/);
  assert.throws(
    () => requireFirstEnablementImages(overlay, disabledProjects, disabledEnv, new Map([['agentweaver.projects.config', 'sha256:' + '0'.repeat(64)]])),
    /agentweaver\.environment in --image-manifest/);
  assert.doesNotThrow(
    () => requireFirstEnablementImages(overlay, disabledProjects, disabledEnv, new Map([
      ['agentweaver.projects.config', 'sha256:' + '0'.repeat(64)],
      ['agentweaver.environment', 'sha256:' + '1'.repeat(64)],
    ])));

  const enabled = loadBaseResources();
  applyProviderHostRequestOverlay(enabled, overlay);
  const enabledProjects = enabled.find(r => r.metadata.name === 'projects-config-runtime-config');
  const enabledEnv = enabled.find(r => r.metadata.name === 'environment-runtime-config');
  assert.doesNotThrow(
    () => requireFirstEnablementImages(overlay, enabledProjects, enabledEnv, new Map([['agentweaver.gateway', 'sha256:' + '0'.repeat(64)]])));
});
