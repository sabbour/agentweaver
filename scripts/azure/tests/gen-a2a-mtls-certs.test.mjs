// gen-a2a-mtls-certs.test.mjs -- unit tests for the A2A mTLS secret
// existence probes and generation decisions. All kubectl and openssl calls
// are stubbed.

import test from "node:test";
import assert from "node:assert/strict";
import {
  run as runGenA2aMtlsCerts,
  secretReadState,
} from "../steps/gen-a2a-mtls-certs.mjs";

const CFG = Object.freeze({
  NAMESPACE: "agentweaver",
  AGENTWEAVER_TMP_DIR: "C:\\agentweaver\\tmp",
  repoRoot: "C:\\fake\\repo",
});

const SECRET_NAMES = Object.freeze(["agentweaver-a2a-ca", "agentweaver-a2a-server-tls", "agentweaver-a2a-client-tls"]);

function fakeExec({ captureImpl, runImpl } = {}) {
  const calls = { capture: [], run: [] };
  return {
    calls,
    async capture(cmd, args, opts) {
      calls.capture.push({ cmd, args, opts });
      if (captureImpl) return captureImpl(cmd, args, opts);
      return { stdout: "", stderr: "", code: 0 };
    },
    async run(cmd, args, opts) {
      calls.run.push({ cmd, args, opts });
      if (runImpl) return runImpl(cmd, args, opts);
      return { code: 0 };
    },
  };
}

function fakeFs() {
  const calls = { rmSync: [], mkdirSync: [], writeFileSync: [], readFileSync: [] };
  return {
    calls,
    rmSync(path, opts) {
      calls.rmSync.push({ path, opts });
    },
    mkdirSync(path, opts) {
      calls.mkdirSync.push({ path, opts });
    },
    writeFileSync(path, content) {
      calls.writeFileSync.push({ path, content });
    },
    readFileSync(path) {
      calls.readFileSync.push({ path });
      return Buffer.from(`cert:${path}`);
    },
  };
}

function noopLog() {
  return {
    info() {},
    section() {},
    field() {},
    ok() {},
    warn() {},
  };
}

function kubectlGetSecretName(args) {
  if (args[0] === "get" && args[1] === "secret") return args[2];
  return null;
}

test("secretReadState: exit 1 with NotFound on stderr is absent", async () => {
  const exec = fakeExec({
    captureImpl: async () => ({
      stdout: "",
      stderr: 'Error from server (NotFound): secrets "agentweaver-a2a-ca" not found',
      code: 1,
    }),
  });

  const result = await secretReadState("agentweaver-a2a-ca", "agentweaver", { exec, log: noopLog(), sleep: async () => {} });

  assert.deepEqual(result, { state: "absent" });
  assert.equal(exec.calls.capture.length, 1);
});

test("secretReadState: exit 1 with throttling on stderr is unknown, not absent", async () => {
  const exec = fakeExec({
    captureImpl: async () => ({
      stdout: "",
      stderr: "Error from server (TooManyRequests): request was throttled by the API server",
      code: 1,
    }),
  });

  const result = await secretReadState("agentweaver-a2a-server-tls", "agentweaver", {
    exec,
    log: noopLog(),
    sleep: async () => {},
  });

  assert.equal(result.state, "unknown");
  assert.notEqual(result.state, "absent");
  assert.match(result.reason, /throttled/i);
  assert.equal(exec.calls.capture.length, 3);
});

test("run: one unknown secret read raises a read error, not the partial-state error", async () => {
  const exec = fakeExec({
    captureImpl: async (_cmd, args) => {
      const name = kubectlGetSecretName(args);
      if (name === "agentweaver-a2a-ca") return { stdout: "", stderr: "", code: 0 };
      return {
        stdout: "",
        stderr: "Error from server (Timeout): the request timed out",
        code: 1,
      };
    },
  });

  await assert.rejects(
    () => runGenA2aMtlsCerts(CFG, { exec, fs: fakeFs(), log: noopLog(), sleep: async () => {} }),
    (error) => {
      assert.match(error.message, /Kubernetes Secret read failed/i);
      assert.match(error.message, /State could not be determined/i);
      assert.doesNotMatch(error.message, /Partial A2A mTLS secrets found/);
      assert.doesNotMatch(error.message, /force: true/);
      return true;
    },
  );
});

test("run: three confirmed absences generate all certificates and secrets", async () => {
  const fsImpl = fakeFs();
  const exec = fakeExec({
    captureImpl: async (_cmd, args) => ({
      stdout: "",
      stderr: `Error from server (NotFound): secrets "${kubectlGetSecretName(args)}" not found`,
      code: 1,
    }),
  });

  const result = await runGenA2aMtlsCerts(CFG, { exec, fs: fsImpl, log: noopLog(), sleep: async () => {} });

  assert.deepEqual(result, { skipped: false });
  assert.equal(exec.calls.capture.length, 3);
  assert.equal(exec.calls.run.filter((call) => call.cmd === "kubectl" && call.args[0] === "apply").length, 3);
  assert.equal(exec.calls.run.filter((call) => call.cmd === "openssl").length, 8);
  assert.equal(fsImpl.calls.writeFileSync.filter((call) => call.path.includes("secret-agentweaver-a2a")).length, 3);
});

test("run: three present secrets skip generation", async () => {
  const exec = fakeExec({
    captureImpl: async () => ({ stdout: "NAME TYPE DATA AGE\n", stderr: "", code: 0 }),
  });

  const result = await runGenA2aMtlsCerts(CFG, { exec, fs: fakeFs(), log: noopLog(), sleep: async () => {} });

  assert.deepEqual(result, { skipped: true });
  assert.equal(exec.calls.capture.length, SECRET_NAMES.length);
  assert.equal(exec.calls.run.length, 0);
});

test("run: confirmed partial secret state still raises the existing partial error", async () => {
  const exec = fakeExec({
    captureImpl: async (_cmd, args) => {
      const name = kubectlGetSecretName(args);
      if (name === "agentweaver-a2a-ca") return { stdout: "NAME TYPE DATA AGE\n", stderr: "", code: 0 };
      return {
        stdout: "",
        stderr: `Error from server (NotFound): secrets "${name}" not found`,
        code: 1,
      };
    },
  });

  await assert.rejects(
    () => runGenA2aMtlsCerts(CFG, { exec, fs: fakeFs(), log: noopLog(), sleep: async () => {} }),
    /Partial A2A mTLS secrets found: agentweaver-a2a-ca\. Re-run with force: true to regenerate all three consistently\./,
  );
});

test("secretReadState: a failed probe that succeeds on retry resolves as present", async () => {
  let attempts = 0;
  const exec = fakeExec({
    captureImpl: async () => {
      attempts += 1;
      if (attempts === 1) {
        return { stdout: "", stderr: "Error from server (Timeout): the request timed out", code: 1 };
      }
      return { stdout: "NAME TYPE DATA AGE\n", stderr: "", code: 0 };
    },
  });

  const result = await secretReadState("agentweaver-a2a-client-tls", "agentweaver", {
    exec,
    log: noopLog(),
    sleep: async () => {},
  });

  assert.deepEqual(result, { state: "present" });
  assert.equal(attempts, 2);
});
