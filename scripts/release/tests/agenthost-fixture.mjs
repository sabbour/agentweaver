import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import { AGENTHOST_DOCKERFILE, AGENTHOST_PROJECT } from '../agenthost-image.mjs';

const runtimePath = 'services/agenthost/Agentweaver.AgentHost/runtime-manifest.json';

export function addAgentHostSource(root, version = '0.1.0') {
  const dockerfile = readFileSync(new URL(`../../../${AGENTHOST_DOCKERFILE}`, import.meta.url), 'utf8');
  const directories = [...dockerfile.matchAll(/^COPY ((?:packages|services)\/\S+)\/ /gm)].map(match => match[1]);
  writeFileSync(path.join(root, 'Directory.Build.props'), '<Project />\n');
  writeFileSync(path.join(root, '.dockerignore'), '**/bin\n**/obj\nartifacts\n');
  for (const directory of directories) {
    mkdirSync(path.join(root, directory), { recursive: true });
    writeFileSync(path.join(root, directory, `${path.posix.basename(directory)}.csproj`),
      `<Project><PropertyGroup><Version>${version}</Version></PropertyGroup></Project>\n`);
    writeFileSync(path.join(root, directory, 'packages.lock.json'), '{"version":1,"dependencies":{}}\n');
    writeFileSync(path.join(root, directory, 'Source.cs'), '// Controlled build input.\n');
  }
  writeFileSync(path.join(root, AGENTHOST_DOCKERFILE), dockerfile);
  writeFileSync(path.join(root, runtimePath), readFileSync(new URL(`../../../${runtimePath}`, import.meta.url)));
}

export function agentHostDockerFixture(root, { calls = [], change, inspectChange, runtimeChange, buildCheck, version = '0.1.0' } = {}) {
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
  const runtime = JSON.parse(readFileSync(path.join(root, runtimePath), 'utf8'));
  let image;
  const docker = (args, cwd) => {
    calls.push({ args, cwd });
    if (args[0] === 'buildx' && args[1] === 'build') {
      if (buildCheck) buildCheck(args, cwd);
      const entries = path.join(root, 'artifacts', 'controlled-oci-inputs');
      mkdirSync(path.join(entries, 'blobs', 'sha256'), { recursive: true });
      const blob = (bytes, mediaType) => {
        const digest = `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
        writeFileSync(path.join(entries, 'blobs', 'sha256', digest.slice(7)), bytes);
        return { mediaType, digest, size: bytes.length };
      };
      const config = {
        os: 'linux', architecture: change === 'platform' ? 'arm64' : 'amd64',
        config: {
          User: '1654:1654', Entrypoint: ['dotnet', 'Agentweaver.AgentHost.dll'],
          Labels: {
            'org.opencontainers.image.revision': change === 'source' ? 'c'.repeat(40) : git('rev-parse', 'HEAD'),
            'io.agentweaver.source-tree': change === 'tree' ? 'c'.repeat(40) : git('rev-parse', 'HEAD^{tree}'),
            'org.opencontainers.image.version': change === 'version' ? '9.9.9' : version,
          },
        },
      };
      const configBlob = blob(Buffer.from(JSON.stringify(config)), 'application/vnd.oci.image.config.v1+json');
      image = { Id: configBlob.digest, Os: config.os, Architecture: config.architecture, Config: config.config };
      const layer = blob(gzipSync(Buffer.from('controlled layer content')), 'application/vnd.oci.image.layer.v1.tar+gzip');
      if (change === 'size') layer.size++;
      const descriptor = blob(Buffer.from(JSON.stringify({
        schemaVersion: 2, mediaType: 'application/vnd.oci.image.manifest.v1+json', config: configBlob, layers: [layer],
      })), 'application/vnd.oci.image.manifest.v1+json');
      writeFileSync(path.join(entries, 'index.json'), JSON.stringify({ schemaVersion: 2, manifests: [descriptor] }));
      const archive = args[args.indexOf('--output') + 1].slice('type=oci,dest='.length);
      if (change !== 'missing-oci')
        execFileSync('tar', ['-cf', archive, '-C', entries, 'index.json', 'blobs']);
      return '';
    }
    if (args[0] === 'image' && args[1] === 'inspect' && args.at(-1).includes('@sha256:')) {
      const reference = args.at(-1).replace(/:[^/:]+(?=@sha256:)/, '');
      return JSON.stringify({ Id: `sha256:${'d'.repeat(64)}`, RepoDigests: [reference] });
    }
    if (args[0] === 'image' && args[1] === 'inspect')
      return JSON.stringify(inspectChange ? inspectChange(structuredClone(image)) : image);
    if (args[0] === 'save') {
      if (change !== 'missing-docker')
        writeFileSync(args[args.indexOf('--output') + 1], 'controlled Docker-save archive bytes');
      return '';
    }
    if (args[0] === 'run') {
      const status = {
        sdkVersion: runtime.sdkVersion, runtimeVersion: runtime.runtimeVersion, ProtocolVersion: 3,
        nativeDistribution: '/app/native', privatePackageCacheAbsent: true,
      };
      return JSON.stringify(runtimeChange ? runtimeChange(status) : status);
    }
    return '';
  };
  return docker;
}

export const agentHostComponent = {
  id: 'Agentweaver.AgentHost', kind: 'service', version: '0.1.0', project: AGENTHOST_PROJECT,
};
