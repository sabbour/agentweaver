import * as execDefault from "./exec.mjs";

const GITHUB_REMOTE_RE = /^(?:https:\/\/github\.com\/|git@github\.com:)([^/]+)\/([^/]+?)(?:\.git)?$/i;

/**
 * Parses a GitHub remote URL (`https://github.com/owner/repo.git` or
 * `git@github.com:owner/repo.git`) into `{ owner, repo }`, or null when the
 * remote does not point at GitHub.
 *
 * @param {string} remoteUrl
 * @returns {{ owner: string, repo: string } | null}
 */
export function parseGitHubRemoteUrl(remoteUrl = "") {
  const match = GITHUB_REMOTE_RE.exec(String(remoteUrl).trim());
  if (!match) return null;
  return { owner: match[1], repo: match[2] };
}

/**
 * Resolves the current repository's `origin` remote to a GitHub owner/repo
 * pair, or null when `origin` is unset or not a GitHub remote.
 *
 * @param {{ repoRoot?: string, exec?: typeof execDefault }} [opts]
 * @returns {Promise<{ owner: string, repo: string } | null>}
 */
export async function resolveGitHubRepository({ repoRoot, exec = execDefault } = {}) {
  const result = await exec.capture("git", ["config", "--get", "remote.origin.url"], {
    cwd: repoRoot,
    allowFailure: true,
  });
  if (result.code !== 0) return null;
  return parseGitHubRemoteUrl(result.stdout);
}

/**
 * True when the given repository has a non-draft GitHub Release for `tag`.
 *
 * @param {string} owner
 * @param {string} repo
 * @param {string} tag
 * @param {{ fetchImpl?: typeof fetch }} [opts]
 * @returns {Promise<boolean>}
 */
export async function githubReleaseExists(owner, repo, tag, { fetchImpl = globalThis.fetch } = {}) {
  if (!owner || !repo || !tag) return false;
  if (typeof fetchImpl !== "function") {
    throw new Error("GitHub release lookup requires a fetch implementation.");
  }

  const response = await fetchImpl(
    `https://api.github.com/repos/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/releases/tags/${encodeURIComponent(tag)}`,
    {
      headers: {
        Accept: "application/vnd.github+json",
        "User-Agent": "agentweaver-azure-toolchain",
      },
    },
  );

  if (response.status === 404) return false;
  if (!response.ok) {
    throw new Error(
      `GitHub release lookup failed for ${owner}/${repo}@${tag} (${response.status} ${response.statusText || "request failed"}).`,
    );
  }

  const release = await response.json();
  return release?.tag_name === tag && release?.draft !== true;
}
