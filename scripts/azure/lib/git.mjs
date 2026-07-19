// git.mjs -- Thin, side-effect-free git query helpers over exec.mjs's
// capture(), shared by steps/20-build-push-images.mjs and
// steps/25-verify-image-provenance.mjs so the "resolve a release tag's
// source commit" / "did watched paths change" logic is written exactly
// once (see image-spec.mjs for the corresponding single watched-path/
// build-arg source of truth).
//
// Every export here is a pure query (no mutation of repo state) and accepts
// an injectable `{ capture }` for tests, matching the az.mjs/variables.mjs
// convention already established in this directory.

import { capture as defaultCapture } from "./exec.mjs";

/**
 * Resolves `ref` to a full commit SHA, or null if it does not resolve to a
 * commit in this repository (mirrors `git rev-parse --verify <ref>^{commit}`).
 */
export async function revParseCommit(ref, { cwd, capture = defaultCapture } = {}) {
  try {
    const { stdout } = await capture("git", ["rev-parse", "--verify", `${ref}^{commit}`], { cwd });
    return stdout.trim() || null;
  } catch {
    return null;
  }
}

/** Resolves HEAD to a full commit SHA. */
export async function revParseHead({ cwd, capture = defaultCapture } = {}) {
  return revParseCommit("HEAD", { cwd, capture });
}

/** True if the local clone is shallow (mirrors `git rev-parse --is-shallow-repository`). */
export async function isShallowRepository({ cwd, capture = defaultCapture } = {}) {
  try {
    const { stdout } = await capture("git", ["rev-parse", "--is-shallow-repository"], { cwd, allowFailure: true });
    return stdout.trim() === "true";
  } catch {
    return false;
  }
}

/**
 * Lists every commit (newest first, across all refs) that touched `filePath`,
 * as full SHAs. Mirrors `git log --format=%H --all -- <filePath>`.
 */
export async function logAllCommitsForPath(filePath, { cwd, capture = defaultCapture } = {}) {
  try {
    const { stdout } = await capture("git", ["log", "--format=%H", "--all", "--", filePath], { cwd });
    return stdout.split("\n").map((l) => l.trim()).filter(Boolean);
  } catch {
    return [];
  }
}

/** Returns the content of `filePath` as it existed at `commit`, or null if unavailable. */
export async function showFileAtCommit(commit, filePath, { cwd, capture = defaultCapture } = {}) {
  try {
    const { stdout } = await capture("git", ["show", `${commit}:${filePath}`], { cwd, allowFailure: true });
    return stdout;
  } catch {
    return null;
  }
}

/** True if `ancestor` is an ancestor of (or equal to) `descendant`. */
export async function isAncestor(ancestor, descendant, { cwd, capture = defaultCapture } = {}) {
  try {
    const { code } = await capture("git", ["merge-base", "--is-ancestor", ancestor, descendant], {
      cwd,
      allowFailure: true,
    });
    return code === 0;
  } catch {
    return false;
  }
}

/**
 * True if there is NO diff between `refA` and `refB` restricted to `paths`
 * (mirrors `git diff --quiet <refA> <refB> -- <paths...>`, exit 0 == quiet/no
 * diff). Returns true (no diff / "unchanged") when either ref is empty, to
 * match the legacy scripts' `paths_changed()` guard -- callers decide what an
 * empty ref means for THEIR control flow, this helper only answers "is there
 * a diff between these two resolvable refs".
 */
export async function diffIsQuiet(refA, refB, paths, { cwd, capture = defaultCapture } = {}) {
  if (!refA || !refB) return true;
  try {
    const { code } = await capture("git", ["diff", "--quiet", refA, refB, "--", ...paths], {
      cwd,
      allowFailure: true,
    });
    return code === 0;
  } catch {
    return true;
  }
}

/**
 * Resolves a `commitish` (full SHA, short SHA, or any git-resolvable ref) to
 * a full commit SHA. If it does not resolve directly, falls back to matching
 * it as a prefix against `git log --all --format=%H` (mirrors
 * `resolve_provenance_commit()` in 25-verify-image-provenance.sh, used for
 * validating a `prov-<sha>` tag's SHA suffix against local history).
 */
export async function resolveCommitish(commitish, { cwd, capture = defaultCapture } = {}) {
  const direct = await revParseCommit(commitish, { cwd, capture });
  if (direct) return direct;
  try {
    const { stdout } = await capture("git", ["log", "--all", "--format=%H"], { cwd });
    const match = stdout.split("\n").find((sha) => sha.startsWith(commitish));
    return match ? match.trim() : null;
  } catch {
    return null;
  }
}

/** Full HEAD commit SHA plus its 7-char short form, for build-arg/label stamping. */
export async function currentGitSha({ cwd, capture = defaultCapture } = {}) {
  const full = await revParseHead({ cwd, capture });
  return { full, short: full ? full.slice(0, 7) : null };
}
