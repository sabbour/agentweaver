import * as execDefault from "./exec.mjs";

/**
 * Resolves one exact ACR tag to its manifest digest without enumerating the
 * repository's manifests or tags.
 */
export async function manifestDigestForTag(
  registry,
  image,
  tag,
  { exec = execDefault, timeoutMs } = {},
) {
  try {
    const options = { allowFailure: true };
    if (timeoutMs) options.timeoutMs = timeoutMs;
    const { stdout } = await exec.capture(
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
    return String(stdout ?? "")
      .split(/\r?\n/)
      .map((line) => line.trim())
      .find(Boolean) ?? null;
  } catch {
    return null;
  }
}
