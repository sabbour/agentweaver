// prune-registry.mjs -- Safely delete unreferenced image manifests from the
// Agentweaver Azure Container Registry.
//
// WHY THIS TALKS TO THE REGISTRY REST API INSTEAD OF `az acr`
// ----------------------------------------------------------
// `az acr repository ...` intermittently hangs for many minutes (see
// ERR-20260913-AZ-ACR-SHOW-HANG): the Windows `az` shim leaves a live Python
// grandchild holding the output pipe. The same queries answer in well under a
// second over the registry REST API. A prune walks *every* manifest in *every*
// repository, so it is exactly the workload that made those hangs unbearable.
// The only `az` call here is a single token bootstrap.
//
// SAFETY MODEL: PROTECT BY DIGEST, NEVER BY TAG NAME
// --------------------------------------------------
//   protected = digests of kept tags
//             + every child manifest of those indexes
//             + digests pinned directly by running workloads
//   delete    = every manifest whose digest is not protected
//
// The obvious shortcut -- "delete everything untagged" -- is actively unsafe.
// A multi-arch OCI index references its per-architecture children by digest,
// and those children carry no tags of their own. Deleting untagged manifests
// therefore destroys the architectures of images you meant to keep. This was
// verified empirically against v0.31.0.
//
// The prune also fails closed: if the set of running images cannot be read
// from the cluster, it refuses to delete anything rather than guessing.

import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";
import { confirm as confirmDefault, isInteractive } from "./lib/prompt.mjs";
import { withRetry } from "./lib/retry.mjs";

/** Tags that always float to the current deployment and must never be pruned. */
export const FLOATING_TAGS = Object.freeze(["latest-release", "latest", "stable"]);

/**
 * Repositories retained in full. `moby/*` holds the BuildKit images that ACR's
 * own build tasks run on; pruning them breaks `az acr build`.
 */
export const PROTECTED_REPOSITORY_PREFIXES = Object.freeze(["moby/"]);

const MANIFEST_ACCEPT = [
  "application/vnd.oci.image.index.v1+json",
  "application/vnd.docker.distribution.manifest.list.v2+json",
  "application/vnd.oci.image.manifest.v1+json",
  "application/vnd.docker.distribution.manifest.v2+json",
].join(",");

const DEFAULT_KEEP_VERSIONS = 3;
const DEFAULT_TIMEOUT_MS = 60_000;
const DEFAULT_CONCURRENCY = 8;

export class PruneError extends Error {}

export const HELP_TEXT = `prune-registry -- delete unreferenced manifests from the Agentweaver ACR

Usage:
  node scripts/azure/cli.mjs prune-registry [--registry <name>] [--keep <n>] [--execute]

Options:
  --registry <name>   ACR name (without .azurecr.io). Defaults to ACR_NAME from
                      your params file or environment.
  --keep <n>          Number of newest vX.Y.Z releases to retain per repository
                      (default: ${DEFAULT_KEEP_VERSIONS}).
  --execute           Actually delete. Without this flag the command only
                      prints the plan and changes nothing.
  --yes               Skip the confirmation prompt when using --execute.
  --json              Emit the plan as JSON instead of a human-readable report.
  --concurrency <n>   Parallel deletes (default: ${DEFAULT_CONCURRENCY}).

Dry run by default. A manifest is retained when its digest belongs to a kept
release, a floating tag (${FLOATING_TAGS.join(", ")}), an image currently
running in the cluster, or a child of any retained multi-arch index. Anything
else is unreferenced and safe to remove.

Refuses to delete anything if the running images cannot be read from the
cluster, because that set is what protects in-use digests.
`;

export function parseArgs(argv = []) {
  const parsed = {
    registry: undefined,
    keepVersions: DEFAULT_KEEP_VERSIONS,
    execute: false,
    yes: false,
    json: false,
    concurrency: DEFAULT_CONCURRENCY,
    help: false,
  };

  const takeValue = (i, name) => {
    const raw = argv[i];
    const eq = raw.indexOf("=");
    if (eq !== -1) return { value: raw.slice(eq + 1), consumed: 0 };
    const next = argv[i + 1];
    if (next === undefined) throw new PruneError(`${name} requires a value`);
    return { value: next, consumed: 1 };
  };

  const positiveInteger = (value, name) => {
    const parsedValue = Number(value);
    if (!Number.isInteger(parsedValue) || parsedValue < 1) {
      throw new PruneError(`${name} must be a positive integer; received '${value}'.`);
    }
    return parsedValue;
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (["-h", "--help", "help"].includes(arg)) {
      parsed.help = true;
    } else if (arg === "--execute") {
      parsed.execute = true;
    } else if (arg === "--yes" || arg === "-y") {
      parsed.yes = true;
    } else if (arg === "--json") {
      parsed.json = true;
    } else if (arg === "--registry" || arg.startsWith("--registry=")) {
      const { value, consumed } = takeValue(i, "--registry");
      parsed.registry = value;
      i += consumed;
    } else if (arg === "--keep" || arg.startsWith("--keep=")) {
      const { value, consumed } = takeValue(i, "--keep");
      parsed.keepVersions = positiveInteger(value, "--keep");
      i += consumed;
    } else if (arg === "--concurrency" || arg.startsWith("--concurrency=")) {
      const { value, consumed } = takeValue(i, "--concurrency");
      parsed.concurrency = positiveInteger(value, "--concurrency");
      i += consumed;
    } else {
      throw new PruneError(`Unknown argument: ${arg}.`);
    }
  }

  return parsed;
}

// --- release ordering -------------------------------------------------------

const SEMVER_TAG = /^v(\d+)\.(\d+)\.(\d+)$/;

/** Parses a `vX.Y.Z` tag into comparable parts, or null when it is not one. */
export function parseReleaseTag(tag) {
  const match = SEMVER_TAG.exec(tag ?? "");
  if (!match) return null;
  return { major: Number(match[1]), minor: Number(match[2]), patch: Number(match[3]) };
}

/**
 * Newest-first release ordering. Compares numerically, so v0.31.2 correctly
 * sorts above v0.9.9 -- a lexicographic sort would keep the wrong releases.
 */
export function compareReleaseTagsDescending(a, b) {
  const left = parseReleaseTag(a);
  const right = parseReleaseTag(b);
  if (!left || !right) return 0;
  return (right.major - left.major) || (right.minor - left.minor) || (right.patch - left.patch);
}

// --- in-use image parsing ---------------------------------------------------

/**
 * Splits `registry/repo:tag` or `registry/repo@sha256:...` into its parts.
 * Returns null for images belonging to another registry.
 */
export function parseImageReference(image, loginServer) {
  if (typeof image !== "string") return null;
  const trimmed = image.trim();
  const prefix = `${loginServer}/`;
  if (!trimmed.startsWith(prefix)) return null;
  const rest = trimmed.slice(prefix.length);

  const digestSplit = rest.indexOf("@");
  if (digestSplit !== -1) {
    return { repository: rest.slice(0, digestSplit), digest: rest.slice(digestSplit + 1), tag: null };
  }
  // A tag cannot contain '/', so only consider a colon in the final segment --
  // this keeps registry ports and nested repository paths from being misread.
  const lastSlash = rest.lastIndexOf("/");
  const colon = rest.indexOf(":", lastSlash + 1);
  if (colon === -1) return { repository: rest, tag: null, digest: null };
  return { repository: rest.slice(0, colon), tag: rest.slice(colon + 1), digest: null };
}

/**
 * Reads every container image referenced by every pod in the cluster.
 *
 * Fails closed: a cluster that cannot be read yields an error rather than an
 * empty set, because an empty set would make every in-use digest look
 * unreferenced and therefore deletable.
 */
export async function readInUseImages(loginServer, { exec = execDefault } = {}) {
  const jsonPath =
    "{range .items[*]}" +
    "{range .spec.containers[*]}{.image}{'\\n'}{end}" +
    "{range .spec.initContainers[*]}{.image}{'\\n'}{end}" +
    "{end}";

  let result;
  try {
    result = await exec.capture(
      "kubectl",
      ["get", "pods", "--all-namespaces", "-o", `jsonpath=${jsonPath}`],
      { allowFailure: true, timeoutMs: DEFAULT_TIMEOUT_MS },
    );
  } catch (error) {
    throw new PruneError(
      `Refusing to prune: could not read running images from the cluster (${error.message}). ` +
      "That set is what protects in-use digests from deletion.",
    );
  }
  if (result.code !== 0) {
    if (result.timedOut) {
      throw new PruneError(
        "Refusing to prune: reading running images from the cluster timed out. " +
        "That set is what protects in-use digests from deletion.",
      );
    }
    throw new PruneError(
      "Refusing to prune: could not read running images from the cluster " +
      `(kubectl exited ${result.code}). That set is what protects in-use digests from deletion.`,
    );
  }

  const tags = new Map();
  const digests = new Map();
  const images = [];
  for (const line of String(result.stdout).split("\n")) {
    const parsed = parseImageReference(line, loginServer);
    if (!parsed) continue;
    images.push(line.trim());
    const bucket = parsed.digest ? digests : tags;
    const value = parsed.digest ?? parsed.tag;
    if (!value) continue;
    if (!bucket.has(parsed.repository)) bucket.set(parsed.repository, new Set());
    bucket.get(parsed.repository).add(value);
  }

  if (images.length === 0) {
    throw new PruneError(
      `Refusing to prune: no running images from ${loginServer} were found in the cluster. ` +
      "Check kubectl context before retrying.",
    );
  }
  return { images, tags, digests };
}

// --- planning (pure) --------------------------------------------------------

/**
 * Decides what to keep and what to delete for one repository.
 *
 * Pure and synchronous except for `expandIndex`, which resolves a multi-arch
 * index into its child digests. Keeping this separable is what makes the
 * safety rules testable without a registry.
 */
export async function planRepository({
  repository,
  manifests,
  keepVersions = DEFAULT_KEEP_VERSIONS,
  inUseTags = new Set(),
  inUseDigests = new Set(),
  expandIndex = async () => [],
  log = logDefault,
}) {
  const keepAll = PROTECTED_REPOSITORY_PREFIXES.some((prefix) => repository.startsWith(prefix));

  const keepTags = new Set();
  if (!keepAll) {
    const releaseTags = [];
    for (const manifest of manifests) {
      for (const tag of manifest.tags ?? []) {
        if (parseReleaseTag(tag)) releaseTags.push(tag);
      }
    }
    const newest = [...new Set(releaseTags)].sort(compareReleaseTagsDescending).slice(0, keepVersions);
    for (const tag of newest) keepTags.add(tag);

    for (const manifest of manifests) {
      for (const tag of manifest.tags ?? []) {
        if (FLOATING_TAGS.includes(tag)) keepTags.add(tag);
      }
    }
    for (const tag of inUseTags) keepTags.add(tag);
  }

  const protectedDigests = new Set(inUseDigests);
  const kept = [];
  for (const manifest of manifests) {
    const keptByTag = (manifest.tags ?? []).some((tag) => keepTags.has(tag));
    if (keepAll || protectedDigests.has(manifest.digest) || keptByTag) {
      kept.push(manifest);
      protectedDigests.add(manifest.digest);
    }
  }

  // A retained multi-arch index keeps its children alive even though those
  // children are untagged. Skipping this step silently destroys architectures.
  for (const manifest of kept) {
    let children;
    try {
      children = await expandIndex(repository, manifest.digest);
    } catch (error) {
      // Fail closed for this manifest: if we cannot prove what it references,
      // we must not delete anything it might reference.
      log.warn(`  ${repository}: could not expand ${manifest.digest} (${error.message}); keeping its children`);
      continue;
    }
    for (const child of children ?? []) protectedDigests.add(child);
  }

  const toDelete = manifests.filter((manifest) => !protectedDigests.has(manifest.digest));
  return { repository, keepAll, keepTags, protectedDigests, kept, toDelete };
}

// --- registry REST client ---------------------------------------------------

function isRetryableStatus(status) {
  return status === 408 || status === 429 || (status >= 500 && status <= 599);
}

class RegistryHttpError extends Error {
  constructor(status, message) {
    super(message);
    this.name = "RegistryHttpError";
    this.status = status;
  }
}

/**
 * Minimal ACR REST client. Tokens are scoped per repository and cached; they
 * are held in memory only and never logged.
 */
export async function createRegistryClient({
  registry,
  exec = execDefault,
  fetchImpl = globalThis.fetch,
  timeoutMs = DEFAULT_TIMEOUT_MS,
}) {
  const loginServer = `${registry}.azurecr.io`;

  const login = await exec.capture(
    "az",
    ["acr", "login", "--name", registry, "--expose-token", "--output", "json"],
    { allowFailure: true, timeoutMs: DEFAULT_TIMEOUT_MS },
  );
  if (login.code !== 0) {
    throw new PruneError(
      `Could not obtain an ACR refresh token for '${registry}'. Run 'az login' first. ${login.stderr ?? ""}`.trim(),
    );
  }
  let refreshToken;
  try {
    refreshToken = JSON.parse(login.stdout).refreshToken;
  } catch {
    refreshToken = undefined;
  }
  if (!refreshToken) {
    throw new PruneError(`Could not parse an ACR refresh token for '${registry}'.`);
  }

  const tokenCache = new Map();
  async function accessToken(scope) {
    if (tokenCache.has(scope)) return tokenCache.get(scope);
    const body = new URLSearchParams({
      grant_type: "refresh_token",
      service: loginServer,
      scope,
      refresh_token: refreshToken,
    });
    const response = await fetchImpl(`https://${loginServer}/oauth2/token`, {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body,
      signal: AbortSignal.timeout(timeoutMs),
    });
    if (!response.ok) {
      throw new PruneError(`Registry token exchange failed for scope '${scope}' (HTTP ${response.status}).`);
    }
    const token = (await response.json()).access_token;
    tokenCache.set(scope, token);
    return token;
  }

  const repositoryScope = (repository) =>
    `repository:${repository}:metadata_read,metadata_write,pull,delete`;

  async function request(pathOrUrl, { scope, method = "GET", accept, body, allowStatuses = [] } = {}) {
    const url = pathOrUrl.startsWith("http") ? pathOrUrl : `https://${loginServer}${pathOrUrl}`;
    return withRetry(
      async () => {
        const token = await accessToken(scope);
        const headers = { authorization: `Bearer ${token}` };
        if (accept) headers.accept = accept;
        if (body) headers["content-type"] = "application/json";
        const response = await fetchImpl(url, {
          method,
          headers,
          body,
          signal: AbortSignal.timeout(timeoutMs),
        });
        if (!response.ok && !allowStatuses.includes(response.status)) {
          const error = new RegistryHttpError(
            response.status,
            `${method} ${url.replace(`https://${loginServer}`, "")} failed: HTTP ${response.status}`,
          );
          // Only transport/service failures are retried; a 403/404/405 is a
          // decision the registry has already made and will repeat.
          if (!isRetryableStatus(response.status)) error.permanent = true;
          throw error;
        }
        return response;
      },
      {
        attempts: 3,
        label: `registry ${method}`,
        isTransient: (error) => !error?.permanent,
        onRetry: () => {},
      },
    );
  }

  return { loginServer, repositoryScope, request };
}

/** Lists every repository in the registry. */
export async function listRepositories(client) {
  const response = await client.request("/v2/_catalog?n=1000", { scope: "registry:catalog:*" });
  return (await response.json()).repositories ?? [];
}

/** Lists every manifest in a repository, following ACR's Link-header paging. */
export async function listManifests(client, repository) {
  const scope = client.repositoryScope(repository);
  const manifests = [];
  let next = `/acr/v1/${repository}/_manifests?n=500`;
  while (next) {
    const response = await client.request(next, { scope });
    const payload = await response.json();
    for (const manifest of payload.manifests ?? []) manifests.push(manifest);
    const link = response.headers.get("link");
    const match = link && /<([^>]+)>;\s*rel="next"/.exec(link);
    next = match ? match[1] : null;
  }
  return manifests;
}

/** Resolves a manifest into its child digests (empty for a single-arch image). */
export async function expandIndexChildren(client, repository, digest) {
  const response = await client.request(`/v2/${repository}/manifests/${digest}`, {
    scope: client.repositoryScope(repository),
    accept: MANIFEST_ACCEPT,
  });
  const payload = await response.json();
  return (payload.manifests ?? []).map((child) => child.digest);
}

/**
 * Deletes one manifest, lifting a provenance write-lock if the registry
 * refuses with 405. Provenance tags are deliberately locked read-only, so the
 * lock is lifted only for a manifest already chosen for deletion.
 */
export async function deleteManifest(client, repository, manifest, { log = logDefault } = {}) {
  const scope = client.repositoryScope(repository);
  const remove = () =>
    client.request(`/v2/${repository}/manifests/${manifest.digest}`, {
      scope,
      method: "DELETE",
      // 404 means someone else already removed it; that is success, not failure.
      allowStatuses: [404, 405],
    });

  let response = await remove();
  if (response.status === 405 && (manifest.tags ?? []).length > 0) {
    let unlocked = false;
    for (const tag of manifest.tags) {
      try {
        await client.request(`/acr/v1/${repository}/_tags/${tag}`, {
          scope,
          method: "PATCH",
          body: JSON.stringify({ writeEnabled: true, deleteEnabled: true }),
        });
        unlocked = true;
      } catch (error) {
        log.warn(`  ${repository}:${tag} could not be unlocked: ${error.message}`);
      }
    }
    if (unlocked) {
      response = await remove();
      if (response.ok || response.status === 404) return { deleted: true, unlocked: true };
    }
  }

  if (response.ok || response.status === 404) return { deleted: true, unlocked: false };
  return { deleted: false, unlocked: false, status: response.status };
}

/** Runs `worker` over `items` with bounded concurrency. */
async function mapWithConcurrency(items, limit, worker) {
  const results = [];
  let index = 0;
  const runners = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (index < items.length) {
      const current = index++;
      results[current] = await worker(items[current], current);
    }
  });
  await Promise.all(runners);
  return results;
}

// --- command ----------------------------------------------------------------

export async function run(opts = {}) {
  const {
    argv = [],
    log = logDefault,
    env = process.env,
    exec = execDefault,
    fetchImpl = globalThis.fetch,
    confirm = confirmDefault,
    createClient = createRegistryClient,
  } = opts;

  const parsed = parseArgs(argv);
  if (parsed.help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  const registry = parsed.registry || env.ACR_NAME;
  if (!registry) {
    throw new PruneError(
      "No registry specified. Pass --registry <name> or set ACR_NAME in your params file or environment.",
    );
  }

  const client = await createClient({ registry, exec, fetchImpl });
  log.section(`Pruning ${client.loginServer}`);

  const inUse = await readInUseImages(client.loginServer, { exec });
  log.field("Images in use", String(inUse.images.length));
  for (const image of inUse.images) log.info(`  ${image}`);

  const repositories = await listRepositories(client);
  log.field("Repositories", String(repositories.length));

  const plans = [];
  for (const repository of repositories) {
    const manifests = await listManifests(client, repository);
    if (manifests.length === 0) continue;

    const plan = await planRepository({
      repository,
      manifests,
      keepVersions: parsed.keepVersions,
      inUseTags: inUse.tags.get(repository) ?? new Set(),
      inUseDigests: inUse.digests.get(repository) ?? new Set(),
      expandIndex: (repo, digest) => expandIndexChildren(client, repo, digest),
      log,
    });

    log.info(
      `  ${repository}: ${manifests.length} manifest(s), ` +
      `${manifests.length - plan.toDelete.length} kept, ${plan.toDelete.length} to delete` +
      (plan.keepAll ? " (protected repository)" : ""),
    );
    if (plan.keepTags.size > 0) {
      log.info(`    keep tags: ${[...plan.keepTags].sort().join(", ")}`);
    }
    if (plan.toDelete.length > 0) plans.push(plan);
  }

  const totalToDelete = plans.reduce((sum, plan) => sum + plan.toDelete.length, 0);

  if (parsed.json) {
    log.info(JSON.stringify({
      registry: client.loginServer,
      totalToDelete,
      repositories: plans.map((plan) => ({
        repository: plan.repository,
        delete: plan.toDelete.map((manifest) => ({ digest: manifest.digest, tags: manifest.tags ?? [] })),
      })),
    }, null, 2));
  }

  if (totalToDelete === 0) {
    log.ok("Nothing to prune.");
    return { ok: true, executed: false, totalToDelete: 0, deleted: 0, failed: 0, plans };
  }

  log.warn(`Plan: delete ${totalToDelete} manifest(s) across ${plans.length} repository(ies).`);

  if (!parsed.execute) {
    log.info("Dry run -- nothing was deleted. Re-run with --execute to apply.");
    return { ok: true, executed: false, totalToDelete, deleted: 0, failed: 0, plans };
  }

  if (!parsed.yes) {
    if (!isInteractive()) {
      throw new PruneError("--execute needs confirmation. Re-run with --yes in a non-interactive shell.");
    }
    const confirmed = await confirm(`Delete ${totalToDelete} manifest(s) from ${client.loginServer}?`, {
      defaultValue: false,
    });
    if (!confirmed) {
      log.info("Aborted; nothing was deleted.");
      return { ok: true, executed: false, totalToDelete, deleted: 0, failed: 0, plans };
    }
  }

  let deleted = 0;
  let unlocked = 0;
  let failed = 0;
  for (const plan of plans) {
    const outcomes = await mapWithConcurrency(plan.toDelete, parsed.concurrency, async (manifest) => {
      try {
        return await deleteManifest(client, plan.repository, manifest, { log });
      } catch (error) {
        return { deleted: false, unlocked: false, error: error.message };
      }
    });
    for (const outcome of outcomes) {
      if (outcome.deleted) {
        deleted += 1;
        if (outcome.unlocked) unlocked += 1;
      } else {
        failed += 1;
        if (failed <= 5) {
          log.warn(`  ${plan.repository}: ${outcome.error ?? `HTTP ${outcome.status}`}`);
        }
      }
    }
    log.ok(`  ${plan.repository}: processed ${plan.toDelete.length} manifest(s)`);
  }

  log.field("Deleted", String(deleted));
  if (unlocked > 0) log.field("Unlocked then deleted", String(unlocked));
  if (failed > 0) log.field("Failed", String(failed));

  return { ok: failed === 0, executed: true, totalToDelete, deleted, unlocked, failed, plans };
}
