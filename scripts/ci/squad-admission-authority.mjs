import { readFile, realpath } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { homedir } from 'node:os';
import { dirname, isAbsolute, join, normalize, resolve } from 'node:path';

export const REQUIRED_REVIEW_SOURCES = Object.freeze([
  'code-review',
  'security-review',
  'ponytail-review',
]);
export const REVIEWER_IDENTITIES = Object.freeze({
  'code-review': Object.freeze(['smith']),
  'security-review': Object.freeze(['seraph']),
  'ponytail-review': Object.freeze(['ponytail-reviewer']),
});
export const WAIVER_ACTORS = Object.freeze(['sabbour']);

function required(value, field) {
  if (typeof value !== 'string' || value.trim() === '') throw new Error(`${field} must be a non-empty string`);
  return value.trim();
}

function samePath(left, right) {
  const normalizeCase = (value) => process.platform === 'win32' ? normalize(value).toLowerCase() : normalize(value);
  return normalizeCase(left) === normalizeCase(right);
}

async function canonicalPath(value, field, canonicalize = realpath) {
  const requested = required(value, field);
  if (!isAbsolute(requested)) throw new Error(`${field} must be absolute`);
  const absolute = resolve(requested);
  const canonical = await canonicalize(absolute);
  if (!samePath(absolute, canonical)) throw new Error(`${field} must not use a symlink, reparse point, or canonical-path substitution`);
  return canonical;
}

async function repositoryRootFromWorktree(cwd, dependencies) {
  const canonicalize = dependencies.realpath ?? realpath;
  const worktree = await canonicalPath(cwd, 'repository worktree', canonicalize);
  const result = await (dependencies.run ?? runGit)(
    ['rev-parse', '--path-format=absolute', '--git-common-dir'],
    worktree,
  );
  if (result.exitCode !== 0) throw new Error(`unable to resolve git common directory: ${result.stderr.trim()}`);
  const commonDirectory = await canonicalPath(result.stdout.trim(), 'git common directory', canonicalize);
  if (dirname(commonDirectory) === commonDirectory) throw new Error('git common directory has no repository parent');
  return canonicalPath(dirname(commonDirectory), 'repository authority root', canonicalize);
}

function platformAppData(env, platform, home) {
  if (platform === 'win32') return required(env.APPDATA, 'APPDATA');
  if (platform === 'darwin') return join(home, 'Library', 'Application Support');
  return env.XDG_CONFIG_HOME || join(home, '.config');
}

function normalizeBackend(value) {
  const backend = value ?? 'local';
  if (backend === 'worktree') return 'local';
  if (backend === 'git-notes') return 'two-layer';
  if (!['local', 'orphan', 'two-layer'].includes(backend)) {
    throw new Error(`unsupported configured state backend: ${backend}`);
  }
  return backend;
}

async function runGit(args, cwd) {
  return new Promise((resolveResult, reject) => {
    const child = spawn('git', args, { cwd, windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', (exitCode) => resolveResult({ exitCode, stdout, stderr }));
  });
}

export function requiredReviewSourcesForChanges(changedFiles) {
  if (!Array.isArray(changedFiles) || changedFiles.length === 0) {
    throw new Error('changed files are required to resolve reviewer policy');
  }
  const file = changedFiles[0]?.replaceAll('\\', '/');
  const singleLowRiskDocument = changedFiles.length === 1
    && file.endsWith('.md')
    && (file === 'README.md' || file.startsWith('docs/') || file.startsWith('.changeset/'));
  return singleLowRiskDocument ? ['code-review'] : [...REQUIRED_REVIEW_SOURCES];
}

export async function resolveAdmissionReviewPolicy({
  cwd = process.cwd(),
  headSha,
  baseSha,
} = {}, dependencies = {}) {
  if (!/^[0-9a-f]{40}$/iu.test(required(headSha, 'candidate SHA'))) {
    throw new Error('candidate SHA must be a 40-character SHA');
  }
  if (!/^[0-9a-f]{40}$/iu.test(required(baseSha, 'trusted base SHA'))) {
    throw new Error('trusted base SHA must be a 40-character SHA');
  }
  const run = dependencies.run ?? runGit;
  const ancestry = await run(['merge-base', '--is-ancestor', baseSha, headSha], cwd);
  if (ancestry.exitCode !== 0) throw new Error('trusted base SHA must be an ancestor of the candidate SHA');
  const result = await run([
    'diff',
    '--name-status',
    '-z',
    '-M',
    '-C',
    '--find-copies-harder',
    `${baseSha}...${headSha}`,
  ], cwd);
  if (result.exitCode !== 0) throw new Error(`unable to resolve admission review policy: ${result.stderr.trim()}`);
  const tokens = result.stdout.split('\0').filter(Boolean);
  const changedFiles = [];
  for (let index = 0; index < tokens.length;) {
    const status = tokens[index++];
    const paths = /^[RC]/u.test(status) ? 2 : 1;
    changedFiles.push(...tokens.slice(index, index + paths));
    index += paths;
  }
  return requiredReviewSourcesForChanges(changedFiles);
}

export async function resolveAdmissionAuthority({
  cwd = process.cwd(),
  env = process.env,
  platform = process.platform,
  home = homedir(),
} = {}, dependencies = {}) {
  const read = dependencies.readFile ?? readFile;
  const canonicalize = dependencies.realpath ?? realpath;
  const repositoryRoot = await repositoryRootFromWorktree(cwd, dependencies);
  const repositoryConfigPath = join(repositoryRoot, '.squad', 'config.json');
  const repositoryConfig = JSON.parse(await read(repositoryConfigPath, 'utf8'));

  let configuredRoot;
  if (repositoryConfig.stateLocation === 'external') {
    const projectKey = required(repositoryConfig.projectKey, 'configured Squad project key');
    configuredRoot = join(platformAppData(env, platform, home), 'squad', 'projects', projectKey);
  } else if (repositoryConfig.teamRoot !== undefined) {
    configuredRoot = required(repositoryConfig.teamRoot, 'configured team root');
    if (!isAbsolute(configuredRoot)) configuredRoot = resolve(repositoryRoot, configuredRoot);
  } else {
    configuredRoot = join(repositoryRoot, '.squad');
  }

  const teamRoot = await canonicalPath(configuredRoot, 'configured team root', canonicalize);
  const teamConfig = JSON.parse(await read(join(teamRoot, '.squad', 'config.json'), 'utf8'));
  return {
    teamRoot,
    stateBackend: normalizeBackend(teamConfig.stateBackend),
  };
}

export async function assertAdmissionAuthority(authority, {
  teamRoot,
  stateBackend,
} = {}, dependencies = {}) {
  if (!authority || typeof authority !== 'object') throw new Error('configured admission authority is required');
  const canonicalize = dependencies.realpath ?? realpath;
  const configuredRoot = await canonicalPath(authority.teamRoot, 'configured team root', canonicalize);
  const requestedRoot = await canonicalPath(teamRoot, 'requested team root', canonicalize);
  if (!samePath(requestedRoot, configuredRoot)) throw new Error('requested team root does not match configured authority');
  const configuredBackend = normalizeBackend(authority.stateBackend);
  if (normalizeBackend(stateBackend) !== configuredBackend) throw new Error('requested state backend does not match configured authority');
  return {
    teamRoot: configuredRoot,
    stateBackend: configuredBackend,
  };
}
