import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { diffChanges, resolveMergeBase, validateChangesets } from './changesets.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const semver = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const sha = /^[a-f0-9]{40}$/;
const digest = /^sha256:[a-f0-9]{64}$/;
const id = /^[A-Za-z][A-Za-z0-9.-]*$/;

function fail(location, message) {
  throw new Error(`${location}: ${message}`);
}

function object(value, location, required, allowed = required) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    fail(location, 'expected an object');
  }
  for (const key of required) {
    if (!Object.hasOwn(value, key)) fail(location, `missing required field "${key}"`);
  }
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) fail(location, `unknown field "${key}"`);
  }
}

function array(value, location, min = 0) {
  if (!Array.isArray(value) || value.length < min) {
    fail(location, `expected an array with at least ${min} item(s)`);
  }
}

function version(value, location) {
  const match = typeof value === 'string' && semver.exec(value);
  if (!match || (match[4] && match[4].split('.').some((part) => /^\d+$/.test(part) && part.length > 1 && part[0] === '0'))) {
    fail(location, 'expected a valid semantic version (major.minor.patch)');
  }
}

function projectPath(value, kind, location) {
  if (kind === 'service' && value === 'tools/Agentweaver.FoundationProbe/Agentweaver.FoundationProbe.csproj') return;
  const prefixes = kind === 'service' ? ['services'] : kind === 'library' ? ['packages', 'services'] : ['packages'];
  const expectedPaths = prefixes.map((prefix) => `${prefix}/.../*.csproj`).join(' or ');
  if (typeof value !== 'string' || !prefixes.some((prefix) =>
      new RegExp(`^${prefix}/[A-Za-z0-9./-]+\\.csproj$`).test(value)) ||
      value.split('/').some((part) => part === '.' || part === '..' || part === '')) {
    fail(location, `expected a safe ${expectedPaths} path`);
  }
}

function projectFile(project, root, readProject) {
  const file = path.resolve(root, project);
  if (!file.startsWith(root + path.sep)) fail(project, 'project escapes repository root');
  try {
    return readProject(file);
  } catch (error) {
    fail(project, `cannot read checked-in project: ${error.message}`);
  }
}

export function validateManifest(manifest, { root = repositoryRoot, readProject = (file) => readFileSync(file, 'utf8') } = {}) {
  root = path.resolve(root);
  object(manifest, 'manifest', ['schemaVersion', 'stage', 'components', 'compatibility'],
    ['$schema', 'schemaVersion', 'stage', 'components', 'compatibility', 'evidence']);
  if (manifest.$schema !== undefined && manifest.$schema !== './manifest.schema.json') {
    fail('manifest.$schema', 'expected "./manifest.schema.json"');
  }
  if (manifest.schemaVersion !== 1) fail('manifest.schemaVersion', 'expected schema version 1');
  if (!['draft', 'release'].includes(manifest.stage)) fail('manifest.stage', 'expected "draft" or "release"');
  array(manifest.components, 'manifest.components', 1);
  array(manifest.compatibility, 'manifest.compatibility');

  const components = new Map();
  const projects = new Set();
  for (const [index, component] of manifest.components.entries()) {
    const location = `manifest.components[${index}]`;
    object(component, location, ['id', 'kind', 'version', 'project'],
      ['id', 'kind', 'version', 'project', 'imageDigest']);
    if (typeof component.id !== 'string' || !id.test(component.id)) fail(`${location}.id`, 'invalid component ID');
    if (components.has(component.id)) fail(`${location}.id`, `duplicate component ID "${component.id}"`);
    if (!['contract', 'library', 'service'].includes(component.kind)) fail(`${location}.kind`, 'expected contract, library, or service');
    version(component.version, `${location}.version`);
    projectPath(component.project, component.kind, `${location}.project`);
    if (projects.has(component.project)) fail(`${location}.project`, 'duplicate project path');
    projects.add(component.project);
    if (component.kind === 'service') {
      // A draft composition may represent an unpublished service honestly, with no digest yet.
      // When present (draft or release), it must still be a well-formed sha256 digest.
      if (component.imageDigest !== undefined && (typeof component.imageDigest !== 'string' || !digest.test(component.imageDigest))) {
        fail(`${location}.imageDigest`, 'service requires a sha256 image digest');
      }
    } else if (component.imageDigest !== undefined) {
      fail(`${location}.imageDigest`, 'only services have image digests');
    }
    const xml = projectFile(component.project, root, readProject).replace(/<!--[\s\S]*?-->/g, '');
    const versions = [...xml.matchAll(/<Version>([^<]*)<\/Version>/g)];
    if (versions.length !== 1 || versions[0][1].trim() !== component.version) {
      fail(`${location}.version`, `checked-in ${component.project} must declare <Version>${component.version}</Version>`);
    }
    components.set(component.id, { ...component, xml });
  }

  const compatibility = new Map();
  for (const [index, rule] of manifest.compatibility.entries()) {
    const location = `manifest.compatibility[${index}]`;
    object(rule, location, ['consumer', 'dependency', 'versions']);
    const consumer = components.get(rule.consumer);
    const dependency = components.get(rule.dependency);
    if (!consumer || !dependency || rule.consumer === rule.dependency) {
      fail(location, 'consumer and dependency must be distinct declared component IDs');
    }
    array(rule.versions, `${location}.versions`, 1);
    for (const [i, supported] of rule.versions.entries()) version(supported, `${location}.versions[${i}]`);
    if (new Set(rule.versions).size !== rule.versions.length) fail(`${location}.versions`, 'duplicate supported version');
    if (!rule.versions.includes(dependency.version)) {
      fail(`${location}.versions`, `does not include pinned ${rule.dependency} ${dependency.version}`);
    }
    const key = `${rule.consumer}\0${rule.dependency}`;
    if (compatibility.has(key)) fail(location, 'duplicate compatibility relationship');
    compatibility.set(key, true);
  }

  for (const [consumerId, consumer] of components) {
    const references = [...consumer.xml.matchAll(/<ProjectReference\b[^>]*\bInclude\s*=\s*(["'])(.*?)\1[^>]*\/?>/g)];
    const referenced = new Set();
    for (const match of references) {
      const target = path.resolve(path.dirname(path.resolve(root, consumer.project)), match[2].replaceAll('\\', '/'));
      const dependency = [...components.values()].find((item) => path.resolve(root, item.project) === target);
      if (!dependency) fail(consumer.project, `ProjectReference "${match[2]}" is not pinned in the manifest`);
      const key = `${consumerId}\0${dependency.id}`;
      if (referenced.has(key)) fail(consumer.project, `duplicate ProjectReference to ${dependency.id}`);
      referenced.add(key);
      if (!compatibility.has(key)) fail(consumer.project, `missing compatibility for ${consumerId} -> ${dependency.id}`);
    }
    for (const key of compatibility.keys()) {
      if (key.startsWith(`${consumerId}\0`) && !referenced.has(key)) {
        fail('manifest.compatibility', `relationship ${key.replace('\0', ' -> ')} has no ProjectReference`);
      }
    }
  }

  if (manifest.stage === 'draft') {
    if (manifest.evidence !== undefined) fail('manifest.evidence', 'draft must not claim deployment or persona evidence');
  } else {
    const services = [...components.values()].filter((component) => component.kind === 'service');
    if (services.length === 0) {
      fail('manifest.components', 'release requires an actual deployable service');
    }
    for (const service of services) {
      if (typeof service.imageDigest !== 'string' || !digest.test(service.imageDigest)) {
        fail(`manifest.components[${service.id}].imageDigest`, 'release requires a published sha256 image digest for each service');
      }
    }
    object(manifest.evidence, 'manifest.evidence', ['sourceSha', 'deploymentSha', 'personaRuns']);
    const evidence = manifest.evidence;
    if (!sha.test(evidence.sourceSha)) fail('manifest.evidence.sourceSha', 'expected a lowercase 40-character Git SHA');
    if (!sha.test(evidence.deploymentSha) || evidence.deploymentSha !== evidence.sourceSha) {
      fail('manifest.evidence.deploymentSha', 'AKS deployment SHA must equal source SHA');
    }
    array(evidence.personaRuns, 'manifest.evidence.personaRuns', 3);
    if (evidence.personaRuns.length !== 3) fail('manifest.evidence.personaRuns', 'expected exactly three surfaces');
    const surfaces = new Set();
    for (const [index, run] of evidence.personaRuns.entries()) {
      const location = `manifest.evidence.personaRuns[${index}]`;
      object(run, location, ['surface', 'sourceSha', 'runUrl']);
      if (!['api', 'ui', 'mcp'].includes(run.surface) || surfaces.has(run.surface)) {
        fail(`${location}.surface`, 'expected each of api, ui, and mcp exactly once');
      }
      surfaces.add(run.surface);
      if (!sha.test(run.sourceSha) || run.sourceSha !== evidence.sourceSha) {
        fail(`${location}.sourceSha`, 'persona SHA must equal deployed source SHA');
      }
      if (typeof run.runUrl !== 'string' || !/^https:\/\/[^\s]+$/.test(run.runUrl)) {
        fail(`${location}.runUrl`, 'expected an HTTPS evidence URL');
      }
    }
  }
  return manifest;
}

export function validateFile(file, options = {}) {
  let text;
  try {
    text = readFileSync(file, 'utf8');
  } catch (error) {
    fail(file, `cannot read manifest: ${error.message}`);
  }
  let manifest;
  try {
    manifest = JSON.parse(text);
  } catch (error) {
    fail(file, `invalid JSON: ${error.message}`);
  }
  return validateManifest(manifest, options);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const file = process.argv[2];
    if (!file || ![3, 5].includes(process.argv.length) ||
        (process.argv.length === 5 && process.argv[3] !== '--base')) {
      fail('usage', 'node scripts/release/validate.mjs <manifest.json> [--base <full-git-sha>]');
    }
    const manifest = validateFile(file);
    const changes = process.argv.length === 5 ? diffChanges(repositoryRoot, process.argv[4]) : undefined;
    const ancestorSha = process.argv.length === 5 ? resolveMergeBase(repositoryRoot, process.argv[4]) : undefined;
    const records = validateChangesets(manifest, { root: repositoryRoot, changes, ancestorSha });
    console.log(`Validated ${file} and ${records.size} changesets${changes ? ' with diff coverage' : ''}`);
  } catch (error) {
    console.error(`Release manifest validation failed: ${error.message}`);
    process.exitCode = 1;
  }
}
