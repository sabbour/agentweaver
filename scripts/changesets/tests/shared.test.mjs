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
  validateReleasePreparation,
  validateReleasePreparationFiles,
  validateSyncBranch,
  getUnexpectedIgnoredFiles,
} from "../shared.mjs";

test("version mirrors require VERSION, package.json, and lockfile to match", () => {
  const files = new Map([
    ["/repo/VERSION", "0.9.70\n"],
    ["/repo/package.json", '{"version":"0.9.70"}'],
    ["/repo/package-lock.json", '{"packages":{"":{"version":"0.9.70"}}}'],
  ]);
  const readFile = (file) => files.get(file.replaceAll("\\", "/"));

  assert.equal(assertVersionMirrors("/repo", { readFile }), "0.9.70");
  files.set("/repo/package-lock.json", '{"packages":{"":{"version":"0.9.69"}}}');
  assert.throws(() => assertVersionMirrors("/repo", { readFile }), /Version mirrors disagree/);
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

test("getUnexpectedIgnoredFiles filters allowed ignored patterns", () => {
  const stdout = [
    "!! node_modules/",
    "!! apps/web/node_modules/",
    "!! dist/",
    "!! apps/web/dist/",
    "!! obj/",
    "!! bin/",
    "!! packages/Agentweaver.SandboxExec/bin/Debug/",
    "!! packages/Agentweaver.SandboxExec/bin/Release/",
    "!! TestResults/",
    "!! .squad/",
    "!! .vite/",
    "!! .idea/",
    "!! .vscode/",
    "!! .vs/",
    "!! .env",
    "!! .env.local",
    "!! npm-debug.log",
    "!! scripts/azure/params.test.json",
    "!! scripts/azure/steps/.rendered/",
    "!! scripts/azure/tests/.scratch-123",
    "!! .security/",
    "!! test.user",
    "!! test.tsbuildinfo",
    "!! malicious.js",
    "!! src/malicious.js"
  ].join("\n");
  const unexpected = getUnexpectedIgnoredFiles(stdout);
  assert.deepEqual(unexpected, ["malicious.js", "src/malicious.js"]);
});
