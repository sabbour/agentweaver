// verify-step.test.mjs -- Tests for steps/40-verify.mjs: pod-count tallying,
// gateway/httproute checks, the authenticated HTTP feature probe (fetch
// stubbed), SecretProviderClass/RBAC/sandbox/storage checks, and the overall
// pass/fail summary. All kubectl/fetch calls are injected fakes -- no real
// cluster or network access.

import test from "node:test";
import assert from "node:assert/strict";
import {
  run,
  runningPodCount,
  kubectlOk,
  httpStatus,
  httpJson,
  httpDiscoveryJson,
  firstProjectId,
} from "../steps/40-verify.mjs";

const CFG = Object.freeze({ NAMESPACE: "agentweaver" });

function noopLog() {
  const rec = () => () => {};
  return { info: rec(), section: rec(), field: rec(), ok: rec(), skip: rec(), warn: rec(), error: rec(), debug: rec(), command: rec() };
}

function fakeExec(captureImpl) {
  return {
    async capture(cmd, args, opts) {
      return captureImpl(cmd, args, opts) ?? { stdout: "", stderr: "", code: 0 };
    },
    async run() {
      return { code: 0 };
    },
  };
}

test("runningPodCount: counts non-empty lines", async () => {
  const exec = fakeExec(() => ({ stdout: "pod-a\npod-b\n", stderr: "", code: 0 }));
  assert.equal(await runningPodCount("agentweaver", "app=agentweaver-api", { exec }), 2);
});

test("runningPodCount: 0 when kubectl fails", async () => {
  const exec = fakeExec(() => ({ stdout: "", stderr: "err", code: 1 }));
  assert.equal(await runningPodCount("agentweaver", "app=agentweaver-api", { exec }), 0);
});

test("kubectlOk: true/false on exit code", async () => {
  const execOk = fakeExec(() => ({ stdout: "", stderr: "", code: 0 }));
  const execFail = fakeExec(() => ({ stdout: "", stderr: "", code: 1 }));
  assert.equal(await kubectlOk(["get", "x"], { exec: execOk }), true);
  assert.equal(await kubectlOk(["get", "x"], { exec: execFail }), false);
});

test("httpStatus: returns numeric status", async () => {
  const fetchImpl = async () => ({ status: 401 });
  assert.equal(await httpStatus("https://example.test/api/projects", { fetchImpl }), 401);
});

test("httpStatus: returns '000' on network failure", async () => {
  const fetchImpl = async () => {
    throw new Error("network down");
  };
  assert.equal(await httpStatus("https://example.test/api/projects", { fetchImpl }), "000");
});

test("httpJson: returns [] on non-ok response", async () => {
  const fetchImpl = async () => ({ ok: false, status: 500 });
  assert.deepEqual(await httpJson("https://example.test/api/projects", "tok", { fetchImpl }), []);
});

test("httpDiscoveryJson: reads anonymous metadata and fails closed", async () => {
  const expected = { issuer: "https://example.test/" };
  assert.deepEqual(
    await httpDiscoveryJson("https://example.test/.well-known/openid-configuration", {
      fetchImpl: async () => ({ ok: true, json: async () => expected }),
    }),
    expected,
  );
  assert.equal(
    await httpDiscoveryJson("https://example.test/.well-known/openid-configuration", {
      fetchImpl: async () => ({ ok: false }),
    }),
    null,
  );
});

test("firstProjectId: handles array/projects/items shapes and id/projectId keys", () => {
  assert.equal(firstProjectId([{ id: "p1" }]), "p1");
  assert.equal(firstProjectId({ projects: [{ projectId: "p2" }] }), "p2");
  assert.equal(firstProjectId({ items: [{ id: "p3" }] }), "p3");
  assert.equal(firstProjectId({}), "");
  assert.equal(firstProjectId([]), "");
});

test("run: all-pass scenario reports ok:true with no HOST-dependent checks skipped incorrectly", async () => {
  const captureImpl = (cmd, args) => {
    const joined = args.join(" ");
    if (joined.includes("--field-selector=status.phase=Running")) return { stdout: "pod-1\n", stderr: "", code: 0 };
    if (joined.includes('gateway agentweaver-gateway') && joined.includes("Programmed")) return { stdout: "True", stderr: "", code: 0 };
    if (joined.includes("gateway agentweaver-gateway") && joined.includes("addresses")) return { stdout: "1.2.3.4", stderr: "", code: 0 };
    if (joined.includes("agentweaver-preview-gateway")) return { stdout: "True", stderr: "", code: 0 };
    if (joined.includes("httproute")) return { stdout: "True", stderr: "", code: 0 };
    if (joined.includes("defaultdomaincertificate")) return { stdout: "", stderr: "", code: 0 }; // no domain -> skip HTTP checks
    if (joined.includes("secretproviderclasspodstatus")) return { stdout: "spc-1\n", stderr: "", code: 0 };
    if (joined.includes("secretproviderclass")) return { stdout: "", stderr: "", code: 0 };
    if (joined.includes("auth can-i")) return { stdout: "yes", stderr: "", code: 0 };
    if (joined.includes("agentweaver-sandbox")) return { stdout: "", stderr: "", code: 1 }; // legacy template/warmpool: absent
    return { stdout: "", stderr: "", code: 0 }; // every other `get X` check: exists (exit 0)
  };
  const exec = fakeExec(captureImpl);
  const result = await run(CFG, { exec, log: noopLog(), env: {} });
  assert.equal(result.fail, 0);
  assert.equal(result.ok, true);
  assert.ok(result.pass > 0);
});

test("run: probes pods/exec create via --subresource=exec, not the deprecated slash form", async () => {
  const canIArgs = [];
  const captureImpl = (cmd, args) => {
    const joined = args.join(" ");
    if (joined.includes("auth can-i")) {
      canIArgs.push(joined);
      return { stdout: "yes", stderr: "", code: 0 };
    }
    if (joined.includes("--field-selector=status.phase=Running")) return { stdout: "pod-1\n", stderr: "", code: 0 };
    if (joined.includes("Programmed") || joined.includes("Accepted") || joined.includes("ResolvedRefs")) return { stdout: "True", stderr: "", code: 0 };
    if (joined.includes("addresses")) return { stdout: "1.2.3.4", stderr: "", code: 0 };
    if (joined.includes("defaultdomaincertificate")) return { stdout: "", stderr: "", code: 0 };
    if (joined.includes("secretproviderclasspodstatus")) return { stdout: "spc-1\n", stderr: "", code: 0 };
    if (joined.includes("agentweaver-sandbox")) return { stdout: "", stderr: "", code: 1 };
    return { stdout: "", stderr: "", code: 0 };
  };
  const exec = fakeExec(captureImpl);
  await run(CFG, { exec, log: noopLog(), env: {} });
  const execProbes = canIArgs.filter((a) => a.includes("--subresource=exec"));
  assert.ok(execProbes.length >= 2, "both API and worker SAs must probe pods/exec via --subresource=exec");
  for (const probe of execProbes) {
    assert.ok(
      /\bcreate pods\b/.test(probe) && probe.includes("--subresource=exec"),
      `expected 'create pods ... --subresource=exec', got: ${probe}`,
    );
  }
  assert.ok(
    !canIArgs.some((a) => /\bpods\/exec\b/.test(a)),
    "must not use the deprecated 'pods/exec' slash form (kubectl >=1.33 returns a false 'no')",
  );
});

test("run: reports failures for missing pods and unprogrammed gateway", async () => {
  const captureImpl = (cmd, args) => {
    const joined = args.join(" ");
    if (joined.includes("--field-selector=status.phase=Running")) return { stdout: "", stderr: "", code: 0 }; // no running pods
    if (joined.includes("Programmed")) return { stdout: "False", stderr: "", code: 0 };
    if (joined.includes("addresses")) return { stdout: "", stderr: "", code: 0 };
    if (joined.includes("Accepted") || joined.includes("ResolvedRefs")) return { stdout: "False", stderr: "", code: 0 };
    if (joined.includes("defaultdomaincertificate")) return { stdout: "", stderr: "", code: 0 };
    return { stdout: "", stderr: "", code: 1 }; // every other `get X` check: missing
  };
  const exec = fakeExec(captureImpl);
  const result = await run(CFG, { exec, log: noopLog(), env: {} });
  assert.equal(result.ok, false);
  assert.ok(result.fail > 0);
});

test("run: performs authenticated feature checks when HOST resolves and a token is supplied", async () => {
  const captureImpl = (cmd, args) => {
    const joined = args.join(" ");
    if (joined.includes("--field-selector=status.phase=Running")) return { stdout: "pod-1\n", stderr: "", code: 0 };
    if (joined.includes("Programmed") || joined.includes("Accepted") || joined.includes("ResolvedRefs")) return { stdout: "True", stderr: "", code: 0 };
    if (joined.includes("addresses")) return { stdout: "1.2.3.4", stderr: "", code: 0 };
    if (joined.includes("defaultdomaincertificate")) return { stdout: "*.westus2.cloudapp.azure.com", stderr: "", code: 0 };
    if (joined.includes("agentweaver-runtime-config") && joined.includes("OAUTH_PUBLIC_ORIGIN")) {
      return { stdout: "https://agentweaver.westus2.cloudapp.azure.com", stderr: "", code: 0 };
    }
    if (joined.includes("agentweaver-runtime-config") && joined.includes("OAUTH_SIGNING_CERTIFICATE_NAME")) {
      return { stdout: "agentweaver-oauth-signing", stderr: "", code: 0 };
    }
    if (joined.includes("agentweaver-runtime-config") && joined.includes("OAUTH_ENCRYPTION_CERTIFICATE_NAME")) {
      return { stdout: "agentweaver-oauth-encryption", stderr: "", code: 0 };
    }
    if (joined.includes("secretproviderclasspodstatus")) return { stdout: "spc-1\n", stderr: "", code: 0 };
    if (joined.includes("auth can-i")) return { stdout: "yes", stderr: "", code: 0 };
    if (joined.includes("agentweaver-sandbox")) return { stdout: "", stderr: "", code: 1 }; // legacy template/warmpool: absent
    return { stdout: "", stderr: "", code: 0 };
  };
  const exec = fakeExec(captureImpl);
  const calledUrls = [];
  const fetchImpl = async (url, init) => {
    calledUrls.push(url);
    const origin = "https://agentweaver.westus2.cloudapp.azure.com";
    if (url.includes("oauth-protected-resource")) {
      return {
        status: 200,
        ok: true,
        json: async () => ({
          resource: `${origin}/mcp`,
          authorization_servers: [`${origin}/`],
          scopes_supported: ["mcp:invoke"],
        }),
      };
    }
    if (url.endsWith("/.well-known/oauth-authorization-server")
      || url.endsWith("/.well-known/openid-configuration")) {
      return {
        status: 200,
        ok: true,
        json: async () => ({ issuer: `${origin}/`, jwks_uri: `${origin}/oauth/jwks` }),
      };
    }
    if (url.endsWith("/oauth/jwks")) {
      return {
        status: 200,
        ok: true,
        json: async () => ({ keys: [{ use: "sig", alg: "RS256", kid: "key-1" }] }),
      };
    }
    if (url.endsWith("/api/projects") && !init.headers?.Authorization) return { status: 401, ok: false, json: async () => [] };
    if (url.endsWith("/api/auth/github")) return { status: 200, ok: true, json: async () => ({}) };
    if (url.endsWith("/api/projects")) return { status: 200, ok: true, json: async () => [{ id: "proj-1" }] };
    return { status: 200, ok: true, json: async () => ({}) };
  };
  const result = await run(CFG, { exec, log: noopLog(), env: { AGENTWEAVER_VALIDATION_TOKEN: "tok" }, fetchImpl });
  assert.ok(calledUrls.some((u) => u.includes("/api/projects/proj-1/memory")));
  assert.equal(result.ok, true);
});

test("run: verifies configured OAuth certificate families and enabled Key Vault versions", async () => {
  const captureImpl = (_cmd, args) => {
    const joined = args.join(" ");
    if (joined.includes("--field-selector=status.phase=Running")) return { stdout: "pod-1\n", stderr: "", code: 0 };
    if (joined.includes("Programmed") || joined.includes("Accepted") || joined.includes("ResolvedRefs")) return { stdout: "True", stderr: "", code: 0 };
    if (joined.includes("addresses")) return { stdout: "1.2.3.4", stderr: "", code: 0 };
    if (joined.includes("defaultdomaincertificate")) return { stdout: "*.example.test", stderr: "", code: 0 };
    if (joined.includes("OAUTH_PUBLIC_ORIGIN")) return { stdout: "https://agentweaver.example.test", stderr: "", code: 0 };
    if (joined.includes("OAUTH_SIGNING_CERTIFICATE_NAME")) return { stdout: "oauth-signing-custom", stderr: "", code: 0 };
    if (joined.includes("OAUTH_ENCRYPTION_CERTIFICATE_NAME")) return { stdout: "oauth-encryption-custom", stderr: "", code: 0 };
    if (joined.includes("keyvault certificate show")) return { stdout: "", stderr: "", code: 0 };
    if (joined.includes("keyvault secret list-versions")) return { stdout: "2", stderr: "", code: 0 };
    if (joined.includes("secretproviderclasspodstatus")) return { stdout: "spc-1\n", stderr: "", code: 0 };
    if (joined.includes("auth can-i")) return { stdout: "yes", stderr: "", code: 0 };
    if (joined.includes("agentweaver-sandbox")) return { stdout: "", stderr: "", code: 1 };
    return { stdout: "", stderr: "", code: 0 };
  };
  const fetchImpl = async (url) => {
    const origin = "https://agentweaver.example.test";
    if (url.includes("oauth-protected-resource")) return {
      ok: true, json: async () => ({ resource: `${origin}/mcp`, authorization_servers: [`${origin}/`], scopes_supported: ["mcp:invoke"] }),
    };
    if (url.endsWith("/oauth/jwks")) return { ok: true, json: async () => ({ keys: [{ use: "sig", alg: "RS256", kid: "active" }, { use: "sig", alg: "RS256", kid: "previous" }] }) };
    if (url.includes(".well-known")) return { ok: true, json: async () => ({ issuer: `${origin}/`, jwks_uri: `${origin}/oauth/jwks` }) };
    return { status: 401, ok: false, json: async () => ({}) };
  };
  const result = await run({
    NAMESPACE: "agentweaver",
    KEYVAULT_NAME: "test-kv",
    OAUTH_SIGNING_CERTIFICATE_NAME: "oauth-signing-custom",
    OAUTH_ENCRYPTION_CERTIFICATE_NAME: "oauth-encryption-custom",
  }, { exec: fakeExec(captureImpl), log: noopLog(), env: {}, fetchImpl });
  assert.equal(result.ok, true);
  assert.ok(result.results.some((entry) => entry.message.includes("runtime retains the newest two usable versions")));
});
