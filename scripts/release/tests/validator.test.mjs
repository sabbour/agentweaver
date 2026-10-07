import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { validateFile, validateManifest } from '../validate.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const fixture = JSON.parse(readFileSync(path.join(root, 'releases', 'foundation.json'), 'utf8'));
const projectReferences = new Map([
  ['Agentweaver.FoundationProbe', ['Agentweaver.Abstractions', 'Agentweaver.Providers',
    'Agentweaver.Persistence.Postgres', 'Agentweaver.Secrets.AzureKeyVault',
    'Agentweaver.ObjectStore.AzureBlob', 'Agentweaver.Telemetry.AzureMonitor']
    .map(id => `..\\..\\packages\\${id}\\${id}.csproj`)],
  ['Agentweaver.Providers', ['..\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj']],
  ['Agentweaver.Secrets.AzureKeyVault', ['..\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj']],
  ['Agentweaver.Identity', ['..\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj']],
  ['Agentweaver.Telemetry.AzureMonitor', ['..\\Agentweaver.Telemetry\\Agentweaver.Telemetry.csproj']],
  ['Agentweaver.ObjectStore.AzureBlob', ['..\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj']],
  ['Agentweaver.Identity.Broker', [
    '..\\..\\..\\packages\\Agentweaver.Identity\\Agentweaver.Identity.csproj',
    '..\\..\\..\\packages\\Agentweaver.Secrets.AzureKeyVault\\Agentweaver.Secrets.AzureKeyVault.csproj',
  ]],
  ['Agentweaver.Orchestrator.Core', [
    '..\\..\\..\\packages\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj',
    '..\\..\\..\\packages\\Agentweaver.Providers\\Agentweaver.Providers.csproj',
  ]],
  ['Agentweaver.Orchestrator', [
    '..\\Agentweaver.Orchestrator.Core\\Agentweaver.Orchestrator.Core.csproj',
    '..\\..\\..\\packages\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj',
    '..\\..\\..\\packages\\Agentweaver.Persistence.Postgres\\Agentweaver.Persistence.Postgres.csproj',
  ]],
  ['Agentweaver.EventsAndSessions', [
    '..\\..\\..\\packages\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj',
    '..\\..\\..\\packages\\Agentweaver.Providers\\Agentweaver.Providers.csproj',
    '..\\..\\..\\packages\\Agentweaver.Persistence.Postgres\\Agentweaver.Persistence.Postgres.csproj',
    '..\\..\\..\\packages\\Agentweaver.Telemetry\\Agentweaver.Telemetry.csproj',
  ]],
  ['Agentweaver.Projects.Config', [
    '..\\..\\..\\packages\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj',
    '..\\..\\..\\packages\\Agentweaver.Providers\\Agentweaver.Providers.csproj',
  ]],
  ['Agentweaver.Environment', [
    '..\\..\\..\\packages\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj',
    '..\\..\\..\\packages\\Agentweaver.Providers\\Agentweaver.Providers.csproj',
    '..\\..\\..\\packages\\Agentweaver.Providers.Storage.AzureFiles\\Agentweaver.Providers.Storage.AzureFiles.csproj',
  ]],
  ['Agentweaver.Providers.Storage.AzureFiles', [
    '..\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj',
  ]],
  ['Agentweaver.Knowledge', [
    '..\\..\\..\\packages\\Agentweaver.Abstractions\\Agentweaver.Abstractions.csproj',
    '..\\..\\..\\packages\\Agentweaver.Providers\\Agentweaver.Providers.csproj',
    '..\\..\\..\\packages\\Agentweaver.Persistence.Postgres\\Agentweaver.Persistence.Postgres.csproj',
    '..\\..\\..\\packages\\Agentweaver.Telemetry\\Agentweaver.Telemetry.csproj',
  ]],
]);
const projects = new Map(fixture.components.map((component) => {
  const references = projectReferences.get(component.id) ?? [];
  const itemGroup = references.length === 0 ? '' :
    `<ItemGroup>${references.map((reference) => `<ProjectReference Include="${reference}" />`).join('')}</ItemGroup>`;
  return [component.project,
    `<Project><PropertyGroup><Version>${component.version}</Version></PropertyGroup>${itemGroup}</Project>`];
}));
const readProject = (file) => {
  const relative = path.relative(root, file).replaceAll('\\', '/');
  if (!projects.has(relative)) throw new Error(`missing ${relative}`);
  return projects.get(relative);
};
const check = (manifest, overrides = {}) => validateManifest(manifest, { root, readProject, ...overrides });
const edit = (change) => {
  const manifest = structuredClone(fixture);
  change(manifest);
  return manifest;
};
const nextPatchVersion = (version) => {
  const [major, minor, patch] = version.split('.').map(Number);
  return `${major}.${minor}.${patch + 1}`;
};

test('the checked-in draft composition has a valid shape and references', () => {
  assert.equal(check(fixture).stage, 'draft');
});

test('a library component may be checked in under services', () => {
  const orchestrator = fixture.components.find(component => component.id === 'Agentweaver.Orchestrator.Core');
  assert.equal(orchestrator.kind, 'library');
  assert.ok(orchestrator.project.startsWith('services/'));
  assert.equal(check(fixture).stage, 'draft');
});

test('the Orchestrator host is registered as a baseline service with all project dependencies pinned', () => {
  const host = fixture.components.find(component => component.id === 'Agentweaver.Orchestrator');
  assert.equal(host.kind, 'service');
  assert.equal(host.version, '0.0.0');
  assert.equal(host.project, 'services/orchestrator/Agentweaver.Orchestrator/Agentweaver.Orchestrator.csproj');
  assert.deepEqual(fixture.compatibility
    .filter(({ consumer }) => consumer === host.id)
    .map(({ dependency, versions }) => [dependency, versions]), [
    ['Agentweaver.Orchestrator.Core', ['0.0.0']],
    ['Agentweaver.Abstractions', ['0.0.0']],
    ['Agentweaver.Persistence.Postgres', ['0.0.0']],
  ]);
  assert.equal(check(fixture).stage, 'draft');
});

test('the acceptance-only Probe is tracked as its exact image artifact, not an arbitrary tools project', () => {
  const probe = fixture.components.find(component => component.id === 'Agentweaver.FoundationProbe');
  assert.equal(probe.kind, 'service');
  assert.equal(probe.version, '0.0.0');
  assert.equal(probe.project, 'tools/Agentweaver.FoundationProbe/Agentweaver.FoundationProbe.csproj');
  assert.throws(() => check(edit(manifest => {
    manifest.components.find(component => component.id === probe.id).project = 'tools/Other/Other.csproj';
  })), /safe services/);
});

test('the initial P0 NuGet baseline contains exactly the eight packages at 0.0.0', () => {
  const packageIds = [
    'Agentweaver.Abstractions',
    'Agentweaver.Providers',
    'Agentweaver.Persistence.Postgres',
    'Agentweaver.Secrets.AzureKeyVault',
    'Agentweaver.Identity',
    'Agentweaver.Telemetry',
    'Agentweaver.Telemetry.AzureMonitor',
    'Agentweaver.ObjectStore.AzureBlob',
  ].sort();
  const packages = fixture.components.filter(({ id }) => packageIds.includes(id));
  assert.deepEqual(packages.map(({ id }) => id).sort(), packageIds);
  assert.ok(packages.every(({ version }) => version === '0.0.0'));
  assert.ok(fixture.compatibility.every(({ versions }) => versions.length === 1 && versions[0] === '0.0.0'));

  const baseline = JSON.parse(readFileSync(path.join(root, 'releases', 'initial-baseline.json'), 'utf8'));
  assert.equal(baseline.baselineVersion, '0.0.0');
  assert.deepEqual([...baseline.components].sort(), packageIds);
  assert.ok(!baseline.components.includes('Agentweaver.Orchestrator.Core'));
  assert.equal(baseline.changeset, '.changeset/initial-p0-baseline-1825.md');
});

test('the CLI accepts the checked-in composition and JSON schema is parseable', () => {
  const schema = JSON.parse(readFileSync(path.join(root, 'releases', 'manifest.schema.json'), 'utf8'));
  assert.equal(schema.$id, 'https://agentweaver.dev/schemas/release-manifest-v1');
  const result = spawnSync(process.execPath,
    [path.join(root, 'scripts', 'release', 'validate.mjs'), 'releases/foundation.json'],
    { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Validated releases\/foundation.json/);
});

test('rejects malformed JSON and returns an actionable CLI error and nonzero exit', () => {
  const file = path.join(root, 'scripts', 'release', 'tests', 'fixtures', 'malformed.json');
  assert.throws(() => validateFile(file), /invalid JSON/);
  const result = spawnSync(process.execPath, [path.join(root, 'scripts', 'release', 'validate.mjs'), file], { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /Release manifest validation failed: .*invalid JSON/);
});

test('rejects missing CLI arguments without a stack trace', () => {
  const result = spawnSync(process.execPath, [path.join(root, 'scripts', 'release', 'validate.mjs')], { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /usage: node scripts\/release\/validate.mjs/);
  assert.doesNotMatch(result.stderr, /\n\s+at /);
});

test('rejects unsupported schema and missing required keys', () => {
  assert.throws(() => check(edit((m) => { m.schemaVersion = 2; })), /schema version 1/);
  assert.throws(() => check(edit((m) => { delete m.components; })), /missing required field "components"/);
  assert.throws(() => check(edit((m) => { delete m.compatibility[0].versions; })), /missing required field "versions"/);
});

test('rejects unknown fields at every nesting level', () => {
  assert.throws(() => check(edit((m) => { m.platformVersion = '1.0.0'; })), /unknown field "platformVersion"/);
  assert.throws(() => check(edit((m) => { m.components[0].surprise = true; })), /unknown field "surprise"/);
  assert.throws(() => check(edit((m) => { m.compatibility[0].range = '^0.1'; })), /unknown field "range"/);
});

test('rejects duplicate component IDs and paths', () => {
  assert.throws(() => check(edit((m) => { m.components[1].id = m.components[0].id; })), /duplicate component ID/);
  assert.throws(() => check(edit((m) => { m.components[1].project = m.components[0].project; })), /duplicate project path/);
});

test('rejects invalid component kinds, paths and image digests', () => {
  assert.throws(() => check(edit((m) => { m.components[0].kind = 'container'; })), /expected contract, library, or service/);
  assert.throws(() => check(edit((m) => { m.components[0].project = 'packages/../outside.csproj'; })), /safe packages/);
  assert.throws(() => check(edit((m) => { m.components[0].imageDigest = 'sha256:fake'; })), /only services/);
  assert.throws(() => check(edit((m) => { m.components[0].kind = 'service'; })), /safe services/);
});

test('rejects invalid semver and version drift from checked-in projects', () => {
  for (const invalid of ['1', '01.0.0', '1.0.0-01', '1.0.0-', '1.0.0+']) {
    assert.throws(() => check(edit((m) => { m.components[0].version = invalid; })), /semantic version/);
  }
  const abstraction = fixture.components.find((component) => component.id === 'Agentweaver.Abstractions');
  const mismatchedVersion = nextPatchVersion(abstraction.version);
  assert.throws(() => check(edit((m) => {
    m.components.find((component) => component.id === abstraction.id).version = mismatchedVersion;
  })), new RegExp(`must declare <Version>${mismatchedVersion}</Version>`));
  assert.throws(() => check(fixture, { readProject: (file) => {
    const project = readProject(file);
    return path.basename(file) === path.basename(abstraction.project)
      ? project.replace(`<Version>${abstraction.version}</Version>`, `<Version>${mismatchedVersion}</Version>`)
      : project;
  } }), new RegExp(`must declare <Version>${abstraction.version}</Version>`));
  assert.throws(() => check(fixture, { readProject: () => { throw new Error('ENOENT'); } }), /cannot read checked-in project: ENOENT/);
});

test('rejects dangling, duplicate, unsupported, and missing compatibility edges', () => {
  assert.throws(() => check(edit((m) => { m.compatibility[0].dependency = 'Unknown'; })), /distinct declared component IDs/);
  const dependency = fixture.components.find((component) => component.id === fixture.compatibility[0].dependency);
  const unsupportedVersion = nextPatchVersion(dependency.version);
  assert.throws(() => check(edit((m) => { m.compatibility[0].versions = [unsupportedVersion]; })), /does not include pinned/);
  assert.throws(() => check(edit((m) => { m.compatibility[0].versions = [dependency.version, dependency.version]; })), /duplicate supported version/);
  assert.throws(() => check(edit((m) => { m.compatibility.push(m.compatibility[0]); })), /duplicate compatibility relationship/);
  assert.throws(() => check(edit((m) => { m.compatibility = []; })), /missing compatibility/);
  const providerProject = projects.get(fixture.components.find((component) => component.id === 'Agentweaver.Providers').project);
  assert.throws(() => check(fixture, { readProject: (file) => file.includes('Providers.csproj')
    ? providerProject.replace(/<ProjectReference\b[^>]*\/>/, '') : readProject(file) }), /has no ProjectReference/);
  assert.throws(() => check(fixture, { readProject: (file) => file.includes('Providers.csproj')
    ? providerProject.replace('Agentweaver.Abstractions.csproj', 'Unpinned.csproj') : readProject(file) }), /not pinned in the manifest/);
});

test('scans both XML quote forms and ignores commented-out versions and references', () => {
  const providersProject = projects.get(fixture.components.find((component) => component.id === 'Agentweaver.Providers').project);
  const singleQuoted = providersProject.replace(/Include="([^"]+)"/, "Include = '$1'");
  assert.equal(check(fixture, {
    readProject: (file) => file.includes('Providers.csproj') ? singleQuoted : readProject(file),
  }).stage, 'draft');
  assert.throws(() => check(edit((m) => { m.compatibility = []; }), {
    readProject: (file) => file.includes('Providers.csproj')
      ? singleQuoted.replace('Agentweaver.Abstractions.csproj', 'Unpinned.csproj') : readProject(file),
  }), /not pinned in the manifest/);
  const componentByProject = new Map(fixture.components.map((component) => [component.project, component]));
  assert.equal(check(edit((m) => { m.compatibility = []; }), {
    readProject: (file) => {
      const relative = path.relative(root, file).replaceAll('\\', '/');
      const component = componentByProject.get(relative);
      if (!component) throw new Error(`missing ${relative}`);
      return `<Project><PropertyGroup><Version>${component.version}</Version></PropertyGroup><!-- <Version>9.0.0</Version><ProjectReference Include='Unpinned.csproj' /> --></Project>`;
    },
  }).stage, 'draft');
});

test('schema and executable agree on semantic version identifiers', () => {
  const schema = JSON.parse(readFileSync(path.join(root, 'releases', 'manifest.schema.json'), 'utf8'));
  const pattern = new RegExp(schema.$defs.version.pattern);
  for (const valid of ['0.1.0', '1.0.0-0', '1.0.0-alpha.01a', '1.0.0+001', '1.0.0-rc.1+build.2']) {
    assert.ok(pattern.test(valid), valid);
    assert.equal(check(edit((m) => {
      m.compatibility[0].versions = [...new Set([...m.compatibility[0].versions, valid])];
    })).stage, 'draft');
  }
  for (const invalid of ['01.0.0', '1.0.0-01', '1.0.0-alpha.01', '1.0.0-', '1.0.0+']) {
    assert.ok(!pattern.test(invalid), invalid);
    assert.throws(() => check(edit((m) => { m.compatibility[0].versions.push(invalid); })), /semantic version/);
  }
});

test('drafts cannot carry deployment evidence, and releases need actual services', () => {
  assert.throws(() => check(edit((m) => { m.evidence = {}; })), /draft must not claim/);
  assert.throws(() => check(edit((m) => {
    m.stage = 'release';
    m.components = m.components.filter((component) => component.kind !== 'service');
    const componentIds = new Set(m.components.map((component) => component.id));
    m.compatibility = m.compatibility.filter((rule) =>
      componentIds.has(rule.consumer) && componentIds.has(rule.dependency));
  })), /actual deployable service/);
});

const sourceSha = 'a'.repeat(40);
const validEvidence = {
  sourceSha,
  deploymentSha: sourceSha,
  personaRuns: ['api', 'ui', 'mcp'].map((surface) => ({ surface, sourceSha, runUrl: `https://example.org/${surface}` })),
};
const release = () => edit((m) => {
  m.stage = 'release';
  for (const component of m.components) {
    if (component.kind === 'service') component.imageDigest = `sha256:${'b'.repeat(64)}`;
  }
  m.components.push({
    id: 'Agentweaver.Example',
    kind: 'service',
    version: '1.0.0',
    project: 'services/Agentweaver.Example/Agentweaver.Example.csproj',
    imageDigest: `sha256:${'b'.repeat(64)}`,
  });
  m.evidence = structuredClone(validEvidence);
});
const releaseProject = (file) => file.includes('Example.csproj')
  ? '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>' : readProject(file);
const checkRelease = (manifest) => check(manifest, { readProject: releaseProject });

test('release requires exact-SHA matching AKS and distinct API/UI/MCP evidence', () => {
  assert.equal(checkRelease(release()).stage, 'release');
  assert.throws(() => checkRelease(editRelease((m) => { m.evidence.sourceSha = 'z'.repeat(40); })), /lowercase 40-character Git SHA/);
  assert.throws(() => checkRelease(editRelease((m) => { m.evidence.deploymentSha = 'c'.repeat(40); })), /deployment SHA must equal/);
  assert.throws(() => checkRelease(editRelease((m) => { m.evidence.personaRuns[1].sourceSha = 'c'.repeat(40); })), /persona SHA must equal/);
  assert.throws(() => checkRelease(editRelease((m) => { m.evidence.personaRuns[1].surface = 'api'; })), /each of api, ui, and mcp/);
  assert.throws(() => checkRelease(editRelease((m) => { m.evidence.personaRuns[1].runUrl = 'http://example.org'; })), /HTTPS evidence URL/);
  assert.throws(() => checkRelease(editRelease((m) => { m.evidence.personaRuns[1].unknown = true; })), /unknown field "unknown"/);
  assert.throws(() => checkRelease(editRelease((m) => { m.components.at(-1).imageDigest = 'bad'; })), /sha256 image digest/);
  assert.throws(() => checkRelease(editRelease((m) => {
    m.components.at(-1).imageDigest = [`sha256:${'b'.repeat(64)}`];
  })), /sha256 image digest/);
});

function editRelease(change) {
  const manifest = release();
  change(manifest);
  return manifest;
}

test('draft services may honestly omit an image digest, but release services may not', () => {
  const draftWithService = edit((m) => {
    m.components.push({
      id: 'Agentweaver.Example',
      kind: 'service',
      version: '1.0.0',
      project: 'services/Agentweaver.Example/Agentweaver.Example.csproj',
    });
  });
  assert.equal(check(draftWithService, { readProject: releaseProject }).stage, 'draft');
  const draftWithDigest = edit((m) => {
    m.components.push({
      id: 'Agentweaver.Example',
      kind: 'service',
      version: '1.0.0',
      project: 'services/Agentweaver.Example/Agentweaver.Example.csproj',
      imageDigest: `sha256:${'b'.repeat(64)}`,
    });
  });
  assert.equal(check(draftWithDigest, { readProject: releaseProject }).stage, 'draft');
  const draftWithBadDigest = edit((m) => {
    m.components.push({
      id: 'Agentweaver.Example',
      kind: 'service',
      version: '1.0.0',
      project: 'services/Agentweaver.Example/Agentweaver.Example.csproj',
      imageDigest: 'sha256:not-a-digest',
    });
  });
  assert.throws(() => check(draftWithBadDigest, { readProject: releaseProject }), /sha256 image digest/);
  assert.throws(() => checkRelease(editRelease((m) => { delete m.components.at(-1).imageDigest; })),
    /requires a published sha256 image digest/);
});
