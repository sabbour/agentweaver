import { appendFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export function classify(paths, full = false) {
  const result = { dotnet: full, web: full, node: full, images: full };
  for (const path of paths) {
    if (/^(docs\/|\.squad\/|\.changeset\/)/.test(path) || /^[^/]+\.md$/.test(path)) continue;
    if (path.startsWith('.github/workflows/') && path !== '.github/workflows/v1-ci.yml') continue;
    if (/^(apps\/web\/)/.test(path)) {
      result.dotnet = result.web = result.node = result.images = true;
    } else if (/^(services\/|packages\/|tests\/|deploy\/)/.test(path) ||
               /(^|\/)([^/]+\.(csproj|props|targets|slnx)|packages\.lock\.json)$/.test(path) ||
               /^(global\.json|[Nn]u[Gg]et\.[Cc]onfig|coverage\.runsettings)$/.test(path)) {
      result.dotnet = result.images = true;
      if (/^(deploy\/|services\/agenthost\/)/.test(path) ||
          /(^|\/)([^/]+\.(csproj|props|targets|slnx)|packages\.lock\.json)$/.test(path) ||
          /^(global\.json|[Nn]u[Gg]et\.[Cc]onfig)$/.test(path)) result.node = true;
    } else if (/^scripts\/(release|azure|.*harness|harness-shared)\//.test(path) ||
               path.startsWith('.github/extensions/')) {
      result.node = result.images = true;
    } else {
      for (const key of Object.keys(result)) result[key] = true;
    }
  }
  // Broker browser tests need the exact candidate's built web client.
  if (result.dotnet || result.node) result.web = true;
  return result;
}

function main() {
  const base = process.env.CI_DIFF_BASE;
  const full = process.env.GITHUB_EVENT_NAME === 'workflow_dispatch' ||
    !base || /^0+$/.test(base) || process.env.COLLECT_COVERAGE === 'true';
  let paths = [];
  if (!full) {
    const diff = spawnSync('git', ['diff', '--name-only', '-z', base, 'HEAD'], { encoding: 'utf8' });
    if (diff.error || diff.status !== 0) throw new Error(`Cannot classify changed paths: ${diff.error?.message ?? diff.stderr}`);
    paths = diff.stdout.split('\0').filter(Boolean);
  }
  const outputs = classify(paths, full);
  const lines = Object.entries(outputs).map(([name, value]) => `${name}=${value}`).join('\n') + '\n';
  if (!process.env.GITHUB_OUTPUT) throw new Error('GITHUB_OUTPUT is required');
  appendFileSync(process.env.GITHUB_OUTPUT, lines);
  console.log(lines);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
