// render.test.mjs -- parity tests for lib/render.mjs (envsubst allow-list replacement).

import test from "node:test";
import assert from "node:assert/strict";
import { renderTemplate, parseAllowList } from "../lib/render.mjs";

test("parseAllowList extracts names from ${VAR} and $VAR forms", () => {
  assert.deepEqual(parseAllowList("${A} ${B} $C"), ["A", "B", "C"]);
});

test("substitutes only allow-listed ${VAR} references", () => {
  const template = "image: ${ACR_LOGIN_SERVER}/api:${IMAGE_TAG}";
  const out = renderTemplate(
    template,
    { ACR_LOGIN_SERVER: "registry.azurecr.io", IMAGE_TAG: "v1.2.3" },
    ["ACR_LOGIN_SERVER", "IMAGE_TAG"],
  );
  assert.equal(out, "image: registry.azurecr.io/api:v1.2.3");
});

test("leaves unknown/non-allow-listed variables completely literal", () => {
  const template = "value: ${HOST} other: ${NOT_ALLOWED} bare: $ALSO_NOT_ALLOWED";
  const out = renderTemplate(
    template,
    { HOST: "example.com", NOT_ALLOWED: "should-not-appear", ALSO_NOT_ALLOWED: "nope" },
    ["HOST"],
  );
  assert.equal(out, "value: example.com other: ${NOT_ALLOWED} bare: $ALSO_NOT_ALLOWED");
});

test("allow-listed variable with no value substitutes as empty string", () => {
  const template = 'value: "${PREVIEW_HOSTNAME}"';
  const out = renderTemplate(template, {}, ["PREVIEW_HOSTNAME"]);
  assert.equal(out, 'value: ""');
});

test("allow-listed variable explicitly undefined/null substitutes as empty string", () => {
  const template = "a=${A} b=${B}";
  const out = renderTemplate(template, { A: undefined, B: null }, ["A", "B"]);
  assert.equal(out, "a= b=");
});

test("accepts an envsubst-style allow-list string in addition to an array", () => {
  const template = "${A}-${B}-${C}";
  const out = renderTemplate(template, { A: "1", B: "2", C: "3" }, "${A} ${B}");
  assert.equal(out, "1-2-${C}");
});

test("bare $VAR form is substituted the same as ${VAR} when allow-listed", () => {
  const template = "$HOST and ${HOST}";
  const out = renderTemplate(template, { HOST: "example.com" }, ["HOST"]);
  assert.equal(out, "example.com and example.com");
});

test("matches 30-deploy.sh's real allow-list on a representative k8s snippet", () => {
  const allowList =
    "${HOST} ${ACR_LOGIN_SERVER} ${IMAGE_TAG} ${AGENTHOST_IMAGE_TAG} ${IDENTITY_CLIENT_ID} " +
    "${KEYVAULT_NAME} ${AGENTHOST_KEYVAULT_URI} ${TENANT_ID} ${PREVIEW_HOSTNAME} " +
    "${PREVIEW_TLS_SECRET} ${SANDBOX_PREVIEW_ENABLED} ${SANDBOX_PREVIEW_ZONE_SUFFIX} ${APPINSIGHTS_WORKSPACE_ID}";
  const template = [
    "image: ${ACR_LOGIN_SERVER}/agentweaver-api:${IMAGE_TAG}",
    'value: "${GitHub__ClientId}"',
    "# comment mentioning $HOME should not be touched",
  ].join("\n");
  const vars = { ACR_LOGIN_SERVER: "agentweaverregistry.azurecr.io", IMAGE_TAG: "v0.9.70" };
  const out = renderTemplate(template, vars, allowList);
  assert.equal(
    out,
    [
      "image: agentweaverregistry.azurecr.io/agentweaver-api:v0.9.70",
      'value: "${GitHub__ClientId}"',
      "# comment mentioning $HOME should not be touched",
    ].join("\n"),
  );
});
