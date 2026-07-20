import test from "node:test";
import assert from "node:assert/strict";
import { assertVersionMirrors, extractChangelogSection, releaseBranchVersion } from "../shared.mjs";

test("version mirrors require VERSION, package.json, and lockfile to match", () => {
  const files = new Map([["/repo/VERSION", "0.9.70\n"], ["/repo/package.json", '{"version":"0.9.70"}'], ["/repo/package-lock.json", '{"packages":{"":{"version":"0.9.70"}}}']]);
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

test("release branch parser accepts only release/vX.Y.Z", () => {
  assert.equal(releaseBranchVersion("release/v0.10.0"), "0.10.0");
  assert.equal(releaseBranchVersion("dev"), undefined);
});
