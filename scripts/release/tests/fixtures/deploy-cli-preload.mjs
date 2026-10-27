import childProcess from 'node:child_process';
import fs from 'node:fs';
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import { parseAllDocuments } from 'yaml';

const fixture = JSON.parse(readFileSync(process.env.AGENTWEAVER_DEPLOY_CLI_FIXTURE, 'utf8'));
const originalSpawnSync = childProcess.spawnSync;
const originalReadFileSync = fs.readFileSync;
const sourceProjectFiles = new Map(Object.entries(fixture.projectFiles).map(([file, contents]) => [
  path.resolve(fixture.repoRoot, file),
  Buffer.from(contents, 'base64'),
]));
const runtimeResources = new Map();
const deploymentReads = new Map();

fs.readFileSync = function (file, options) {
  const contents = sourceProjectFiles.get(path.resolve(String(file)));
  if (!contents) return originalReadFileSync.call(this, file, options);
  if (typeof options === 'string') return contents.toString(options);
  if (options?.encoding) return contents.toString(options.encoding);
  return Buffer.from(contents);
};

function result(stdout = '', status = 0, stderr = '') {
  return { status, stdout, stderr, error: undefined, pid: process.pid, signal: null };
}

function commandName(file, args) {
  const name = path.basename(String(file)).toLowerCase();
  if (name === 'cmd.exe' || name === 'cmd') {
    const commandLine = String(args.at(-1) ?? '').toLowerCase();
    if (commandLine.includes('az.cmd')) return 'az';
    return 'blocked-command-shim';
  }
  return name.replace(/\.(?:exe|cmd|bat)$/i, '');
}

function completeRuntimeInputs(directory) {
  const managedHost = 'agentweaver.0123456789abcdef.eastus2euap.aksapp.io';
  const replacements = new Map([
    ['CHANGEME-KEYVAULT-HOST', 'configured-kv-01.vault.azure.net'],
    ['CHANGEME-MANAGED-HOST', managedHost],
    ['CHANGEME-PORTAL-HOST', `portal.${managedHost}`],
    ['CHANGEME-identity-broker-client-id', '00000000-0000-4000-8000-000000000001'],
    ['CHANGEME-environment-client-id', '00000000-0000-4000-8000-000000000002'],
    ['CHANGEME-events-and-sessions-client-id', '00000000-0000-4000-8000-000000000003'],
    ['CHANGEME-knowledge-client-id', '00000000-0000-4000-8000-000000000004'],
    ['CHANGEME-orchestrator-client-id', '00000000-0000-4000-8000-000000000005'],
    ['CHANGEME-projects-config-client-id', '00000000-0000-4000-8000-000000000006'],
  ]);
  const visit = current => {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const file = path.join(current, entry.name);
      if (entry.isDirectory()) {
        visit(file);
      } else if (/\.(?:json|yaml|yml)$/.test(entry.name)) {
        let contents = readFileSync(file, 'utf8');
        for (const [placeholder, value] of replacements) contents = contents.replaceAll(placeholder, value);
        if (file.includes(`${path.sep}runtime-configs${path.sep}`)) {
          const resource = JSON.parse(contents);
          if (resource.metadata?.annotations) {
            delete resource.metadata.annotations['agentweaver.io/runtime-config-status'];
            delete resource.metadata.annotations['agentweaver.io/runtime-config-incomplete-reason'];
          }
          contents = `${JSON.stringify(resource, null, 2)}\n`;
        }
        writeFileSync(file, contents, 'utf8');
      }
    }
  };
  visit(directory);
}

function saveRenderedResources(text) {
  runtimeResources.clear();
  for (const document of parseAllDocuments(text)) {
    const value = document.toJSON();
    for (const resource of value?.kind === 'List' ? value.items ?? [] : [value]) {
      if (resource?.kind && resource.metadata?.name) {
        runtimeResources.set(`${resource.kind}/${resource.metadata.name}`, resource);
      }
    }
  }
}

function resource(kind, name) {
  return runtimeResources.get(`${kind}/${name}`);
}

function fakePod(deployment, name) {
  const template = deployment.spec.template;
  const container = template.spec.containers[0];
  return {
    apiVersion: 'v1',
    kind: 'Pod',
    metadata: {
      name: `${name}-cli-test`,
      namespace: fixture.target.namespace,
      uid: `pod-${name}`,
      labels: template.metadata.labels,
    },
    spec: { ...template.spec, containers: template.spec.containers },
    status: {
      podIP: '10.0.0.10',
      podIPs: [{ ip: '10.0.0.10' }],
      conditions: [{ type: 'Ready', status: 'True' }],
      containerStatuses: [{
        name: container.name,
        ready: true,
        image: container.image,
        imageID: `containerd://${container.image}`,
      }],
    },
  };
}

function defaultDomainCertificate(namespace) {
  return {
    apiVersion: 'approuting.kubernetes.azure.com/v1alpha1',
    kind: 'DefaultDomainCertificate',
    metadata: { name: 'identity-broker', namespace, uid: `certificate-${namespace}`, generation: 1 },
    spec: { target: { secret: 'identity-broker-tls' } },
    status: {
      domain: '*.0123456789abcdef.eastus2euap.aksapp.io',
      conditions: [{ type: 'Available', status: 'True', observedGeneration: 1 }],
    },
  };
}

function fakeKubectl(file, args, options) {
  if (args[0] === 'kustomize') {
    const directory = path.resolve(args[1]);
    const tempRoot = path.resolve(os.tmpdir());
    if (!directory.startsWith(`${tempRoot}${path.sep}`)) {
      return result('', 98, 'test blocked a Kustomize render outside the temporary directory');
    }
    completeRuntimeInputs(directory);
    const rendered = originalSpawnSync(file, args, options);
    if (rendered.status === 0) saveRenderedResources(String(rendered.stdout));
    return rendered;
  }
  if (args[0] === 'config' && args[1] === 'view') {
    return result(JSON.stringify({
      'current-context': 'cli-test',
      contexts: [{ name: 'cli-test', context: { cluster: 'p0-test' } }],
      clusters: [{
        name: 'p0-test',
        cluster: { server: 'https://agentweaver-aks.example.test:443' },
      }],
    }));
  }
  if (args[0] === 'auth' && args[1] === 'can-i') return result('yes');
  if (['apply', 'rollout', 'wait'].includes(args[0])) return result();
  if (args[0] !== 'get') return result('', 98, `test blocked kubectl ${args.join(' ')}`);

  const kind = args[1];
  const name = args[2] && !args[2].startsWith('-') ? args[2] : undefined;
  const namespaceIndex = args.indexOf('--namespace');
  const namespace = namespaceIndex >= 0 ? args[namespaceIndex + 1] : fixture.target.namespace;
  const output = args.find(arg => arg.startsWith('--output='))?.slice('--output='.length) ??
    (args.includes('--output') ? args[args.indexOf('--output') + 1] : '');

  if (kind === 'namespace') {
    return result(JSON.stringify({
      metadata: {
        name: fixture.target.namespace,
        labels: { 'agentweaver.io/environment': 'v1-p0' },
      },
    }));
  }
  if (kind === 'defaultdomaincertificates') {
    const certificate = defaultDomainCertificate(namespace);
    return result(JSON.stringify({ items: [certificate] }));
  }
  if (['gateways', 'httproutes', 'backendtlspolicies'].includes(kind)) {
    return result(JSON.stringify({ items: [] }));
  }
  if (kind === 'configmap') {
    if (output === 'name') return result(`configmap/${name}`);
    if (name === 'service-backend-ca') {
      if (output.includes('ca') && output.includes('crt')) return result(fixture.caPem);
      return result(JSON.stringify({
        apiVersion: 'v1',
        kind: 'ConfigMap',
        metadata: { name, namespace },
        data: { 'ca.crt': fixture.caPem },
      }));
    }
    const configMap = resource('ConfigMap', name);
    if (configMap) return result(JSON.stringify(configMap));
    return result(JSON.stringify({ metadata: { name, namespace }, data: {} }));
  }
  if (kind === 'secret') {
    const certificate = name === 'projects-config-tls'
      ? fixture.projectsPemBase64
      : fixture.backendPemBase64;
    if (output.includes('go-template')) {
      return result(name === 'projects-config-tls'
        ? 'kubernetes.io/tls|certificate|key'
        : '');
    }
    if (output.includes('tls\\.crt')) {
      return result(name === 'projects-config-tls'
        ? fixture.projectsPemBase64
        : `kubernetes.io/tls:${fixture.backendPemBase64}`);
    }
    if (output.includes('metadata.name')) return result(`${name}:${namespace}`);
    return result(JSON.stringify({
      apiVersion: 'v1',
      kind: 'Secret',
      metadata: { name, namespace },
      type: 'kubernetes.io/tls',
      data: { 'tls.crt': certificate, 'tls.key': 'test-only-private-key' },
    }));
  }
  if (kind === 'deployment') {
    if (name === 'agent-sandbox-controller') {
      return result(fixture.agentSandboxControllerImage);
    }
    const deployment = resource('Deployment', name);
    if (!deployment) return result('', 44, `unknown test Deployment ${name}`);
    const count = (deploymentReads.get(name) ?? 0) + 1;
    deploymentReads.set(name, count);
    if (count > 1) {
      writeFileSync(fixture.markerPath, 'readback started');
      return result('', 47, 'intentional readback failure');
    }
    return result(JSON.stringify(deployment));
  }
  if (kind === 'service') {
    const service = resource('Service', name);
    return service ? result(JSON.stringify(service))
      : result('', 44, `unknown test Service ${name}`);
  }
  if (kind === 'pods') {
    const selector = args.find(arg => arg.startsWith('app.kubernetes.io/name='));
    const serviceName = selector?.slice('app.kubernetes.io/name='.length);
    const deployment = resource('Deployment', serviceName);
    return result(JSON.stringify({
      apiVersion: 'v1',
      kind: 'PodList',
      items: deployment ? [fakePod(deployment, serviceName)] : [],
    }));
  }
  if (output.includes('metadata.name')) return result(`${name}:${namespace}`);
  return result(JSON.stringify({
    apiVersion: 'v1',
    kind,
    metadata: { name, namespace },
    items: [],
  }));
}

function fakeAzure(args) {
  const command = args.join(' ').toLowerCase();
  if (command.includes('account')) {
    return result(JSON.stringify({
      id: fixture.target.subscriptionId,
      tenantId: fixture.target.tenantId,
    }));
  }
  if (command.includes('aks')) {
    return result(JSON.stringify({
      id: `/subscriptions/${fixture.target.subscriptionId}/resourceGroups/${fixture.target.resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${fixture.target.clusterName}`,
      fqdn: 'agentweaver-aks.example.test',
      agentPoolProfiles: [{
        enableAutoScaling: true,
        minCount: fixture.target.nodeMin,
        maxCount: fixture.target.nodeMax,
        count: fixture.target.nodeMin,
      }],
    }));
  }
  return result('', 98, `test blocked unexpected Azure command: ${command}`);
}

function fakeGitHub(args) {
  const route = args[1];
  if (route === `repos/sabbour/agentweaver/releases/tags/${fixture.releaseTag}`) {
    return result(JSON.stringify({
      tag_name: fixture.releaseTag,
      draft: false,
      prerelease: false,
    }));
  }
  if (route === `repos/sabbour/agentweaver/git/ref/tags/${encodeURIComponent(fixture.releaseTag)}`) {
    return result(JSON.stringify({ object: { type: 'tag', sha: '1'.repeat(40) } }));
  }
  if (route === `repos/sabbour/agentweaver/git/tags/${'1'.repeat(40)}`) {
    return result(JSON.stringify({
      tag: fixture.releaseTag,
      object: { type: 'commit', sha: fixture.sourceSha },
    }));
  }
  const publicationRef =
    `agentweaver-publication/${fixture.sourceSha}/result`;
  if (route === `repos/sabbour/agentweaver/git/ref/tags/${encodeURIComponent(publicationRef)}`) {
    return result(JSON.stringify({ object: { type: 'tag', sha: '2'.repeat(40) } }));
  }
  if (route === `repos/sabbour/agentweaver/git/tags/${'2'.repeat(40)}`) {
    return result(JSON.stringify({
      tag: publicationRef,
      object: { type: 'commit', sha: fixture.sourceSha },
      message: fixture.publicationJson,
    }));
  }
  return result('', 98, `test blocked unexpected GitHub request: ${route}`);
}

function response(body, headers = {}) {
  const bytes = Buffer.isBuffer(body) ? body : Buffer.from(String(body));
  return {
    ok: true,
    status: 200,
    headers: {
      get(name) {
        return headers[name.toLowerCase()] ?? null;
      },
    },
    async text() { return bytes.toString('utf8'); },
    async json() { return JSON.parse(bytes.toString('utf8')); },
    async arrayBuffer() {
      return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
    },
  };
}

globalThis.fetch = async input => {
  const url = new URL(String(input));
  if (url.href === 'https://github.com/kubernetes-sigs/agent-sandbox/releases/download/v1.0.7/sandbox-with-extensions.yaml') {
    return response(Buffer.from(fixture.agentSandboxManifestBase64, 'base64'));
  }
  if (url.hostname === 'ghcr.io' && url.pathname === '/token') {
    return response(JSON.stringify({ token: 'cli-test-token' }));
  }
  if (url.hostname === 'ghcr.io' && url.pathname.includes('/manifests/')) {
    const digest = url.pathname.slice(url.pathname.lastIndexOf('/') + 1);
    const body = url.pathname.includes(`/${fixture.hostRepository}/`)
      ? fixture.hostManifestText
      : JSON.stringify({ schemaVersion: 2 });
    return response(body, { 'docker-content-digest': digest });
  }
  if (url.hostname === 'ghcr.io' && url.pathname.includes('/blobs/')) {
    return response(fixture.hostConfigText);
  }
  throw new Error(`test blocked unexpected network request: ${url.href}`);
};

childProcess.spawnSync = function (file, args = [], options = {}) {
  const command = commandName(file, args);
  if (command === 'kubectl') return fakeKubectl(file, args, options);
  if (command === 'az') return fakeAzure(args);
  if (command === 'gh') return fakeGitHub(args);
  if (command === 'git') {
    if (args[0] === 'rev-parse' && args[1] === 'HEAD') return result(fixture.sourceSha);
    if (args[0] === 'status' && args[1] === '--porcelain') return result();
    return result('', 98, `test blocked unexpected Git command: ${args.join(' ')}`);
  }
  if (command === 'pwsh') return result('valid');
  return result('', 98, `test blocked unexpected child process: ${command}`);
};
syncBuiltinESMExports();
