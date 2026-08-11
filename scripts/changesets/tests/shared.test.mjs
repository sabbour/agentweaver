import test from "node:test";
import assert from "node:assert/strict";
import {
  assertVersionMirrors,
  extractChangelogSection,
  hasChangesetExemption,
  isReleaseMetadataOnly,
  isReleaseRelevant,
  parseChangesetFragment,
  releaseBranchVersion,
  synchronizePackageLockVersion,
  validateReleasePreparation,
  validateReleasePreparationFiles,
  validateSyncBranch,
  getUnexpectedIgnoredFiles,
} from "../shared.mjs";

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
