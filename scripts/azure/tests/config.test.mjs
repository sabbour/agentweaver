// config.test.mjs -- precedence tests for lib/config.mjs
// (flags > env > params-file > detected-defaults > prompt; prompt only fills gaps).

import test from "node:test";
import assert from "node:assert/strict";
import { resolveConfig, resolveRawValue, parseJsonc } from "../lib/config.mjs";
import { NonInteractiveError } from "../lib/prompt.mjs";

test("resolveRawValue: flag wins over env, params-file, and default", () => {
  const spec = { env: "FOO", default: "default-value" };
  const result = resolveRawValue(
    "foo",
    spec,
    { flags: { foo: "flag-value" }, env: { FOO: "env-value" }, paramsFile: { foo: "params-value" } },
  );
  assert.deepEqual(result, { value: "flag-value", source: "flag" });
});

test("resolveRawValue: env wins over params-file and default when no flag", () => {
  const spec = { env: "FOO", default: "default-value" };
  const result = resolveRawValue(
    "foo",
    spec,
    { flags: {}, env: { FOO: "env-value" }, paramsFile: { foo: "params-value" } },
  );
  assert.deepEqual(result, { value: "env-value", source: "env" });
});

test("resolveRawValue: params-file wins over default when no flag/env", () => {
  const spec = { env: "FOO", default: "default-value" };
  const result = resolveRawValue(
    "foo",
    spec,
    { flags: {}, env: {}, paramsFile: { foo: "params-value" } },
  );
  assert.deepEqual(result, { value: "params-value", source: "params-file" });
});

test("resolveRawValue: falls back to default when nothing else set", () => {
  const spec = { env: "FOO", default: "default-value" };
  const result = resolveRawValue("foo", spec, { flags: {}, env: {}, paramsFile: {} });
  assert.deepEqual(result, { value: "default-value", source: "default" });
});

test("resolveRawValue: empty-string env value does not shadow params-file/default (matches bash [-z] checks)", () => {
  const spec = { env: "FOO", default: "default-value" };
  const result = resolveRawValue(
    "foo",
    spec,
    { flags: {}, env: { FOO: "" }, paramsFile: { foo: "params-value" } },
  );
  assert.deepEqual(result, { value: "params-value", source: "params-file" });
});

test("resolveConfig: full precedence chain across multiple fields", async () => {
  const schema = {
    resourceGroup: { env: "RESOURCE_GROUP", default: "agentweaver-rg" },
    location: { env: "LOCATION", default: "westus2" },
    clusterName: { env: "CLUSTER_NAME", default: "agentweaver-aks-2" },
  };
  const config = await resolveConfig(schema, {
    flags: { resourceGroup: "flag-rg" },
    env: { LOCATION: "eastus" },
    paramsFile: { clusterName: "params-cluster" },
  });
  assert.equal(config.resourceGroup, "flag-rg");
  assert.equal(config.location, "eastus");
  assert.equal(config.clusterName, "params-cluster");
});

test("resolveConfig: prompt only fills gaps, never overrides a resolved value", async () => {
  let promptCalls = 0;
  const schema = {
    resourceGroup: {
      env: "RESOURCE_GROUP",
      default: "agentweaver-rg",
      required: true,
      prompt: async () => {
        promptCalls += 1;
        return "should-not-be-used";
      },
    },
  };
  const config = await resolveConfig(schema, { flags: {}, env: {}, paramsFile: {} });
  assert.equal(config.resourceGroup, "agentweaver-rg");
  assert.equal(promptCalls, 0, "prompt must not run when a default already resolved the field");
});

test("resolveConfig: prompt is invoked only when the field is otherwise unresolved and required", async () => {
  let promptCalls = 0;
  const schema = {
    subscription: {
      env: "AZURE_SUBSCRIPTION",
      required: true,
      prompt: async () => {
        promptCalls += 1;
        return "prompted-subscription";
      },
    },
  };
  const config = await resolveConfig(schema, { flags: {}, env: {}, paramsFile: {} });
  assert.equal(config.subscription, "prompted-subscription");
  assert.equal(promptCalls, 1);
});

test("resolveConfig: missing required field with no prompt function throws", async () => {
  const schema = { subscription: { env: "AZURE_SUBSCRIPTION", required: true } };
  await assert.rejects(
    () => resolveConfig(schema, { flags: {}, env: {}, paramsFile: {} }),
    /Missing required config 'subscription'/,
  );
});

test("resolveConfig: NonInteractiveError from prompt is surfaced as a clear non-interactive message", async () => {
  const schema = {
    subscription: {
      env: "AZURE_SUBSCRIPTION",
      required: true,
      prompt: async () => {
        throw new NonInteractiveError("Select a subscription");
      },
    },
  };
  await assert.rejects(
    () => resolveConfig(schema, { flags: {}, env: {}, paramsFile: {} }),
    /no interactive TTY is available/,
  );
});

test("resolveConfig: validators run after all fields resolve, and can fail the whole resolution", async () => {
  const schema = {
    imageTag: {
      env: "IMAGE_TAG",
      default: "latest",
      validate: (value) => (value === "latest" ? "must not be 'latest'" : undefined),
    },
  };
  await assert.rejects(
    () => resolveConfig(schema, { flags: {}, env: {}, paramsFile: {} }),
    /imageTag: must not be 'latest'/,
  );
});

test("resolveConfig: parse() coerces the resolved value regardless of source", async () => {
  const schema = {
    replicaCount: { env: "REPLICA_COUNT", default: "3", parse: (v) => Number.parseInt(v, 10) },
  };
  const config = await resolveConfig(schema, { flags: {}, env: { REPLICA_COUNT: "5" }, paramsFile: {} });
  assert.equal(config.replicaCount, 5);
  assert.equal(typeof config.replicaCount, "number");
});

test("parseJsonc: tolerates // line comments, /* block */ comments, and trailing commas", () => {
  const text = `{
    // resource group
    "resourceGroup": "agentweaver-rg",
    /* cluster name */
    "clusterName": "agentweaver-aks-2",
    "tags": ["a", "b",],
  }`;
  const parsed = parseJsonc(text);
  assert.deepEqual(parsed, {
    resourceGroup: "agentweaver-rg",
    clusterName: "agentweaver-aks-2",
    tags: ["a", "b"],
  });
});

test("parseJsonc: plain JSON (no comments) parses unchanged", () => {
  const text = '{"a": 1, "b": "two", "c": ["//not-a-comment"]}';
  const parsed = parseJsonc(text);
  assert.deepEqual(parsed, { a: 1, b: "two", c: ["//not-a-comment"] });
});
