import { globSync } from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { ensureProject, npmInvocation } from './shared-deps.mjs';

const ALL_AREAS = ['node', 'harness', 'web', 'docs', 'dotnet'];

function run(command, args, cwd) {
  const label = `${command} ${args.join(' ')}`;
  const startedAt = performance.now();
  console.log(`[validate] starting ${label}`);
  const result = spawnSync(command, args, {
    cwd,
    env: process.env,
    shell: process.platform === 'win32' && command.endsWith('.cmd'),
    stdio: 'inherit',
  });
  const elapsedSeconds = ((performance.now() - startedAt) / 1000).toFixed(2);
  console.log(`[validate] finished ${label} in ${elapsedSeconds}s`);
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(`${label} failed with exit code ${result.status}`);
  }
}

function runNpm(args, repoRoot) {
  const invocation = npmInvocation(args);
  run(invocation.command, invocation.args, repoRoot);
}

function parseArgs(argv) {
  const options = {
    profile: 'layer',
    areas: [],
    base: 'origin/dev',
    dotnetFilter: null,
    isolatedDeps: process.env.CI === 'true',
  };
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--profile') {
      options.profile = argv[++index];
    } else if (argument === '--area') {
      options.areas.push(...argv[++index].split(','));
    } else if (argument === '--base') {
      options.base = argv[++index];
    } else if (argument === '--dotnet-filter') {
      options.dotnetFilter = argv[++index];
    } else if (argument === '--isolated-deps') {
      options.isolatedDeps = true;
    } else {
      throw new Error(`unknown argument: ${argument}`);
    }
  }
  if (!['layer', 'full', 'ci'].includes(options.profile)) {
    throw new Error(`unknown validation profile: ${options.profile}`);
  }
  return options;
}

function gitLines(repoRoot, args) {
  const result = spawnSync('git', args, {
    cwd: repoRoot,
    encoding: 'utf8',
    shell: false,
  });
  if (result.status !== 0) {
    throw new Error(result.stderr.trim() || `git ${args.join(' ')} failed`);
  }
  return result.stdout.split(/\r?\n/u).filter(Boolean);
}

export function areasForPaths(paths) {
  const areas = new Set();
  for (const rawPath of paths) {
    const filePath = rawPath.replaceAll('\\', '/');
    if (
      filePath.startsWith('scripts/azure/')
      || filePath.startsWith('scripts/changesets/')
      || filePath.startsWith('scripts/ci/')
      || filePath === 'package.json'
      || filePath === 'package-lock.json'
    ) {
      areas.add('node');
    }
    if (
      filePath.startsWith('scripts/ui-harness/')
      || filePath.startsWith('scripts/harness-judge/')
      || filePath.startsWith('scripts/harness-shared/')
      || filePath.startsWith('scripts/persona-briefs/')
    ) {
      areas.add('harness');
    }
    if (filePath.startsWith('apps/web/')) {
      areas.add('web');
    }
    if (filePath.startsWith('docs/')) {
      areas.add('docs');
    }
    if (
      filePath.startsWith('tests/')
      || filePath.endsWith('.cs')
      || filePath.endsWith('.csproj')
      || filePath.endsWith('.sln')
      || filePath.endsWith('.props')
      || filePath.endsWith('.targets')
      || filePath === 'global.json'
      || filePath.toLowerCase() === 'nuget.config'
    ) {
      areas.add('dotnet');
    }
    if (filePath.startsWith('.github/workflows/')) {
      for (const area of ALL_AREAS) {
        areas.add(area);
      }
    }
  }
  return [...areas];
}

function changedAreas(repoRoot, base) {
  const committed = gitLines(
    repoRoot,
    ['diff', '--no-renames', '--name-only', `${base}...HEAD`],
  );
  const working = gitLines(repoRoot, ['diff', '--no-renames', '--name-only']);
  const staged = gitLines(repoRoot, ['diff', '--no-renames', '--cached', '--name-only']);
  const statusPaths = gitLines(
    repoRoot,
    ['status', '--porcelain=v1', '--untracked-files=all'],
  ).map((line) => {
    const statusPath = line.slice(3);
    const renameSeparator = statusPath.lastIndexOf(' -> ');
    return renameSeparator >= 0 ? statusPath.slice(renameSeparator + 4) : statusPath;
  });
  return areasForPaths([...new Set([...committed, ...working, ...staged, ...statusPaths])]);
}

function ensureDependencies(repoRoot, project, isolated) {
  ensureProject({
    repoRoot,
    project,
    isolated,
  });
}

function runNodeTests(repoRoot) {
  const files = [
    ...globSync('scripts/azure/tests/*.test.mjs', { cwd: repoRoot }),
    ...globSync('scripts/changesets/tests/*.test.mjs', { cwd: repoRoot }),
    ...globSync('scripts/ci/tests/*.test.mjs', { cwd: repoRoot }),
  ];
  run(process.execPath, ['--test', ...files], repoRoot);
}

function runWeb(repoRoot, isolated, selection) {
  ensureDependencies(repoRoot, 'apps/web', isolated);
  if (selection !== 'lint') {
    runNpm(['--prefix', 'apps/web', 'run', 'test'], repoRoot);
  }
  if (selection !== 'test') {
    runNpm(['--prefix', 'apps/web', 'run', 'lint'], repoRoot);
  }
}

function runHarness(repoRoot, isolated) {
  ensureDependencies(repoRoot, 'scripts/ui-harness', isolated);
  runNpm(['--prefix', 'scripts/ui-harness', 'test'], repoRoot);
}

function runDocs(repoRoot, isolated) {
  ensureDependencies(repoRoot, 'docs', isolated);
  runNpm(['--prefix', 'docs', 'run', 'build'], repoRoot);
}

function runDotnet(repoRoot, profile, filter) {
  if (profile === 'layer' && !filter) {
    throw new Error(
      'layer validation for .NET requires --dotnet-filter. '
      + 'Use a FullyQualifiedName filter for the changed component, or use --profile full at the stack top.',
    );
  }
  const args = [
    'test',
    'tests/Agentweaver.Tests/Agentweaver.Tests.csproj',
    '-p:CopilotSkipCliDownload=true',
  ];
  if (filter) {
    args.push('--filter', filter);
  }
  run('dotnet', args, repoRoot);
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
  let areas = options.areas;
  if (areas.length === 0) {
    areas = options.profile === 'full' || options.profile === 'ci'
      ? ALL_AREAS
      : changedAreas(repoRoot, options.base);
  }
  areas = [...new Set(areas)];
  const unknownAreas = areas.filter((area) => (
    !ALL_AREAS.includes(area) && !['web-test', 'web-lint'].includes(area)
  ));
  if (unknownAreas.length > 0) {
    throw new Error(`unknown validation area(s): ${unknownAreas.join(', ')}`);
  }
  if (areas.length === 0) {
    console.log('[validate] no validation areas changed');
    return;
  }

  const startedAt = performance.now();
  console.log(`[validate] profile=${options.profile} areas=${areas.join(',')}`);
  if (areas.includes('node')) {
    runNodeTests(repoRoot);
  }
  if (areas.includes('harness')) {
    runHarness(repoRoot, options.isolatedDeps);
  }
  if (areas.includes('web') || areas.includes('web-test')) {
    runWeb(repoRoot, options.isolatedDeps, areas.includes('web-test') ? 'test' : 'both');
  } else if (areas.includes('web-lint')) {
    runWeb(repoRoot, options.isolatedDeps, 'lint');
  }
  if (areas.includes('docs')) {
    runDocs(repoRoot, options.isolatedDeps);
  }
  if (areas.includes('dotnet')) {
    runDotnet(repoRoot, options.profile, options.dotnetFilter);
  }
  const elapsedSeconds = ((performance.now() - startedAt) / 1000).toFixed(2);
  console.log(`[validate] profile ${options.profile} completed in ${elapsedSeconds}s`);
}

const isMain = process.argv[1]
  && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url));
if (isMain) {
  try {
    main();
  } catch (error) {
    console.error(`[validate] ${error?.stack ?? error}`);
    process.exitCode = 1;
  }
}
