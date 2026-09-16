import * as execDefault from "./exec.mjs";

const REGISTRY_USERNAME = "00000000-0000-0000-0000-000000000000";
const MANIFEST_ACCEPT = [
  "application/vnd.oci.image.manifest.v1+json",
  "application/vnd.oci.image.index.v1+json",
  "application/vnd.docker.distribution.manifest.v2+json",
  "application/vnd.docker.distribution.manifest.list.v2+json",
].join(", ");
const DEFAULT_HTTP_TIMEOUT_MS = 15_000;
const DEFAULT_LOGIN_TIMEOUT_MS = 90_000;

function encodeRepository(repository) {
  return repository.split("/").map(encodeURIComponent).join("/");
}

/**
 * Creates a deployment-scoped ACR data-plane client. Azure CLI is invoked once
 * to obtain a short-lived registry refresh token; exact tag lookups then use
 * the registry API directly and never enumerate repository manifests.
 */
export function createAcrManifestClient(
  registry,
  {
    exec = execDefault,
    fetchImpl = globalThis.fetch,
    httpTimeoutMs = DEFAULT_HTTP_TIMEOUT_MS,
    loginTimeoutMs = DEFAULT_LOGIN_TIMEOUT_MS,
  } = {},
) {
  let loginPromise;
  const scopedTokens = new Map();

  async function login() {
    if (!loginPromise) {
      loginPromise = (async () => {
        if (exec.isDryRun?.()) {
          return { dryRun: true, loginServer: `${registry}.azurecr.io` };
        }
        const { stdout, code } = await exec.capture(
          "az",
          ["acr", "login", "--name", registry, "--expose-token", "--output", "json"],
          { allowFailure: true, timeoutMs: loginTimeoutMs },
        );
        if (code !== 0) throw new Error(`ACR token login failed for ${registry}`);
        const result = JSON.parse(stdout);
        if (!result?.accessToken || !result?.loginServer) {
          throw new Error(`ACR token login returned an incomplete response for ${registry}`);
        }
        return result;
      })();
      loginPromise.catch(() => {
        loginPromise = undefined;
      });
    }
    return loginPromise;
  }

  async function exchangeToken(repository) {
    const session = await login();
    if (session.dryRun) return { ...session, token: "dry-run" };
    const basic = Buffer.from(`${REGISTRY_USERNAME}:${session.accessToken}`).toString("base64");
    const tokenUrl = new URL(`https://${session.loginServer}/oauth2/token`);
    tokenUrl.searchParams.set("service", session.loginServer);
    tokenUrl.searchParams.set("scope", `repository:${repository}:pull`);
    const response = await fetchImpl(tokenUrl, {
      headers: { Authorization: `Basic ${basic}` },
      signal: AbortSignal.timeout(httpTimeoutMs),
    });
    if (!response.ok) throw new Error(`ACR scoped-token exchange returned HTTP ${response.status}`);
    const body = await response.json();
    const token = body.token ?? body.access_token;
    if (!token) throw new Error("ACR scoped-token exchange returned no access token");
    return { loginServer: session.loginServer, token };
  }

  async function tokenFor(repository) {
    if (!scopedTokens.has(repository)) {
      const tokenPromise = exchangeToken(repository);
      scopedTokens.set(repository, tokenPromise);
      tokenPromise.catch(() => {
        if (scopedTokens.get(repository) === tokenPromise) scopedTokens.delete(repository);
      });
    }
    return scopedTokens.get(repository);
  }

  async function requestDigest(image, tag, allowRefresh) {
    const session = await tokenFor(image);
    if (session.dryRun) return null;
    const response = await fetchImpl(
      `https://${session.loginServer}/v2/${encodeRepository(image)}/manifests/${encodeURIComponent(tag)}`,
      {
        method: "HEAD",
        headers: {
          Authorization: `Bearer ${session.token}`,
          Accept: MANIFEST_ACCEPT,
        },
        signal: AbortSignal.timeout(httpTimeoutMs),
      },
    );
    if (response.status === 404) return null;
    if ((response.status === 401 || response.status === 403) && allowRefresh) {
      scopedTokens.delete(image);
      return requestDigest(image, tag, false);
    }
    if (!response.ok) throw new Error(`ACR manifest lookup returned HTTP ${response.status}`);
    const digest = response.headers.get("docker-content-digest");
    if (!digest) throw new Error("ACR manifest lookup returned no Docker-Content-Digest header");
    return digest;
  }

  return {
    digestForTag(image, tag) {
      return requestDigest(image, tag, true);
    },
  };
}

/**
 * Resolves one exact ACR tag to its manifest digest without enumerating the
 * repository's manifests or tags.
 */
export async function manifestDigestForTag(
  registry,
  image,
  tag,
  { exec = execDefault, timeoutMs, manifestClient } = {},
) {
  if (manifestClient) return manifestClient.digestForTag(image, tag);
  try {
    const options = { allowFailure: true };
    if (timeoutMs) options.timeoutMs = timeoutMs;
    const { stdout, code } = await exec.capture(
      "az",
      [
        "acr",
        "manifest",
        "show-metadata",
        "--registry",
        registry,
        "--name",
        `${image}:${tag}`,
        "--query",
        "digest",
        "--output",
        "tsv",
      ],
      options,
    );
    if (code !== 0) return null;
    return String(stdout ?? "")
      .split(/\r?\n/)
      .map((line) => line.trim())
      .find(Boolean) ?? null;
  } catch {
    return null;
  }
}
