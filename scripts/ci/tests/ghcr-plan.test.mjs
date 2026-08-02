import test from "node:test";
import assert from "node:assert/strict";

import {
  REGISTRY,
  buildPlan,
  imageReferences,
  main,
  primaryTag,
  resolveChannel,
  shortRef,
  tagsForChannel,
  versionFromRef,
} from "../ghcr-plan.mjs";
import { IMAGES } from "../../azure/image-spec.mjs";

const SHA = "abcdef1234567890abcdef1234567890abcdef12";

test("shortRef strips branch and tag ref prefixes", () => {
  assert.equal(shortRef("refs/heads/dev"), "dev");
  assert.equal(shortRef("refs/heads/release/v1.2.3"), "release/v1.2.3");
  assert.equal(shortRef("refs/tags/v1.2.3"), "v1.2.3");
  assert.equal(shortRef("dev"), "dev");
});

test("versionFromRef reads semver from release branches and tags", () => {
  assert.equal(versionFromRef("refs/heads/release/v1.2.3"), "1.2.3");
  assert.equal(versionFromRef("refs/tags/v0.13.0"), "0.13.0");
  assert.equal(versionFromRef("v1.2.3-rc.1"), "1.2.3-rc.1");
  assert.equal(versionFromRef("refs/heads/dev"), null);
  assert.equal(versionFromRef("refs/heads/feature/thing"), null);
});

test("resolveChannel maps each branch topology stage to a channel", () => {
  assert.equal(resolveChannel({ eventName: "push", ref: "refs/heads/dev" }), "dev");
  assert.equal(resolveChannel({ eventName: "push", ref: "refs/heads/main" }), "main");
  assert.equal(resolveChannel({ eventName: "push", ref: "refs/heads/release/v1.2.3" }), "rc");
  assert.equal(resolveChannel({ eventName: "release", ref: "refs/tags/v1.2.3", releaseTag: "v1.2.3" }), "release");
  assert.equal(resolveChannel({ eventName: "push", ref: "refs/heads/copilot/thing" }), "commit");
  assert.equal(
    resolveChannel({ eventName: "workflow_dispatch", ref: "refs/heads/copilot/thing" }),
    "commit",
  );
  assert.equal(resolveChannel({ eventName: "workflow_dispatch", ref: "refs/heads/dev" }), "dev");
});

test("every channel emits the immutable short-sha tag", () => {
  for (const channel of ["dev", "main", "commit"]) {
    assert.deepEqual(tagsForChannel({ channel, sha: SHA })[0], "sha-abcdef1");
  }
});

test("dev and main pushes add only their moving branch tag", () => {
  assert.deepEqual(tagsForChannel({ channel: "dev", sha: SHA }), ["sha-abcdef1", "dev"]);
  assert.deepEqual(tagsForChannel({ channel: "main", sha: SHA }), ["sha-abcdef1", "main"]);
});

test("an arbitrary commit build is addressable only by its sha", () => {
  assert.deepEqual(tagsForChannel({ channel: "commit", sha: SHA, ref: "refs/heads/wip" }), [
    "sha-abcdef1",
  ]);
});

test("release-candidate branches get an rc-<version> tag", () => {
  assert.deepEqual(tagsForChannel({ channel: "rc", sha: SHA, ref: "refs/heads/release/v1.2.3" }), [
    "sha-abcdef1",
    "rc-1.2.3",
  ]);
});

test("published releases get semver tags plus latest", () => {
  assert.deepEqual(tagsForChannel({ channel: "release", sha: SHA, releaseTag: "v1.2.3" }), [
    "sha-abcdef1",
    "1.2.3",
    "v1.2.3",
    "latest",
  ]);
});

test("prereleases never move latest", () => {
  assert.deepEqual(
    tagsForChannel({ channel: "release", sha: SHA, releaseTag: "v1.2.3", prerelease: true }),
    ["sha-abcdef1", "1.2.3", "v1.2.3"],
  );
  assert.deepEqual(tagsForChannel({ channel: "release", sha: SHA, releaseTag: "v1.2.3-rc.1" }), [
    "sha-abcdef1",
    "1.2.3-rc.1",
    "v1.2.3-rc.1",
  ]);
});

test("missing sha or version inputs fail loudly instead of publishing a mistagged image", () => {
  assert.throws(() => tagsForChannel({ channel: "dev", sha: "" }), /commit sha is required/);
  assert.throws(
    () => tagsForChannel({ channel: "rc", sha: SHA, ref: "refs/heads/release/nope" }),
    /could not derive a version/,
  );
  assert.throws(
    () => tagsForChannel({ channel: "release", sha: SHA, releaseTag: "nope" }),
    /could not derive a version/,
  );
});

test("primaryTag prefers a semver tag and otherwise identifies the commit", () => {
  assert.equal(primaryTag(tagsForChannel({ channel: "dev", sha: SHA })), "sha-abcdef1");
  assert.equal(
    primaryTag(tagsForChannel({ channel: "release", sha: SHA, releaseTag: "v1.2.3" })),
    "v1.2.3",
  );
  assert.equal(
    primaryTag(tagsForChannel({ channel: "rc", sha: SHA, ref: "refs/heads/release/v1.2.3" })),
    "sha-abcdef1",
  );
});

test("imageReferences lowercases the owner namespace", () => {
  assert.deepEqual(imageReferences("Sabbour", "agentweaver-api", ["dev"]), [
    `${REGISTRY}/sabbour/agentweaver-api:dev`,
  ]);
});

test("buildPlan covers exactly the images declared in image-spec", () => {
  const plan = buildPlan({
    owner: "sabbour",
    eventName: "push",
    ref: "refs/heads/dev",
    sha: SHA,
  });
  assert.equal(plan.channel, "dev");
  assert.deepEqual(
    plan.images.map((i) => i.name),
    IMAGES.map((i) => i.name),
  );
  const frontend = plan.images.find((i) => i.name === "agentweaver-frontend");
  assert.equal(frontend.frontendBuild, true);
  assert.equal(frontend.dockerfile, "apps/web/Dockerfile");
  assert.equal(
    frontend.tags,
    `${REGISTRY}/sabbour/agentweaver-frontend:sha-abcdef1\n${REGISTRY}/sabbour/agentweaver-frontend:dev`,
  );
  assert.equal(plan.images.filter((i) => i.frontendBuild).length, 1);
});

test("main emits channel, tags and a JSON matrix for the workflow", () => {
  const lines = [];
  const plan = main({
    env: {
      GITHUB_REPOSITORY_OWNER: "sabbour",
      GITHUB_EVENT_NAME: "release",
      GITHUB_REF: "refs/tags/v1.2.3",
      GITHUB_SHA: SHA,
      RELEASE_TAG: "v1.2.3",
      RELEASE_PRERELEASE: "false",
    },
    write: (line) => lines.push(line),
  });

  assert.equal(plan.channel, "release");
  assert.equal(lines[0], "channel=release");
  assert.equal(lines[1], "tags=sha-abcdef1,1.2.3,v1.2.3,latest");
  assert.equal(lines[2], "primary_tag=v1.2.3");
  const matrix = JSON.parse(lines[3].slice("matrix=".length));
  assert.equal(matrix.include.length, IMAGES.length);
  assert.ok(matrix.include.every((entry) => entry.tags.includes(`${REGISTRY}/sabbour/`)));
});
