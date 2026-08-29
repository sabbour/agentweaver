import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "..");
const surfaceFiles = [
  ".env.example",
  "k8s/base/api-deployment.yaml",
  "k8s/base/worker-deployment.yaml",
  "k8s/base/secret-provider-class.yaml",
  "k8s/overlays/production/kustomization.yaml",
  "scripts/azure/variables.mjs",
  "scripts/azure/provision-infra.mjs",
  "scripts/azure/steps/15-setup-identity.mjs",
  "scripts/azure/dev.mjs",
  "apps/web/src/App.tsx",
  "apps/web/src/api/client.ts",
  "apps/web/src/api/types.ts",
  "apps/web/src/pages/SettingsPage.tsx",
  "apps/web/src/pages/ProjectGalleryPage.tsx",
  "apps/web/src/pages/ProjectSettingsPage.tsx",
  "docs/guide/authentication.md",
  "docs/guide/configuration.md",
  "docs/guide/deployment-aks.md",
  "docs/guide/getting-started.md",
  "docs/guide/architecture-aks.md",
  "docs/reference/api.md",
  "docs/reference/web.md",
  "docs/mcp-oauth.md",
  "README.md",
];
const forbidden = [
  "GITHUB_CLIENT_ID",
  "GITHUB_CLIENT_SECRET",
  "github-client-id",
  "github-client-secret",
  "GITHUB_CALLBACK_URL",
  "GITHUB_FRONTEND_URL",
  "GITHUB_ALLOWED_ORG",
  "GitHubLegacy",
  "github-legacy",
  "GitHub OAuth",
];

test("Fleet OAuth surface cutover leaves no legacy OAuth provisioning references", () => {
  for (const relativePath of surfaceFiles) {
    const contents = fs.readFileSync(path.join(repoRoot, relativePath), "utf8");
    for (const reference of forbidden) {
      assert.equal(
        contents.includes(reference),
        false,
        `${relativePath} must not reference legacy OAuth provisioning value ${reference}`,
      );
    }
  }
});
