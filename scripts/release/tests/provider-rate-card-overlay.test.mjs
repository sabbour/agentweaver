import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  PROVIDER_RATE_CARD_OVERLAY_KIND,
  applyProviderRateCardOverlay,
  parseProviderRateCardOverlay,
  requireFirstEnablementRateCardImages,
  requireLiveProviderRateCardPreserved,
  verifyClusterOidcIssuer,
} from '../provider-rate-card-overlay.mjs';
import { stampEventsRuntimeConfigDigest, EVENTS_RUNTIME_CONFIG_ANNOTATION } from '../model-bindings-overlay.mjs';
import { parseKustomizeResources } from '../kustomize-runtime.mjs';
import { parseDeployKustomizeArgs, verifyImageCapability } from '../deploy-kustomize.mjs';
import { deployFromCommit, parseDeployFromCommitArgs } from '../deploy-from-commit.mjs';
import { IMAGE_REPOSITORIES } from '../image-repositories.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..', '..');

const AUTHORITY = 'https://eastus2euap.oic.prod-aks.azure.com/72f988bf-86f1-41af-91ab-2d7cd011db47/cluster-1/';
const AUDIENCE = 'https://events-and-sessions.agentweaver.svc.cluster.local/internal/platform-components';
const JWKS_URI = 'https://keys.oic-example.azure.net/cluster-1/jwks';

function loadRenderedResources() {
  const rendered = execFileSync('kubectl', ['kustomize', path.join(repoRoot, 'deploy', 'k8s')], {
    cwd: repoRoot,
    encoding: 'utf8',
  });
  return parseKustomizeResources(rendered).items;
}

test('parseProviderRateCardOverlay validates schema, HTTPS URLs, port 443, and optional distinct jwksUri', () => {
  const valid = parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
    audience: AUDIENCE,
    jwksUri: JWKS_URI,
  }));
  assert.equal(valid.authority, AUTHORITY);
  assert.equal(valid.authorityHost, 'eastus2euap.oic.prod-aks.azure.com');
  assert.equal(valid.audience, AUDIENCE);
  assert.equal(valid.jwksUri, JWKS_URI);
  assert.equal(valid.jwksHost, 'keys.oic-example.azure.net');

  assert.throws(() => parseProviderRateCardOverlay('{bad'), /valid JSON/);
  assert.throws(() => parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
  })), /must set "audience"/);
  assert.throws(() => parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
    audience: AUDIENCE,
    token: 'secret',
  })), /unsupported field "token"/);
  assert.throws(() => parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: 'http://eastus2euap.oic.prod-aks.azure.com/issuer',
    audience: AUDIENCE,
  })), /absolute HTTPS URL/);
  assert.throws(() => parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: 'https://eastus2euap.oic.prod-aks.azure.com:8443/issuer',
    audience: AUDIENCE,
  })), /default HTTPS port 443/);
  assert.throws(() => parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: 'https://127.0.0.1/issuer',
    audience: AUDIENCE,
  })), /DNS host name/);
  assert.throws(() => parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: `${AUTHORITY}?x=1`,
    audience: AUDIENCE,
  })), /query or fragment/);
  assert.throws(() => parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
    audience: `${AUDIENCE}?`,
  })), /query or fragment/);
  assert.throws(() => parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
    audience: `${AUDIENCE}#`,
  })), /query or fragment/);
});

test('offline rendered overlay wires exact token projection, Events trust, scoped Cilium policies, and digest', () => {
  const resources = loadRenderedResources();
  const baseEventsDeployment = resources.find(r => r.kind === 'Deployment' && r.metadata.name === 'events-and-sessions');
  stampEventsRuntimeConfigDigest(resources);
  const baseDigest = baseEventsDeployment.spec.template.metadata.annotations[EVENTS_RUNTIME_CONFIG_ANNOTATION];

  const overlay = parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
    audience: AUDIENCE,
    jwksUri: JWKS_URI,
  }));
  applyProviderRateCardOverlay(resources, overlay);
  stampEventsRuntimeConfigDigest(resources);

  const projectsConfig = resources.find(r => r.kind === 'ConfigMap' && r.metadata.name === 'projects-config-runtime-config');
  const eventsConfig = resources.find(r => r.kind === 'ConfigMap' && r.metadata.name === 'events-and-sessions-runtime-config');
  assert.equal(projectsConfig.data.PlatformComponents__ServiceToken__TokenFile,
    '/var/run/projects-config-platform-components/token');
  assert.equal(projectsConfig.data.PlatformComponents__Owners__eventsAndSessions__BaseAddress,
    'https://events-and-sessions.agentweaver.svc.cluster.local:8443/');
  assert.equal(eventsConfig.data.PlatformComponents__ServiceAuthentication__Authority, AUTHORITY);
  assert.equal(eventsConfig.data.PlatformComponents__ServiceAuthentication__Audience, AUDIENCE);
  assert.equal(eventsConfig.data.PlatformComponents__ServiceAuthentication__CallerSubject,
    'system:serviceaccount:agentweaver:projects-config');

  const updatedDigest = baseEventsDeployment.spec.template.metadata.annotations[EVENTS_RUNTIME_CONFIG_ANNOTATION];
  assert.notEqual(updatedDigest, baseDigest);

  const projectsDeployment = resources.find(r => r.kind === 'Deployment' && r.metadata.name === 'projects-config');
  const tokenVolume = projectsDeployment.spec.template.spec.volumes.find(v => v.name === 'platform-components-token');
  assert.deepEqual(tokenVolume.projected.sources[0].serviceAccountToken, {
    audience: AUDIENCE,
    expirationSeconds: 3600,
    path: 'token',
  });
  const tokenMount = projectsDeployment.spec.template.spec.containers[0].volumeMounts.find(
    m => m.name === 'platform-components-token');
  assert.equal(tokenMount.mountPath, '/var/run/projects-config-platform-components');
  assert.equal(tokenMount.readOnly, true);

  const p2e = resources.find(r => r.kind === 'CiliumNetworkPolicy' && r.metadata.name === 'projects-config-events-network');
  const e2p = resources.find(r => r.kind === 'CiliumNetworkPolicy' && r.metadata.name === 'events-and-sessions-projects-network');
  assert.deepEqual(p2e.spec.egress[0].toPorts[0].rules.dns, [
    { matchName: 'events-and-sessions.agentweaver.svc.cluster.local' },
  ]);
  assert.deepEqual(p2e.spec.egress[1].toPorts, [{ ports: [{ port: '8443', protocol: 'TCP' }] }]);
  assert.deepEqual(e2p.spec.ingress[0].fromEndpoints, [{
    matchLabels: {
      'k8s:io.kubernetes.pod.namespace': 'agentweaver',
      'agentweaver.io/workload': 'projects-config',
    },
  }]);
  assert.deepEqual(e2p.spec.ingress[0].toPorts, [{ ports: [{ port: '8443', protocol: 'TCP' }] }]);
  assert.deepEqual(e2p.spec.egress[0].toPorts[0].rules.dns, [
    { matchName: 'eastus2euap.oic.prod-aks.azure.com' },
    { matchName: 'keys.oic-example.azure.net' },
  ]);
  assert.deepEqual(e2p.spec.egress[1].toFQDNs, [
    { matchName: 'eastus2euap.oic.prod-aks.azure.com' },
    { matchName: 'keys.oic-example.azure.net' },
  ]);
  assert.deepEqual(e2p.spec.egress[1].toPorts, [{ ports: [{ port: '443', protocol: 'TCP' }] }]);
});

test('applyProviderRateCardOverlay rejects audience collisions with public and run-binding audiences', () => {
  const resources = loadRenderedResources();
  const projectsConfig = resources.find(r => r.kind === 'ConfigMap' && r.metadata.name === 'projects-config-runtime-config');
  const eventsConfig = resources.find(r => r.kind === 'ConfigMap' && r.metadata.name === 'events-and-sessions-runtime-config');

  for (const colliding of [
    projectsConfig.data.ProjectsConfig__Authentication__Audience,
    projectsConfig.data.ProjectsConfig__Authentication__BrokerRunBindingAudience,
    eventsConfig.data.Identity__Audience,
  ]) {
    const candidate = structuredClone(resources);
    const overlay = parseProviderRateCardOverlay(JSON.stringify({
      kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
      schemaVersion: 1,
      authority: AUTHORITY,
      audience: colliding,
    }));
    assert.throws(() => applyProviderRateCardOverlay(candidate, overlay), /audience must differ/);
  }
});

test('live preservation stops on missing flag for complete or partial config and ignores unrelated owners', () => {
  const base = loadRenderedResources();
  const baseProjects = base.find(r => r.metadata.name === 'projects-config-runtime-config');
  const baseEvents = base.find(r => r.metadata.name === 'events-and-sessions-runtime-config');

  // Unrelated owner configuration alone does not trigger rate-card preservation failure.
  const unrelatedProjects = structuredClone(baseProjects);
  unrelatedProjects.data.PlatformComponents__Owners__knowledge__BaseAddress =
    'https://knowledge.agentweaver.svc.cluster.local:8443/';
  assert.doesNotThrow(() =>
    requireLiveProviderRateCardPreserved(unrelatedProjects, baseEvents, baseProjects, baseEvents));

  // Partial or complete rate-card settings stop before writes when omitted.
  const partialProjects = structuredClone(baseProjects);
  partialProjects.data.PlatformComponents__Owners__eventsAndSessions__BaseAddress =
    'https://events-and-sessions.agentweaver.svc.cluster.local:8443/';
  assert.throws(() =>
    requireLiveProviderRateCardPreserved(partialProjects, baseEvents, baseProjects, baseEvents), /would remove it/);

  const enabled = structuredClone(base);
  applyProviderRateCardOverlay(enabled, parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
    audience: AUDIENCE,
  })));
  const enabledProjects = enabled.find(r => r.metadata.name === 'projects-config-runtime-config');
  const enabledEvents = enabled.find(r => r.metadata.name === 'events-and-sessions-runtime-config');
  assert.throws(() =>
    requireLiveProviderRateCardPreserved(enabledProjects, enabledEvents, baseProjects, baseEvents), /would remove it/);
  assert.doesNotThrow(() =>
    requireLiveProviderRateCardPreserved(enabledProjects, enabledEvents, enabledProjects, enabledEvents));

  const overlay = parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
    audience: AUDIENCE,
  }));
  assert.throws(() =>
    requireFirstEnablementRateCardImages(overlay, baseProjects, baseEvents, new Set(['agentweaver.projects.config'])),
  /agentweaver\.eventsandsessions/);
});

test('verifyClusterOidcIssuer requires matching issuer and distinct jwksUri declaration', () => {
  const overlay = parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
    audience: AUDIENCE,
  }));

  const okExec = () => ({
    status: 0,
    stdout: JSON.stringify({ issuer: AUTHORITY, jwks_uri: `${AUTHORITY}openid/v1/jwks` }),
    stderr: '',
  });
  assert.doesNotThrow(() => verifyClusterOidcIssuer(okExec, 'ctx', '.', overlay));

  const wrongIssuerExec = () => ({
    status: 0,
    stdout: JSON.stringify({ issuer: 'https://other.oic.prod-aks.azure.com/t/c/', jwks_uri: `${AUTHORITY}jwks` }),
    stderr: '',
  });
  assert.throws(() => verifyClusterOidcIssuer(wrongIssuerExec, 'ctx', '.', overlay), /does not match/);

  const distinctJwksExec = () => ({
    status: 0,
    stdout: JSON.stringify({ issuer: AUTHORITY, jwks_uri: JWKS_URI }),
    stderr: '',
  });
  assert.throws(() => verifyClusterOidcIssuer(distinctJwksExec, 'ctx', '.', overlay), /set jwksUri/);

  const withJwks = parseProviderRateCardOverlay(JSON.stringify({
    kind: PROVIDER_RATE_CARD_OVERLAY_KIND,
    schemaVersion: 1,
    authority: AUTHORITY,
    audience: AUDIENCE,
    jwksUri: JWKS_URI,
  }));
  assert.doesNotThrow(() => verifyClusterOidcIssuer(distinctJwksExec, 'ctx', '.', withJwks));
  assert.throws(() => verifyClusterOidcIssuer(okExec, 'ctx', '.', withJwks), /does not match cluster discovery jwks_uri/);
});

test('verifyImageCapability rejects Projects or Events images that predate rate-card support', async () => {
  const sourceSha = 'a'.repeat(40);
  const sourceTree = 'b'.repeat(40);
  const exec = (name, args) => {
    if (name === 'git' && args[0] === 'fetch') return { status: 0, stdout: '', stderr: '' };
    if (name === 'git' && args[0] === 'merge-base') return { status: 0, stdout: '', stderr: '' };
    if (name === 'git' && args[0] === 'rev-parse') return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
    if (name === 'git' && args[0] === 'cat-file') return { status: 1, stdout: '', stderr: '' };
    assert.fail(`unexpected ${name} ${args.join(' ')}`);
  };
  await assert.rejects(
    () => verifyImageCapability({
      image: `ghcr.io/sabbour/agentweaver.projects.config@sha256:${'1'.repeat(64)}`,
      capability: 'projects-config-rate-card',
      root: '.',
      exec,
      readImage: async () => ({ sourceSha, sourceTree }),
    }),
    /does not support private Events rate-card reads/,
  );
  await assert.rejects(
    () => verifyImageCapability({
      image: `ghcr.io/sabbour/agentweaver.eventsandsessions@sha256:${'2'.repeat(64)}`,
      capability: 'events-and-sessions-rate-card',
      root: '.',
      exec,
      readImage: async () => ({ sourceSha, sourceTree }),
    }),
    /does not support private Projects rate-card reads/,
  );
});

test('deploy CLI parsers and deploy-from-commit rollback guard handle --provider-rate-card', async () => {
  const parsed = parseDeployKustomizeArgs([
    '--image-tag', `sha-${'a'.repeat(40)}`,
    '--provider-rate-card', 'rate-card.json',
    '--execute',
  ]);
  assert.equal(parsed.providerRateCard, 'rate-card.json');
  assert.throws(() => parseDeployKustomizeArgs([
    '--image-tag', `sha-${'a'.repeat(40)}`,
    '--provider-rate-card', '--execute',
  ]), /requires a file path/);

  const commitParsed = parseDeployFromCommitArgs([
    'b'.repeat(40),
    '--provider-rate-card', 'rate-card.json',
    '--execute',
  ]);
  assert.equal(commitParsed.providerRateCard, path.resolve('rate-card.json'));

  const testRoot = path.join(repoRoot, `.provider-rate-card-rollback-${randomUUID()}`);
  const worktreePath = path.join(testRoot, 'source');
  mkdirSync(testRoot);
  const sourceSha = 'a'.repeat(40);
  const sourceTree = 'b'.repeat(40);
  const digests = Object.fromEntries(IMAGE_REPOSITORIES.map((repo, i) =>
    [repo, `sha256:${(i + 1).toString(16).padStart(64, '0')}`]));
  const exec = (name, args) => {
    if (name === 'git' && args[0] === 'fetch') return { status: 0, stdout: '', stderr: '' };
    if (name === 'git' && args[0] === 'merge-base') return { status: 0, stdout: '', stderr: '' };
    if (name === 'git' && args[0] === 'rev-parse') return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
    if (name === 'git' && args[0] === 'worktree' && args[1] === 'add') {
      mkdirSync(worktreePath);
      return { status: 0, stdout: '', stderr: '' };
    }
    if (name === 'git' && args[0] === 'worktree' && args[1] === 'remove') {
      rmSync(worktreePath, { recursive: true, force: true });
      return { status: 0, stdout: '', stderr: '' };
    }
    if (name === 'npm' && args[0] === 'ci') return { status: 0, stdout: '', stderr: '' };
    if (name === 'docker' && args[0] === 'buildx') {
      const repo = args[3].slice('ghcr.io/sabbour/'.length).split(':')[0];
      return { status: 0, stdout: `${digests[repo]}\n`, stderr: '' };
    }
    if (name === 'kubectl' && args[0] === 'config' && args[1] === 'current-context') {
      return { status: 0, stdout: 'agentweaver-ctx\n', stderr: '' };
    }
    if (name === 'kubectl' && args[0] === 'config' && args[1] === 'view') {
      return { status: 0, stdout: 'https://agentweaver-aks.hcp.eastus2euap.azmk8s.io:443', stderr: '' };
    }
    if (name === 'kubectl' && args[0] === 'get' && args[1] === 'configmap') {
      if (args[2] === 'events-and-sessions-runtime-config') {
        return {
          status: 0,
          stdout: JSON.stringify({
            kind: 'ConfigMap',
            metadata: { name: args[2], namespace: 'agentweaver' },
            data: { PlatformComponents__ServiceAuthentication__Authority: AUTHORITY },
          }),
          stderr: '',
        };
      }
      return { status: 0, stdout: '', stderr: '' };
    }
    assert.fail(`unexpected ${name} ${args.join(' ')}`);
  };

  try {
    await assert.rejects(
      () => deployFromCommit({ sourceSha }, {
        root: repoRoot,
        worktreePath,
        exec,
        verifyImage: async () => ({ sourceSha, sourceTree }),
        deployKustomize: async () => {},
        deployModule: {
          DEPLOY_KUSTOMIZE_REGISTRY_OPTION: true,
          DEPLOY_KUSTOMIZE_MODEL_BINDINGS_OPTION: true,
          DEPLOY_KUSTOMIZE_PROVIDER_HOST_REQUEST_OPTION: true,
          DEPLOY_KUSTOMIZE_PROVIDER_RATE_CARD_OPTION: false,
          DEPLOY_KUSTOMIZE_TARGET_OPTION: true,
          DEPLOY_KUSTOMIZE_AUDIT_OPTION: true,
        },
      }),
      /predates provider rate-card support/,
    );
  } finally {
    rmSync(testRoot, { recursive: true, force: true });
  }
});
