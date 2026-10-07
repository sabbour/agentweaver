import test from "node:test";
import assert from "node:assert/strict";
import {
  assertVersionMirrors,
  applyExplicitReleaseTarget,
  compareSemver,
  extractChangelogSection,
  hasChangesetExemption,
  isReleaseMetadataOnly,
  isReleaseRelevant,
  latestPublishedVersion,
  parseReleaseTarget,
  parseChangesetFragment,
  releaseBranchVersion,
  synchronizePackageLockVersion,
  validatePublishedReleaseState,
  validateReleasePreparation,
  validateReleasePreparationFiles,
  validateSyncBranch,
  getUnexpectedIgnoredFiles,
} from "../shared.mjs";

test("published release state requires dev forward-port before another release", () => {
  const refs = [
    "1111111111111111111111111111111111111111\trefs/tags/v0.32.4",
    "2222222222222222222222222222222222222222\trefs/tags/v0.9.70",
    "3333333333333333333333333333333333333333\trefs/tags/not-a-release",
    "4444444444444444444444444444444444444444\trefs/tags/v0.32.5",
  ].join("\n");

  assert.equal(compareSemver("0.9.70", "0.32.5"), -1);
  assert.equal(compareSemver("0.32.5", "0.32.5"), 0);
  assert.equal(compareSemver("1.0.0", "0.32.5"), 1);
  assert.equal(latestPublishedVersion(refs), "0.32.5");
  assert.equal(latestPublishedVersion(""), undefined);
  assert.throws(
    () => validatePublishedReleaseState("0.32.4", "0.32.5", "0.32.5"),
    /missing the previous release preparation forward-port/,
  );
  assert.throws(
    () => validatePublishedReleaseState("0.32.5", "0.32.5", "0.32.5"),
    /not newer than published/,
  );
  assert.doesNotThrow(() => validatePublishedReleaseState("0.32.5", "0.32.6", "0.32.5"));
});

test("release target parsing accepts one separated option and rejects malformed forms", () => {
  assert.equal(parseReleaseTarget([]), undefined);
  assert.equal(parseReleaseTarget(["--target", "0.34.2"]), "0.34.2");
  assert.throws(() => parseReleaseTarget(["--target"]), /Use --target X\.Y\.Z/);
  assert.throws(() => parseReleaseTarget(["--target", "--expected"]), /Use --target X\.Y\.Z/);
  assert.throws(() => parseReleaseTarget(["--target=0.34.2"]), /Use --target X\.Y\.Z/);
  assert.throws(() => parseReleaseTarget(["--target", "0.34.2", "--target", "0.34.3"]), /only once/);
});

test("release targets preserve the native minimum and only adjust an eligible package release", () => {
  const releasePlan = {
    releases: [{ name: "agentweaver", oldVersion: "0.34.0", newVersion: "0.34.1" }],
  };

  assert.equal(applyExplicitReleaseTarget(releasePlan, undefined, "0.34.0", "0.34.0"), undefined);
  assert.equal(releasePlan.releases[0].newVersion, "0.34.1");
  assert.deepEqual(
    applyExplicitReleaseTarget(releasePlan, "0.34.2", "0.34.0", "0.34.0"),
    { nativeVersion: "0.34.1" },
  );
  assert.equal(releasePlan.releases[0].newVersion, "0.34.2");
});

test("release targets preserve native fixed and linked package groups", () => {
  for (const groupType of ["fixed", "linked"]) {
    const releasePlan = {
      releases: [
        { name: "agentweaver", oldVersion: "0.34.0", newVersion: "0.34.1" },
        { name: "agentweaver-cli", oldVersion: "0.34.0", newVersion: "0.34.1" },
      ],
    };
    const config = {
      fixed: groupType === "fixed" ? [["agentweaver", "agentweaver-cli"]] : [],
      linked: groupType === "linked" ? [["agentweaver", "agentweaver-cli"]] : [],
    };

    applyExplicitReleaseTarget(releasePlan, "0.34.2", "0.34.0", "0.34.0", config);
    assert.deepEqual(
      releasePlan.releases.map((release) => release.newVersion),
      ["0.34.2", "0.34.2"],
      `${groupType} members in the native plan must receive the same target`,
    );
  }
});

test("release targets reject an incomplete native fixed group without mutating the plan", () => {
  const releasePlan = {
    releases: [{ name: "agentweaver", oldVersion: "0.34.0", newVersion: "0.34.1" }],
  };
  const before = structuredClone(releasePlan);

  assert.throws(
    () => applyExplicitReleaseTarget(
      releasePlan,
      "0.34.2",
      "0.34.0",
      "0.34.0",
      { fixed: [["agentweaver", "agentweaver-cli"]], linked: [] },
    ),
    /fixed release group is missing 'agentweaver-cli'/,
  );
  assert.deepEqual(releasePlan, before);
});

test("inactive linked packages do not activate unrelated fixed groups", () => {
  const releasePlan = {
    releases: [{ name: "agentweaver", oldVersion: "0.34.0", newVersion: "0.34.1" }],
  };

  applyExplicitReleaseTarget(releasePlan, "0.34.2", "0.34.0", "0.34.0", {
    fixed: [["linked-peer", "fixed-peer"]],
    linked: [["agentweaver", "linked-peer"]],
  });
  assert.equal(releasePlan.releases[0].newVersion, "0.34.2");
});

test("invalid release targets are rejected without changing the native plan", () => {
  const cases = [
    {
      name: "malformed",
      target: "0.34",
      nativeVersion: "0.34.1",
      currentVersion: "0.34.0",
      publishedVersion: "0.34.0",
      error: /X\.Y\.Z format/,
    },
    {
      name: "leading-zero component",
      target: "0.34.02",
      nativeVersion: "0.34.1",
      currentVersion: "0.34.0",
      publishedVersion: "0.34.0",
      error: /no leading zeroes/,
    },
    {
      name: "unsafe numeric component",
      target: "0.9007199254740992.0",
      nativeVersion: "0.34.1",
      currentVersion: "0.34.0",
      publishedVersion: "0.34.0",
      error: /safe integer components/,
    },
    {
      name: "equal to current",
      target: "0.34.0",
      nativeVersion: "0.34.1",
      currentVersion: "0.34.0",
      publishedVersion: "0.34.0",
      error: /newer than current version/,
    },
    {
      name: "lower than current",
      target: "0.33.9",
      nativeVersion: "0.34.1",
      currentVersion: "0.34.0",
      publishedVersion: "0.33.8",
      error: /newer than current version/,
    },
    {
      name: "already published",
      target: "0.34.1",
      nativeVersion: "0.34.2",
      currentVersion: "0.34.0",
      publishedVersion: "0.34.1",
      error: /newer than latest published/,
    },
    {
      name: "wrong major/minor series",
      target: "0.35.0",
      nativeVersion: "0.34.1",
      currentVersion: "0.34.0",
      publishedVersion: "0.34.0",
      error: /native release series/,
    },
    {
      name: "below native minimum",
      target: "0.34.1",
      nativeVersion: "0.34.2",
      currentVersion: "0.34.0",
      publishedVersion: "0.34.0",
      error: /below Changesets' required native bump/,
    },
  ];

  for (const scenario of cases) {
    const releasePlan = {
      releases: [{ name: "agentweaver", oldVersion: "0.34.0", newVersion: scenario.nativeVersion }],
    };
    const before = structuredClone(releasePlan);

    assert.throws(
      () => applyExplicitReleaseTarget(
        releasePlan,
        scenario.target,
        scenario.currentVersion,
        scenario.publishedVersion,
      ),
      scenario.error,
      scenario.name,
    );
    assert.deepEqual(releasePlan, before, `${scenario.name} must not modify the native plan`);
  }
});

test("version mirrors require VERSION, package.json, and lockfile to match", () => {
  const files = new Map([
    ["/repo/VERSION", "0.9.70\n"],
    ["/repo/package.json", '{"version":"0.9.70"}'],
    ["/repo/package-lock.json", '{"version":"0.9.70","packages":{"":{"version":"0.9.70"}}}'],
  ]);
  const readFile = (file) => files.get(file.replaceAll("\\", "/"));

  assert.equal(assertVersionMirrors("/repo", { readFile }), "0.9.70");
});

test("version mirrors reject stale and missing package-lock versions independently", () => {
  const files = new Map([
    ["/repo/VERSION", "0.9.70\n"],
    ["/repo/package.json", '{"version":"0.9.70"}'],
  ]);
  const readFile = (file) => files.get(file.replaceAll("\\", "/"));
  const assertLockRejected = (lock, expected) => {
    files.set("/repo/package-lock.json", JSON.stringify(lock));
    assert.throws(() => assertVersionMirrors("/repo", { readFile }), expected);
  };

  assertLockRejected(
    { version: "0.9.69", packages: { "": { version: "0.9.70" } } },
    /package-lock\.json\.version=0\.9\.69/,
  );
  assertLockRejected(
    { packages: { "": { version: "0.9.70" } } },
    /package-lock\.json\.version contains invalid semver: 'missing'/,
  );
  assertLockRejected(
    { version: "0.9.70", packages: { "": { version: "0.9.69" } } },
    /package-lock\.json\.packages\[""\]\.version=0\.9\.69/,
  );
  assertLockRejected(
    { version: "0.9.70", packages: { "": {} } },
    /package-lock\.json\.packages\[""\]\.version contains invalid semver: 'missing'/,
  );
});

test("package-lock synchronization updates both root version mirrors", () => {
  let written;
  const readFile = () => JSON.stringify({
    name: "agentweaver",
    version: "0.16.2",
    lockfileVersion: 3,
    packages: {
      "": {
        name: "agentweaver",
        version: "0.16.2",
      },
    },
  });
  const writeFile = (_file, content) => {
    written = content;
  };

  synchronizePackageLockVersion("/repo", "0.17.0", { readFile, writeFile });

  const lock = JSON.parse(written);
  assert.equal(lock.version, "0.17.0");
  assert.equal(lock.packages[""].version, "0.17.0");
  assert.match(written, /\n$/);
  assert.throws(
    () => synchronizePackageLockVersion("/repo", "next", { readFile, writeFile }),
    /invalid semver/,
  );
});

test("extractChangelogSection returns only the requested version section", () => {
  const text = "# Changelog\n\n## 0.9.71\n\n- New note\n\n## 0.9.70\n\n- Old note\n";

  assert.equal(extractChangelogSection(text, "0.9.71"), "## 0.9.71\n\n- New note");
  assert.throws(() => extractChangelogSection(text, "0.9.72"), /no section/);
});

test("extractChangelogSection accepts bracketed and v-prefixed heading forms", () => {
  const text =
    "# Changelog\n\n## [v0.9.70] - 2026-07-16\n\nsome text\n\n## [v0.9.69]\n\nolder text\n";

  assert.equal(
    extractChangelogSection(text, "0.9.70"),
    "## [v0.9.70] - 2026-07-16\n\nsome text",
  );
  assert.equal(extractChangelogSection("## v0.9.70\n\nplain v-prefixed", "0.9.70"), "## v0.9.70\n\nplain v-prefixed");
  assert.equal(extractChangelogSection("## [0.9.70]\n\nbracketed", "0.9.70"), "## [0.9.70]\n\nbracketed");
  assert.equal(extractChangelogSection("## 0.9.70\n\nbare", "0.9.70"), "## 0.9.70\n\nbare");
});

test("release branch parser accepts only release/vX.Y.Z", () => {
  assert.equal(releaseBranchVersion("release/v0.10.0"), "0.10.0");
  assert.equal(releaseBranchVersion("dev"), undefined);
});

test("changeset fragments require the sole agentweaver package and prose", () => {
  const valid = '---\n"agentweaver": minor\n---\n\nDescribe the user-facing feature.';
  assert.deepEqual(parseChangesetFragment(valid), {
    packageName: "agentweaver",
    bump: "minor",
    summary: "Describe the user-facing feature.",
  });

  assert.throws(() => parseChangesetFragment("not frontmatter"), /frontmatter/);
  assert.throws(() => parseChangesetFragment('---\n"other": patch\n---\n\nNote'), /agentweaver/);
  assert.throws(() => parseChangesetFragment('---\n"agentweaver": major\n---\n\nNote'), /major changesets/);
  assert.throws(() => parseChangesetFragment('---\n"agentweaver": patch\n---\n\n   '), /prose/);
});

test("changeset relevance and exemption policy distinguishes advisory cases", () => {
  assert.equal(isReleaseRelevant(["apps/Agentweaver.Api/Program.cs"]), true);
  assert.equal(isReleaseRelevant(["docs/guide.md", "tests/example.test.mjs"]), false);
  assert.equal(isReleaseMetadataOnly(["VERSION", ".changeset/release.md"]), true);
  assert.equal(isReleaseMetadataOnly(["VERSION", "apps/web/src/App.tsx"]), false);
  assert.equal(hasChangesetExemption(["changeset:not-required"], "Changeset exemption: documentation only"), true);
  assert.equal(hasChangesetExemption(["changeset:not-required"], "No rationale"), false);
});

test("isReleaseRelevant excludes test-only paths under release-relevant prefixes", () => {
  assert.equal(
    isReleaseRelevant(["apps/web/src/__tests__/App.test.tsx", "packages/foo/src/bar.spec.ts"]),
    false,
  );
  assert.equal(
    isReleaseRelevant(["apps/web/src/__tests__/App.test.tsx", "apps/web/src/App.tsx"]),
    true,
  );
  assert.equal(isReleaseRelevant(["scripts/azure/tests/deploy.test.mjs"]), false);
});

test("isReleaseRelevant ignores doc-only and CI/config-only paths", () => {
  assert.equal(isReleaseRelevant(["docs/guide.md", "README.md"]), false);
  assert.equal(isReleaseRelevant([".github/workflows/ci.yml", ".squad/decisions.md", ".copilot/skills/x/SKILL.md"]), false);
});

test("isReleaseMetadataOnly still holds for release-prep-only diffs", () => {
  assert.equal(
    isReleaseMetadataOnly(["VERSION", "package.json", "package-lock.json", "CHANGELOG.md", ".changeset/note.md"]),
    true,
  );
  assert.equal(isReleaseMetadataOnly(["VERSION", "apps/web/src/App.tsx"]), false);
});

test("release preparation validates expected version, branch, and 0.x policy", () => {
  assert.doesNotThrow(() => validateReleasePreparation("0.10.0", "release/v0.10.0", "0.10.0"));
  assert.doesNotThrow(() => validateReleasePreparation("1.0.0", "release/v1.0.0", "1.0.0"));
  assert.throws(() => validateReleasePreparation("0.10.0", "dev", "0.10.0"), /release\/v0.10.0/);
  assert.throws(() => validateReleasePreparation("0.10.0", "release/v0.10.0", "0.10.1"), /calculated/);
  assert.throws(() => validateReleasePreparation("2.0.0", "release/v2.0.0", "2.0.0"), /major-version/);
});

test("dev sync validates branch and prepared release metadata", () => {
  const files = ["VERSION", "package.json", "package-lock.json", "CHANGELOG.md", ".changeset/a.md"];

  assert.doesNotThrow(() => validateSyncBranch("chore/sync-v0.10.0"));
  assert.throws(() => validateSyncBranch("dev"), /short-lived branch/);
  assert.throws(() => validateSyncBranch(""), /short-lived branch/);
  assert.doesNotThrow(() => validateReleasePreparationFiles("abc123", files));
  assert.throws(() => validateReleasePreparationFiles("abc123", files.filter((file) => file !== "VERSION")), /missing VERSION/);
  assert.throws(() => validateReleasePreparationFiles("abc123", files.slice(0, 4)), /does not consume changesets/);
});

test("getUnexpectedIgnoredFiles allows standard build/dep/output roots, flags unexpected paths", () => {
  // `git status --porcelain --ignored=matching` COLLAPSES a wholly-ignored directory to a
  // single trailing-slash entry (e.g. `!! node_modules/`) and never lists its contents, so
  // flagging that root protects nothing while blocking every real release. Policy: allow the
  // standard dependency/build/output roots that always exist in a dev checkout, but still
  // flag ignored files in UNEXPECTED locations (repo root, tracked source trees, unknown dirs).
  const stdout = [
    // Editor / local-tooling (allowed).
    "!! .squad/",
    "!! .idea/",
    "!! .vscode/",
    "!! .vs/",
    "!! .security/",
    "!! .impeccable/",
    "!! .env",
    "!! .env.local",
    "!! apps/web/.env",
    "!! apps/Agentweaver.Api/appsettings.Development.json",
    "!! npm-debug.log",
    "!! scripts/azure/params.test.json",
    "!! scripts/azure/steps/.rendered/",
    "!! scripts/azure/tests/.scratch-123",
    "!! test.user",
    // Standard collapsed dependency/build/output roots (allowed).
    "!! node_modules/",
    "!! dist/",
    "!! apps/web/dist/",
    "!! docs/node_modules/",
    "!! apps/web/.vite/",
    "!! tests/Agentweaver.Tests/TestResults/",
    "!! tests/e2e/playwright-report/",
    "!! tests/e2e/test-results/",
    "!! docs/diagram-renderer/public/specs/",
    // Harness run-artifact dirs enumerate individual files (they keep a tracked .gitignore).
    "!! scripts/api-harness/findings/run-2026.json",
    "!! scripts/api-harness/transcripts/live.jsonl",
    "!! scripts/mcp-harness/dispatch/jordan.md",
    "!! scripts/ui-harness/sessions/",
    // Genuinely UNEXPECTED ignored paths (must still be flagged).
    "!! malicious.js",
    "!! src/malicious.js",
    "!! weird-ignored-dir/"
  ].join("\n");
  const unexpected = getUnexpectedIgnoredFiles(stdout);
  assert.deepEqual(unexpected, ["malicious.js", "src/malicious.js", "weird-ignored-dir/"]);
});

test("getUnexpectedIgnoredFiles allows nested package build artifacts", () => {
  // An optional leading path prefix lets the standard roots match under any package, so a
  // nested build/output directory (git-collapsed) is treated the same as the repo-root one.
  const stdout = [
    "!! packages/Agentweaver.Domain/obj/",
    "!! packages/Agentweaver.AgentRuntime/bin/",
    "!! packages/Agentweaver.SandboxExec/bin/Debug/",
    "!! packages/Agentweaver.SandboxExec/bin/Release/",
    "!! apps/Agentweaver.Api/obj/"
  ].join("\n");
  assert.deepEqual(getUnexpectedIgnoredFiles(stdout), []);
});

test("getUnexpectedIgnoredFiles normalizes Windows separators and path casing", () => {
  const stdout = [
    "!! NODE_MODULES\\",
    "!! Apps\\Web\\DIST\\",
    "!! packages\\Agentweaver.Domain\\OBJ\\",
    "!! packages\\Agentweaver.AgentRuntime\\BIN\\DEBUG\\",
    "!! Tests\\Agentweaver.Tests\\TESTRESULTS\\",
    "!! Scripts\\API-Harness\\Findings\\run-2026.json",
    "!! Apps\\Agentweaver.Api\\APPSETTINGS.DEVELOPMENT.JSON",
    "!! SRC\\Backdoor.TS",
  ].join("\r\n");

  assert.deepEqual(getUnexpectedIgnoredFiles(stdout), ["SRC\\Backdoor.TS"]);
});

test("getUnexpectedIgnoredFiles flags planted files outside recognized roots", () => {
  // Defense that still matters: an ignored file at the repo root or inside a tracked source
  // tree, and an unknown ignored directory, must be surfaced for a human to investigate.
  // A named file git somehow enumerates directly inside a collapsed root (dir patterns are
  // anchored to the trailing slash) is also still flagged.
  const stdout = [
    "!! evil.env.js",
    "!! src/backdoor.ts",
    "!! apps/Agentweaver.Api/Program.injected.cs",
    "!! totally-unknown-dir/",
    "!! node_modules/.hook/malicious.js"
  ].join("\n");
  assert.deepEqual(getUnexpectedIgnoredFiles(stdout), [
    "evil.env.js",
    "src/backdoor.ts",
    "apps/Agentweaver.Api/Program.injected.cs",
    "totally-unknown-dir/",
    "node_modules/.hook/malicious.js"
  ]);
});
