// image-spec.test.mjs -- completeness + bugfix verification for image-spec.mjs,
// the single declarative source consumed by both the build (20) and
// provenance (25) steps.

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { IMAGES, COMMON_DOTNET_PATHS, getImage, buildArgsFor } from "../image-spec.mjs";

test("image-spec: exactly the 4 expected images, each with required fields", () => {
  const names = IMAGES.map((i) => i.name).sort();
  assert.deepEqual(names, [
    "agentweaver-agent-host",
    "agentweaver-api",
    "agentweaver-frontend",
    "agentweaver-mcp",
  ]);

  for (const image of IMAGES) {
    assert.ok(image.dockerfile && image.dockerfile.length > 0, `${image.name} missing dockerfile`);
    assert.ok(image.context, `${image.name} missing context`);
    assert.ok(image.tagField === "IMAGE_TAG" || image.tagField === "AGENTHOST_IMAGE_TAG", `${image.name} bad tagField`);
    assert.ok(Array.isArray(image.watchedPaths) && image.watchedPaths.length > 0, `${image.name} missing watchedPaths`);
    assert.ok(image.currentTag && image.currentTag.kind, `${image.name} missing currentTag descriptor`);
    assert.ok(image.provenance && image.provenance.podSelector, `${image.name} missing provenance descriptor`);
  }
});

test("image-spec: dockerfiles/context point at real repo-relative paths", () => {
  assert.equal(getImage("agentweaver-api").dockerfile, "apps/Agentweaver.Api/Dockerfile");
  assert.equal(getImage("agentweaver-frontend").dockerfile, "apps/web/Dockerfile");
  assert.equal(getImage("agentweaver-mcp").dockerfile, "apps/Agentweaver.Mcp/Dockerfile");
  assert.equal(getImage("agentweaver-agent-host").dockerfile, "apps/Agentweaver.AgentHost/Dockerfile");
  for (const image of IMAGES) {
    assert.equal(image.context, ".", `${image.name} should build from repo root (Dockerfiles COPY from multiple subdirs)`);
  }
});

const apiDockerfile = readFileSync(
  fileURLToPath(new URL("../../../apps/Agentweaver.Api/Dockerfile", import.meta.url)),
  "utf8",
);

test("api image maps TARGETARCH to pinned, verified Copilot CLI packages and native runtime paths", () => {
  assert.match(apiDockerfile, /ARG COPILOT_CLI_VERSION=1\.0\.57/);
  assert.match(apiDockerfile, /ARG COPILOT_NPM_REGISTRY_URL=https:\/\/registry\.npmjs\.org/);
  assert.match(apiDockerfile, /ARG COPILOT_CLI_SHA1_AMD64=7938f9f6dfc3248efc1a23e1c76bf98fa4554141/);
  assert.match(apiDockerfile, /ARG COPILOT_CLI_SHA1_ARM64=3fe206bfe1bd5a32636530e1dd3f3815cea1b336/);
  assert.match(apiDockerfile, /amd64\) runtime_arch=x64; copilot_sha1="\$\{COPILOT_CLI_SHA1_AMD64\}"/);
  assert.match(apiDockerfile, /arm64\) runtime_arch=arm64; copilot_sha1="\$\{COPILOT_CLI_SHA1_ARM64\}"/);
  assert.match(apiDockerfile, /printf '%s' "\$\{runtime_arch\}" > \/copilot-bin\/runtime-arch/);
  assert.match(apiDockerfile, /\$\{COPILOT_NPM_REGISTRY_URL\}\/@github\/copilot-linux-\$\{runtime_arch\}\/-\/copilot-linux-\$\{runtime_arch\}-\$\{COPILOT_CLI_VERSION\}\.tgz/);
  assert.match(apiDockerfile, /echo "\$\{copilot_sha1\}  \/tmp\/copilot\.tgz" \| sha1sum -c - && \\\s+tar -xzf/);
  assert.equal((apiDockerfile.match(/runtime_arch="\$\(cat \/copilot-bin\/runtime-arch\)"/g) ?? []).length, 2);
  assert.match(apiDockerfile, /\/app\/publish\/runtimes\/linux-\$\{runtime_arch\}\/native\/copilot/);
  assert.match(apiDockerfile, /--runtime "linux-\$\{runtime_arch\}"/);
  assert.doesNotMatch(apiDockerfile, /linux-\$\{TARGETARCH\}|linux-amd64/);
});

test("api image installs architecture-matched GitHub CLI from pinned, verified release artifacts", () => {
  const dockerfile = apiDockerfile;
  assert.match(dockerfile, /ARG GH_CLI_VERSION=2\.98\.0/);
  assert.match(dockerfile, /ARG GH_CLI_SHA256_AMD64=f65a3fa2fa0eb2e97c445ee3f5e087a40aae03b64847f45a8f13805e504535d6/);
  assert.match(dockerfile, /ARG GH_CLI_SHA256_ARM64=bbc4ac7964c2a091fd555cd1758d10a7cfcfdc472e405f0b0fb958f05d535cb6/);
  assert.match(dockerfile, /amd64\) gh_sha256="\$\{GH_CLI_SHA256_AMD64\}"/);
  assert.match(dockerfile, /arm64\) gh_sha256="\$\{GH_CLI_SHA256_ARM64\}"/);
  assert.match(
    dockerfile,
    /github\.com\/cli\/cli\/releases\/download\/v\$\{GH_CLI_VERSION\}\/gh_\$\{GH_CLI_VERSION\}_linux_\$\{TARGETARCH\}\.deb/,
  );
  assert.match(dockerfile, /echo "\$\{gh_sha256\}  \/tmp\/gh\.deb" \| sha256sum -c -/);
  assert.doesNotMatch(dockerfile, /apt-get install[^\n]*gh=\$\{GH_CLI_VERSION\}/);
});

test("api image rejects unsupported TARGETARCH before downloads in both stages", () => {
  assert.equal((apiDockerfile.match(/ARG TARGETARCH/g) ?? []).length, 2);
  assert.equal((apiDockerfile.match(/\*\) echo "Unsupported API image TARGETARCH: \$\{TARGETARCH\}" >&2; exit 1/g) ?? []).length, 2);
  assert.match(apiDockerfile, /\/bin\/linux\/\$\{TARGETARCH\}\/kubectl/);
  assert.match(apiDockerfile, /\/bin\/linux\/\$\{TARGETARCH\}\/kubectl\.sha256/);
  assert.doesNotMatch(apiDockerfile, /\/bin\/linux\/amd64\/kubectl/);
});

test("image-spec: agent-host uses AGENTHOST_IMAGE_TAG; others use IMAGE_TAG", () => {
  assert.equal(getImage("agentweaver-agent-host").tagField, "AGENTHOST_IMAGE_TAG");
  assert.equal(getImage("agentweaver-api").tagField, "IMAGE_TAG");
  assert.equal(getImage("agentweaver-frontend").tagField, "IMAGE_TAG");
  assert.equal(getImage("agentweaver-mcp").tagField, "IMAGE_TAG");
});

test("bugfix: nuget.config is lowercase (matches the real repo file, not legacy 'NuGet.config')", () => {
  assert.ok(COMMON_DOTNET_PATHS.includes("nuget.config"), "COMMON_DOTNET_PATHS must include the lowercase filename");
  assert.ok(!COMMON_DOTNET_PATHS.includes("NuGet.config"), "must not reintroduce the case-mismatched legacy entry");
});

test("bugfix: VERSION is a watched path for every .NET image (api/mcp/agent-host)", () => {
  for (const name of ["agentweaver-api", "agentweaver-mcp", "agentweaver-agent-host"]) {
    assert.ok(getImage(name).watchedPaths.includes("VERSION"), `${name} must watch VERSION`);
  }
});

test("bugfix: api image watches apps/Agentweaver.Api.Data and apps/Agentweaver.Api.Migrations.Postgres", () => {
  const api = getImage("agentweaver-api");
  assert.ok(api.watchedPaths.includes("apps/Agentweaver.Api.Data"));
  assert.ok(api.watchedPaths.includes("apps/Agentweaver.Api.Migrations.Postgres"));
});

test("frontend watched paths are unaffected by the .NET COMMON_DOTNET_PATHS bugfixes (different build, no .sln/nuget dependency)", () => {
  const frontend = getImage("agentweaver-frontend");
  assert.deepEqual([...frontend.watchedPaths].sort(), ["apps/Agentweaver.Web", "apps/web"].sort());
});

test("agent-host provenance allows ephemeral pods; api/frontend/mcp do not", () => {
  assert.equal(getImage("agentweaver-agent-host").provenance.allowEphemeralPods, true);
  assert.equal(getImage("agentweaver-api").provenance.allowEphemeralPods, false);
  assert.equal(getImage("agentweaver-frontend").provenance.allowEphemeralPods, false);
  assert.equal(getImage("agentweaver-mcp").provenance.allowEphemeralPods, false);
});

test("bugfix: buildArgsFor always includes IMAGE_TAG and GIT_SHA build-args", () => {
  const args = buildArgsFor("v1.2.3", "abc1234");
  assert.deepEqual(args, ["--build-arg", "IMAGE_TAG=v1.2.3", "--build-arg", "GIT_SHA=abc1234"]);
});

test("getImage: throws a clear error for an unknown image name", () => {
  assert.throws(() => getImage("does-not-exist"), /unknown image/);
});

test("IMAGES and per-image path arrays are frozen (no accidental mutation/duplication across importers)", () => {
  assert.ok(Object.isFrozen(IMAGES));
  for (const image of IMAGES) {
    assert.ok(Object.isFrozen(image), `${image.name} should be frozen`);
    assert.ok(Object.isFrozen(image.watchedPaths), `${image.name}.watchedPaths should be frozen`);
  }
});
