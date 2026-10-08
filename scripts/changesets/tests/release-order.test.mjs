import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "../../..");

test("release runbook accepts existing exact-SHA evidence without requiring catalog diagnostics", () => {
  const runbook = fs.readFileSync(path.join(root, "RELEASING.md"), "utf8");
  const preparation = runbook.split("## Preparing a release\n")[1]?.split("## Publishing and deploying\n")[0];
  assert.ok(preparation, "release preparation section must exist");

  const inOrder = [
    "npm run release:plan",
    "npm run release:plan -- --target X.Y.Z",
    "Create `release/vX.Y.Z`",
    "npm run azure:deploy-from-commit -- <candidate-sha>",
    "npm run azure:verify",
    "Review the existing release-relevant evidence for this exact-SHA deployment",
    "After the exact-SHA candidate deployment and its existing release evidence are",
    "npm run release:prepare -- --expected X.Y.Z",
    "Promote the prepared branch to `main`",
  ];
  let previous = -1;
  for (const step of inOrder) {
    const index = preparation.indexOf(step);
    assert.ok(index > previous, `${step} must follow the preceding release step`);
    previous = index;
  }
  assert.match(preparation, /Promote the prepared branch to `main`[\s\S]*?gh pr merge <release-pr-number> --merge/);
  assert.match(preparation, /After promotion, confirm the resulting `main` source content matches the\s+accepted candidate/);
  assert.doesNotMatch(preparation, /Squash and merge|Squash promotion|Squash merging/);

  const publishing = runbook.split("## Publishing and deploying\n")[1];
  assert.match(publishing, /Do not publish until the exact-SHA candidate deployment is healthy\s+and its accepted release-relevant evidence is recorded/);
  assert.match(publishing, /ordinary shipping does not\s+require a new Harness scenario, catalog fixture, or diagnostic bundle/);
  assert.ok(publishing.indexOf("After publication, reconcile the milestones") > publishing.indexOf("npm run release:publish"));
  assert.match(preparation, /`--expected` remains an assertion/);
  assert.match(preparation, /newer than both `VERSION` and the\s+latest published tag/);
  assert.match(preparation, /Omitting `--target` preserves the native Changesets patch\/minor\s+result/);
  assert.match(preparation, /that invocation stops before\s+applying release metadata/);
  assert.match(preparation, /npm run release:prepare -- --expected X\.Y\.Z --target X\.Y\.Z/);
  assert.doesNotMatch(runbook, /local.k3s/i);
  for (const file of ["docs/guide/architecture-aks.md", "docs/guide/deployment-aks.md", "docs/guide/operations.md"]) {
    assert.doesNotMatch(fs.readFileSync(path.join(root, file), "utf8"), /local.k3s/i, `${file} must not offer a local k3s release route`);
  }
  const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  assert.ok(!Object.keys(packageJson.scripts).some((script) => script.includes("local-k3s")));
});
