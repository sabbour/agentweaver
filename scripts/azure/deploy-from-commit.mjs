// deploy-from-commit.mjs -- Deploy an arbitrary committed git ref without
// switching or modifying the caller's working tree.

import fs from "node:fs";
import path from "node:path";
import * as execDefault from "./lib/exec.mjs";
import * as gitDefault from "./lib/git.mjs";
import * as logDefault from "./lib/log.mjs";
import * as kubectlDefault from "./lib/kubectl.mjs";
import { resolveVariables, DEFAULT_REPO_ROOT } from "./variables.mjs";
import { deployCommittedSha } from "./deploy-from-local.mjs";

export class CommitResolutionError extends Error {}

export function parseArgs(argv = []) {
  let ref;
  let help = false;

  for (const arg of argv) {
    if (["-h", "--help", "help"].includes(arg)) {
      help = true;
    } else if (!ref && !arg.startsWith("-")) {
      ref = arg;
    } else {
      throw new Error(`Unknown argument: ${arg}. Expected a single git SHA or ref.`);
    }
  }

  if (!help && !ref) {
    throw new Error("Usage: azure:deploy-from-commit -- <sha-or-ref>");
  }
  return { ref, help };
}

export const HELP_TEXT = `deploy-from-commit -- deploy an exact committed git ref

Usage:
  node scripts/azure/cli.mjs deploy-from-commit <sha-or-ref>

Fetches and resolves the ref to an exact commit, creates a temporary detached
worktree for that source, and runs the same SHA build/deploy/provenance/warm-
pool pipeline as deploy-from-local. The caller's checkout is never switched or
modified. Uncommitted local changes are never included.
`;

export async function resolveCommitRef(ref, { repoRoot, exec = execDefault, git = gitDefault } = {}) {
  await exec.capture("git", ["fetch", "origin", "--prune", "--tags"], {
    cwd: repoRoot,
    allowFailure: true,
  });

  let commit = await git.revParseCommit(ref, { cwd: repoRoot, capture: exec.capture });
  if (!commit) {
    const fetched = await exec.capture("git", ["fetch", "origin", ref], {
      cwd: repoRoot,
      allowFailure: true,
    });
    if (fetched.code === 0) {
      commit = await git.revParseCommit("FETCH_HEAD", {
        cwd: repoRoot,
        capture: exec.capture,
      });
    }
  }

  if (!commit) {
    throw new CommitResolutionError(`Could not resolve '${ref}' to an exact committed git SHA.`);
  }
  return commit;
}

export async function repositoryWorktreeRoot(repoRoot, { exec = execDefault } = {}) {
  const result = await exec.capture("git", ["rev-parse", "--git-common-dir"], {
    cwd: repoRoot,
    allowFailure: true,
  });
  if (result.code !== 0 || !result.stdout.trim()) {
    throw new Error("Could not locate the repository's shared git directory.");
  }
  const commonDir = path.isAbsolute(result.stdout.trim())
    ? result.stdout.trim()
    : path.resolve(repoRoot, result.stdout.trim());
  return path.dirname(commonDir);
}

export async function run(opts = {}) {
  const {
    argv = [],
    repoRoot = DEFAULT_REPO_ROOT,
    exec = execDefault,
    git = gitDefault,
    log = logDefault,
    kubectl = kubectlDefault,
    resolveVariables: resolveVariablesFn = resolveVariables,
    deployCommittedSha: deployCommittedShaFn = deployCommittedSha,
    fsImpl = fs,
  } = opts;
  const { ref, help } = parseArgs(argv);
  if (help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  const commit = await resolveCommitRef(ref, { repoRoot, exec, git });
  const imageTag = commit.slice(0, 7);
  const worktreeRoot = await repositoryWorktreeRoot(repoRoot, { exec });
  const worktreesDir = path.join(worktreeRoot, ".worktrees");
  const worktreePath = path.join(
    worktreesDir,
    `deploy-from-commit-${imageTag}-${process.pid}`,
  );
  fsImpl.mkdirSync(worktreesDir, { recursive: true });

  log.field("Requested ref", ref);
  log.field("Resolved commit", commit);
  log.field("Deployment tag", imageTag);

  await exec.run("git", ["worktree", "add", "--detach", worktreePath, commit], {
    cwd: repoRoot,
  });

  let result;
  let cleanupError;
  try {
    const env = {
      ...process.env,
      IMAGE_TAG: imageTag,
      AGENTHOST_IMAGE_TAG: imageTag,
      TARGET_GIT_REF: commit,
    };
    const cfg = {
      ...(await resolveVariablesFn({ env, repoRoot: worktreePath })),
      TARGET_GIT_REF: commit,
      repoRoot: worktreePath,
    };
    result = await deployCommittedShaFn(cfg, {
      imageTag,
      verifyGitRef: commit,
      exec,
      log,
      git,
      kubectl,
      cwd: worktreePath,
      sectionTitle: `Agentweaver commit deployment: ${ref} → ${imageTag}`,
      summaryTitle: "COMMIT DEPLOYMENT SUMMARY",
      retryLabel: "commit deployment",
    });
  } finally {
    const removed = await exec.capture(
      "git",
      ["worktree", "remove", "--force", worktreePath],
      { cwd: repoRoot, allowFailure: true },
    );
    if (removed.code !== 0) {
      cleanupError = removed.stderr || removed.stdout || "unknown git worktree removal failure";
      log.warn(`Could not remove temporary deployment worktree '${worktreePath}': ${cleanupError}`);
    } else {
      await exec.capture("git", ["worktree", "prune"], {
        cwd: repoRoot,
        allowFailure: true,
      });
    }
  }

  return {
    ...result,
    requestedRef: ref,
    commit,
    worktreePath,
    worktreeRemoved: !cleanupError,
  };
}
