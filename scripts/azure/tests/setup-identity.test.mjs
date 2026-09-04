import test from "node:test";
import assert from "node:assert/strict";
import { oauthCertificateNames } from "../steps/15-setup-identity.mjs";

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
