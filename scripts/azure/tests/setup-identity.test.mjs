import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { oauthCertificateNames } from "../steps/15-setup-identity.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

test("OAuth certificate provisioning resolves default active/previous version families", () => {
  assert.deepEqual(oauthCertificateNames(), {
    signing: "agentweaver-oauth-signing",
    encryption: "agentweaver-oauth-encryption",
  });
  assert.deepEqual(oauthCertificateNames({
    OAUTH_SIGNING_CERTIFICATE_NAME: "signing-rotation",
    OAUTH_ENCRYPTION_CERTIFICATE_NAME: "encryption-rotation",
  }), {
    signing: "signing-rotation",
    encryption: "encryption-rotation",
  });
});

test("identity provisioning creates a federated credential for the MCP service account", () => {
  const source = fs.readFileSync(
    path.join(__dirname, "..", "steps", "15-setup-identity.mjs"),
    "utf8",
  );

  assert.match(source, /agentweaver-mcp-fedcred/);
  assert.match(source, /system:serviceaccount:\$\{cfg\.NAMESPACE\}:agentweaver-mcp/);
});
