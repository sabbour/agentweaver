// prune-registry.test.mjs -- The prune deletes real, irrecoverable artifacts,
// so its safety rules carry the tests: protect by digest, expand multi-arch
// indexes, and fail closed whenever the protected set cannot be established.

import test from "node:test";
import assert from "node:assert/strict";
import {
  compareReleaseTagsDescending,
  createRegistryClient,
  deleteManifest,
  listManifests,
  parseArgs,
  parseImageReference,
  parseReleaseTag,
  planRepository,
  PruneError,
  readInUseImages,
  run,
} from "../prune-registry.mjs";

const log = {
  info() {}, section() {}, field() {}, ok() {}, skip() {}, warn() {},
  error() {}, debug() {}, command() {},
};

const manifest = (digest, tags = []) => ({ digest, tags });

// --- argument parsing -------------------------------------------------------

test("parseArgs defaults to a dry run", () => {
  const parsed = parseArgs([]);
  assert.equal(parsed.execute, false, "prune must never delete unless --execute is explicit");
  assert.equal(parsed.keepVersions, 3);
  assert.equal(parsed.yes, false);
});

test("parseArgs accepts both flag spellings and rejects bad input", () => {
  assert.equal(parseArgs(["--registry", "acr"]).registry, "acr");
  assert.equal(parseArgs(["--registry=acr"]).registry, "acr");
  assert.equal(parseArgs(["--keep=5"]).keepVersions, 5);
  assert.equal(parseArgs(["--execute", "--yes"]).execute, true);
  assert.throws(() => parseArgs(["--keep", "0"]), /positive integer/);
  assert.throws(() => parseArgs(["--keep", "two"]), /positive integer/);
  assert.throws(() => parseArgs(["--registry"]), /requires a value/);
  assert.throws(() => parseArgs(["--bogus"]), /Unknown argument/);
});

// --- release ordering -------------------------------------------------------

test("release tags sort numerically, not lexicographically", () => {
  assert.deepEqual(parseReleaseTag("v0.31.2"), { major: 0, minor: 31, patch: 2 });
  assert.equal(parseReleaseTag("latest-release"), null);
  assert.equal(parseReleaseTag("v1.2"), null);

  const sorted = ["v0.9.9", "v0.31.2", "v0.10.0", "v1.0.0"].sort(compareReleaseTagsDescending);
  assert.deepEqual(sorted, ["v1.0.0", "v0.31.2", "v0.10.0", "v0.9.9"]);
});

// --- image reference parsing ------------------------------------------------

test("parseImageReference separates tags, digests, and other registries", () => {
  const server = "acr.azurecr.io";
  assert.deepEqual(parseImageReference("acr.azurecr.io/api:v1.2.3", server), {
    repository: "api", tag: "v1.2.3", digest: null,
  });
  assert.deepEqual(parseImageReference(`acr.azurecr.io/api@sha256:${"a".repeat(64)}`, server), {
    repository: "api", digest: `sha256:${"a".repeat(64)}`, tag: null,
  });
  // Nested repository paths must not be mistaken for a tag.
  assert.deepEqual(parseImageReference("acr.azurecr.io/moby/buildkit:v1", server), {
    repository: "moby/buildkit", tag: "v1", digest: null,
  });
  assert.equal(parseImageReference("docker.io/library/nginx:1", server), null);
  assert.equal(parseImageReference(undefined, server), null);
});

// --- planning safety --------------------------------------------------------

test("keeps the newest releases, floating tags, and in-use images", async () => {
  const manifests = [
    manifest("sha256:v33", ["v0.33.0"]),
    manifest("sha256:v32", ["v0.32.0"]),
    manifest("sha256:v31", ["v0.31.2", "latest-release"]),
    manifest("sha256:v30", ["v0.30.0"]),
    manifest("sha256:v29", ["v0.29.0"]),
    manifest("sha256:old", ["v0.1.0"]),
  ];

  const plan = await planRepository({ repository: "api", manifests, keepVersions: 3, log });

  const deleted = plan.toDelete.map((item) => item.digest).sort();
  assert.deepEqual(deleted, ["sha256:old", "sha256:v29", "sha256:v30"]);
  assert.ok(plan.keepTags.has("latest-release"), "a floating tag must always be kept");
});

test("an in-use digest is protected even when nothing tags it", async () => {
  const manifests = [
    manifest("sha256:current"),
    manifest("sha256:newest", ["v9.9.9"]),
    manifest("sha256:orphan"),
  ];

  const plan = await planRepository({
    repository: "api",
    manifests,
    keepVersions: 1,
    inUseDigests: new Set(["sha256:current"]),
    log,
  });

  assert.deepEqual(plan.toDelete.map((item) => item.digest), ["sha256:orphan"]);
});

test("an in-use tag outside the keep window is still protected", async () => {
  const manifests = [
    manifest("sha256:new", ["v2.0.0"]),
    manifest("sha256:running", ["v1.0.0"]),
  ];

  const plan = await planRepository({
    repository: "api",
    manifests,
    keepVersions: 1,
    inUseTags: new Set(["v1.0.0"]),
    log,
  });

  assert.deepEqual(plan.toDelete, [], "a release still running must never be pruned");
});

test("children of a retained multi-arch index are protected", async () => {
  // This is the case that makes "delete everything untagged" unsafe: the
  // per-architecture manifests of a kept image carry no tags of their own.
  const manifests = [
    manifest("sha256:index", ["v1.0.0"]),
    manifest("sha256:amd64"),
    manifest("sha256:arm64"),
    manifest("sha256:unrelated"),
  ];

  const plan = await planRepository({
    repository: "api",
    manifests,
    keepVersions: 1,
    expandIndex: async (_repo, digest) =>
      digest === "sha256:index" ? ["sha256:amd64", "sha256:arm64"] : [],
    log,
  });

  assert.deepEqual(plan.toDelete.map((item) => item.digest), ["sha256:unrelated"]);
});

test("a manifest that cannot be expanded keeps its children", async () => {
  const manifests = [manifest("sha256:index", ["v1.0.0"]), manifest("sha256:child")];

  const plan = await planRepository({
    repository: "api",
    manifests,
    keepVersions: 1,
    expandIndex: async () => { throw new Error("registry unreachable"); },
    log,
  });

  assert.deepEqual(
    plan.toDelete.map((item) => item.digest),
    ["sha256:child"],
    "an unexpandable index must not cause deletion of manifests it might reference",
  );
});

test("moby repositories are retained in full", async () => {
  const manifests = [manifest("sha256:a"), manifest("sha256:b", ["v0.0.1"])];
  const plan = await planRepository({ repository: "moby/buildkit", manifests, keepVersions: 1, log });
  assert.equal(plan.keepAll, true);
  assert.deepEqual(plan.toDelete, [], "BuildKit images power az acr build and must survive a prune");
});

// --- fail-closed cluster read ----------------------------------------------

function kubectlExec(result) {
  return { async capture() { return result; } };
}

test("refuses to prune when the cluster cannot be read", async () => {
  await assert.rejects(
    readInUseImages("acr.azurecr.io", { exec: kubectlExec({ code: 1, stdout: "" }) }),
    (error) => {
      assert.ok(error instanceof PruneError);
      assert.match(error.message, /Refusing to prune/);
      return true;
    },
  );

  await assert.rejects(
    readInUseImages("acr.azurecr.io", {
      exec: { async capture() { throw new Error("kubectl missing"); } },
    }),
    /Refusing to prune/,
  );
});

test("refuses to prune when no image from this registry is running", async () => {
  await assert.rejects(
    readInUseImages("acr.azurecr.io", {
      exec: kubectlExec({ code: 0, stdout: "docker.io/library/nginx:1\n" }),
    }),
    /no running images/,
  );
});

test("collects in-use tags and digests per repository", async () => {
  const stdout = [
    "acr.azurecr.io/api:v1.2.3",
    `acr.azurecr.io/agent-host@sha256:${"c".repeat(64)}`,
    "docker.io/library/nginx:1",
    "",
  ].join("\n");

  const inUse = await readInUseImages("acr.azurecr.io", { exec: kubectlExec({ code: 0, stdout }) });
  assert.deepEqual([...inUse.tags.get("api")], ["v1.2.3"]);
  assert.deepEqual([...inUse.digests.get("agent-host")], [`sha256:${"c".repeat(64)}`]);
  assert.equal(inUse.images.length, 2, "images from other registries are ignored");
});

// --- REST behaviour ---------------------------------------------------------

function fakeResponse({ status = 200, body = {}, headers = {} } = {}) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: (name) => headers[name.toLowerCase()] ?? null },
    async json() { return body; },
  };
}

function fakeClient(handler) {
  return {
    loginServer: "acr.azurecr.io",
    repositoryScope: (repository) => `repository:${repository}:metadata_read`,
    request: handler,
  };
}

test("listManifests follows Link-header pagination", async () => {
  const calls = [];
  const client = fakeClient(async (pathOrUrl) => {
    calls.push(pathOrUrl);
    if (calls.length === 1) {
      return fakeResponse({
        body: { manifests: [manifest("sha256:1")] },
        headers: { link: '</acr/v1/api/_manifests?n=500&orderby=&last=1>; rel="next"' },
      });
    }
    return fakeResponse({ body: { manifests: [manifest("sha256:2")] } });
  });

  const manifests = await listManifests(client, "api");
  assert.deepEqual(manifests.map((item) => item.digest), ["sha256:1", "sha256:2"]);
  assert.equal(calls.length, 2);
});

test("deleteManifest lifts a provenance write-lock only after a 405", async () => {
  const calls = [];
  const client = fakeClient(async (pathOrUrl, options) => {
    calls.push(`${options.method ?? "GET"} ${pathOrUrl}`);
    if (options.method === "DELETE") {
      // Locked until the tag is patched writable.
      const patched = calls.some((call) => call.startsWith("PATCH"));
      return fakeResponse({ status: patched ? 202 : 405 });
    }
    return fakeResponse({ status: 200 });
  });

  const result = await deleteManifest(client, "api", manifest("sha256:locked", ["prov-abc"]), { log });
  assert.deepEqual(result, { deleted: true, unlocked: true });
  assert.ok(calls.some((call) => call === "PATCH /acr/v1/api/_tags/prov-abc"));
});

test("deleteManifest treats an already-deleted manifest as success", async () => {
  const client = fakeClient(async () => fakeResponse({ status: 404 }));
  assert.deepEqual(
    await deleteManifest(client, "api", manifest("sha256:gone"), { log }),
    { deleted: true, unlocked: false },
  );
});

test("createRegistryClient fails clearly when az cannot mint a token", async () => {
  await assert.rejects(
    createRegistryClient({
      registry: "acr",
      exec: { async capture() { return { code: 1, stdout: "", stderr: "please run az login" }; } },
    }),
    (error) => {
      assert.ok(error instanceof PruneError);
      assert.match(error.message, /az login/);
      return true;
    },
  );
});

// --- end-to-end command -----------------------------------------------------

function stubClient({ repositories, manifests }) {
  return {
    loginServer: "acr.azurecr.io",
    repositoryScope: (repository) => `repository:${repository}:metadata_read`,
    async request(pathOrUrl) {
      if (pathOrUrl.startsWith("/v2/_catalog")) return fakeResponse({ body: { repositories } });
      const manifestList = /^\/acr\/v1\/(.+)\/_manifests/.exec(pathOrUrl);
      if (manifestList) return fakeResponse({ body: { manifests: manifests[manifestList[1]] ?? [] } });
      return fakeResponse({ body: {} });
    },
  };
}

function commandOpts(overrides = {}) {
  return {
    log,
    env: { ACR_NAME: "acr" },
    exec: kubectlExec({ code: 0, stdout: "acr.azurecr.io/api:v9.9.9\n" }),
    createClient: async () => stubClient({
      repositories: ["api"],
      manifests: { api: [manifest("sha256:keep", ["v9.9.9"]), manifest("sha256:stale", ["v0.0.1"])] },
    }),
    ...overrides,
  };
}

test("a dry run reports the plan and deletes nothing", async () => {
  const result = await run(commandOpts({ argv: ["--keep=1"] }));
  assert.equal(result.executed, false);
  assert.equal(result.totalToDelete, 1);
  assert.equal(result.deleted, 0);
});

test("nothing is pruned when every manifest falls inside the keep window", async () => {
  const result = await run(commandOpts({ argv: ["--keep=3"] }));
  assert.equal(result.totalToDelete, 0);
  assert.equal(result.deleted, 0);
});

test("--execute without --yes refuses in a non-interactive shell", async () => {
  await assert.rejects(
    run(commandOpts({ argv: ["--execute", "--keep=1"], confirm: async () => true })),
    /--yes/,
  );
});

test("--execute --yes deletes exactly the planned manifests", async () => {
  const deletedDigests = [];
  const client = stubClient({
    repositories: ["api"],
    manifests: { api: [manifest("sha256:keep", ["v9.9.9"]), manifest("sha256:stale", ["v0.0.1"])] },
  });
  const baseRequest = client.request.bind(client);
  client.request = async (pathOrUrl, options = {}) => {
    if (options.method === "DELETE") {
      deletedDigests.push(pathOrUrl.split("/").pop());
      return fakeResponse({ status: 202 });
    }
    return baseRequest(pathOrUrl, options);
  };

  const result = await run(commandOpts({
    argv: ["--execute", "--yes", "--keep=1"],
    createClient: async () => client,
  }));

  assert.equal(result.ok, true);
  assert.equal(result.deleted, 1);
  assert.deepEqual(deletedDigests, ["sha256:stale"], "the in-use release must not be deleted");
});

test("the command requires a registry name", async () => {
  await assert.rejects(
    run(commandOpts({ argv: [], env: {} })),
    /--registry/,
  );
});

test("--help prints usage without touching the registry", async () => {
  const result = await run({
    argv: ["--help"],
    log,
    env: {},
    createClient: async () => { throw new Error("must not connect"); },
  });
  assert.deepEqual(result, { ok: true, help: true });
});
