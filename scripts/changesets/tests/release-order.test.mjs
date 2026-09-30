import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "../../..");

test("release runbook requires exact-SHA E2E acceptance before preparation and publication", () => {
  const runbook = fs.readFileSync(path.join(root, "RELEASING.md"), "utf8");
  const preparation = runbook.split("## Preparing a release\n")[1]?.split("## Publishing and deploying\n")[0];
  assert.ok(preparation, "release preparation section must exist");

  const inOrder = [
    "npm run release:plan",
    "Create `release/vX.Y.Z`",
    "npm run azure:deploy-from-commit -- <candidate-sha>",
    "npm run azure:verify",
    "representative integration coverage and feature-specific API/UI acceptance",
    "Only after exact-SHA candidate acceptance passes",
    "npm run release:prepare -- --expected X.Y.Z",
    "Promote the prepared branch to `main`",
  ];
  let previous = -1;
  for (const step of inOrder) {
    const index = preparation.indexOf(step);
    assert.ok(index > previous, `${step} must follow the preceding release step`);
    previous = index;
  }

  const publishing = runbook.split("## Publishing and deploying\n")[1];
  assert.match(publishing, /Do not publish until the exact-SHA candidate deployment and its\s+representative integration and feature-specific API\/UI acceptance have passed/);
  assert.ok(publishing.indexOf("After publication, reconcile the milestones") > publishing.indexOf("npm run release:publish"));
  assert.doesNotMatch(runbook, /local.k3s/i);
  for (const file of ["docs/guide/architecture-aks.md", "docs/guide/deployment-aks.md", "docs/guide/operations.md"]) {
    assert.doesNotMatch(fs.readFileSync(path.join(root, file), "utf8"), /local.k3s/i, `${file} must not offer a local k3s release route`);
  }
  const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  assert.ok(!Object.keys(packageJson.scripts).some((script) => script.includes("local-k3s")));
});
