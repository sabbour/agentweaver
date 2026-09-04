import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  ensureRepoAppPrivateKeySecret,
  REPO_APP_PRIVATE_KEY_SECRET,
  setSecretFileWithRetry,
} from "../lib/repo-app-secret.mjs";

function noopLog() {
  return { info() {}, ok() {}, warn() {} };
}

function fakeExec(handler) {
  const calls = [];
  return {
    calls,
    async capture(cmd, args, opts) {
      calls.push({ cmd, args, opts });
      return handler(cmd, args, opts, calls) ?? { stdout: "", stderr: "", code: 0 };
    },
  };
}

function requestedSecret(args) {
  const index = args.indexOf("--name");
  return index >= 0 ? args[index + 1] : "";
}

test("Repo App secret contract keeps the application logical name distinct from the physical Key Vault name", () => {
  assert.deepEqual(REPO_APP_PRIVATE_KEY_SECRET, {
    logicalName: "repo-app-private-key",
    physicalName: "ghtok-repo-app-private-key",
    legacyPhysicalName: "repo-app-private-key",
  });
});

test("ensureRepoAppPrivateKeySecret accepts an accessible canonical secret without mutation", async () => {
  const exec = fakeExec((_cmd, args) => {
    assert.equal(requestedSecret(args), REPO_APP_PRIVATE_KEY_SECRET.physicalName);
    return { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 };
  });

  const result = await ensureRepoAppPrivateKeySecret(
    { vaultName: "kv" },
    { exec, log: noopLog() },
  );

  assert.equal(result.status, "available");
  assert.equal(exec.calls.some((call) => call.args.includes("set")), false);
  assert.equal(exec.calls.some((call) => call.args.includes("download")), false);
});

test("ensureRepoAppPrivateKeySecret migrates the legacy secret and preserves it", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-test-"));
  let canonicalAvailable = false;
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    if (operation === "show-deleted") {
      assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      return { stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.physicalName) {
      return canonicalAvailable
        ? { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 }
        : { stdout: "", stderr: "ERROR: (SecretNotFound) secret was not found in this key vault", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName) {
      return { stdout: "https://kv/secrets/repo-app-private-key/version", stderr: "", code: 0 };
    }
    if (operation === "download") {
      const filePath = args[args.indexOf("--file") + 1];
      fs.writeFileSync(filePath, "private-key-material");
      return { stdout: "", stderr: "", code: 0 };
    }
    if (operation === "set") {
      const filePath = args[args.indexOf("--file") + 1];
      assert.equal(fs.readFileSync(filePath, "utf8"), "private-key-material");
      assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      canonicalAvailable = true;
      return { stdout: "", stderr: "", code: 0 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  try {
    const result = await ensureRepoAppPrivateKeySecret(
      { vaultName: "kv" },
      { exec, log: noopLog(), scratchRoot },
    );

    assert.equal(result.status, "migrated");
    assert.equal(exec.calls.some((call) => call.args.includes("delete")), false);
    assert.equal(exec.calls.some((call) => call.args.includes("recover")), false);
    assert.deepEqual(fs.readdirSync(scratchRoot), []);
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});

test("ensureRepoAppPrivateKeySecret fails clearly when canonical and legacy secrets are missing", async () => {
  const exec = fakeExec(() => ({
    stdout: "",
    stderr: "ERROR: (SecretNotFound) secret was not found in this key vault",
    code: 3,
  }));

  await assert.rejects(
    ensureRepoAppPrivateKeySecret(
      { vaultName: "kv" },
      { exec, log: noopLog() },
    ),
    /REPO_APP_PRIVATE_KEY_FILE|--repo-app-private-key-file/,
  );
  assert.equal(exec.calls.some((call) => call.args.includes("set")), false);
  assert.equal(exec.calls.some((call) => call.args.includes("recover")), false);
});

test("ensureRepoAppPrivateKeySecret does not mask canonical access failures with legacy fallback", async () => {
  const exec = fakeExec(() => ({
    stdout: "",
    stderr: "ERROR: (ForbiddenByRbac) caller is not authorized",
    code: 1,
  }));

  await assert.rejects(
    ensureRepoAppPrivateKeySecret(
      { vaultName: "kv" },
      { exec, log: noopLog() },
    ),
    /canonical.*inaccessible/i,
  );
  assert.equal(exec.calls.length, 1);
  assert.equal(exec.calls.some((call) => call.args.includes("show-deleted")), false);
  assert.equal(exec.calls.some((call) => call.args.includes("recover")), false);
});

test("ensureRepoAppPrivateKeySecret does not mask deleted-secret inspection failures with legacy fallback", async () => {
  const exec = fakeExec((_cmd, args) => {
    if (args[2] === "show") {
      return { stdout: "", stderr: "ERROR: (SecretNotFound) active secret was not found", code: 3 };
    }
    if (args[2] === "show-deleted") {
      return { stdout: "", stderr: "ERROR: (ForbiddenByRbac) caller cannot inspect deleted secrets", code: 1 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  await assert.rejects(
    ensureRepoAppPrivateKeySecret(
      { vaultName: "kv" },
      { exec, log: noopLog() },
    ),
    /canonical.*inaccessible/i,
  );
  assert.equal(exec.calls.length, 2);
  assert.equal(exec.calls.some((call) =>
    requestedSecret(call.args) === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName), false);
  assert.equal(exec.calls.some((call) => call.args.includes("recover")), false);
});

test("ensureRepoAppPrivateKeySecret rechecks canonical after another process recovers it during deleted inspection", async () => {
  let activeChecks = 0;
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
    if (operation === "show") {
      activeChecks += 1;
      return activeChecks === 1
        ? { stdout: "", stderr: "ERROR: (SecretNotFound) active secret was not found", code: 3 }
        : { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 };
    }
    if (operation === "show-deleted") {
      return { stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found", code: 3 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  const result = await ensureRepoAppPrivateKeySecret(
    { vaultName: "kv" },
    { exec, log: noopLog() },
  );

  assert.equal(result.status, "available");
  assert.deepEqual(exec.calls.map((call) => call.args[2]), ["show", "show-deleted", "show"]);
  assert.equal(exec.calls.some((call) =>
    requestedSecret(call.args) === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName), false);
  assert.equal(exec.calls.some((call) => ["recover", "set", "download"].includes(call.args[2])), false);
});

test("ensureRepoAppPrivateKeySecret imports a configured PEM file directly to the canonical name", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-import-"));
  const sourceFile = path.join(scratchRoot, "repo-app.pem");
  fs.writeFileSync(sourceFile, "private-key-material");
  const exec = fakeExec((_cmd, args) => {
    if (args[2] === "set") {
      assert.equal(requestedSecret(args), REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      assert.equal(args[args.indexOf("--file") + 1], sourceFile);
    }
    return { stdout: "verified", stderr: "", code: 0 };
  });

  try {
    const result = await ensureRepoAppPrivateKeySecret(
      { vaultName: "kv", sourceFile },
      { exec, log: noopLog() },
    );
    assert.equal(result.status, "imported");
    assert.equal(exec.calls.some((call) =>
      requestedSecret(call.args) === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName), false);
    assert.equal(exec.calls.some((call) => call.args.includes("show-deleted")), false);
    assert.equal(exec.calls.some((call) => call.args.includes("recover")), false);
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});

test("setSecretFileWithRetry retries bounded RBAC propagation failures", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-retry-"));
  const sourceFile = path.join(scratchRoot, "repo-app.pem");
  fs.writeFileSync(sourceFile, "private-key-material");
  let attempts = 0;
  const exec = fakeExec(() => {
    attempts += 1;
    return attempts === 1
      ? { stdout: "", stderr: "ForbiddenByRbac", code: 1 }
      : { stdout: "", stderr: "", code: 0 };
  });

  try {
    await setSecretFileWithRetry("kv", "secret", sourceFile, {
      exec,
      log: noopLog(),
      sleep: async () => {},
    });
    assert.equal(attempts, 2);
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});

test("ensureRepoAppPrivateKeySecret recovers a canonical-only soft-deleted secret before legacy fallback", async () => {
  let activeChecks = 0;
  const messages = [];
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
    if (operation === "show") {
      activeChecks += 1;
      return activeChecks < 3
        ? { stdout: "", stderr: "ERROR: (SecretNotFound) active secret was not found", code: 3 }
        : { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 };
    }
    if (operation === "show-deleted") {
      return { stdout: "https://kv/deletedsecrets/ghtok-repo-app-private-key", stderr: "", code: 0 };
    }
    if (operation === "recover") {
      return { stdout: "", stderr: "", code: 0 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  const result = await ensureRepoAppPrivateKeySecret(
    { vaultName: "kv" },
    {
      exec,
      log: {
        ...noopLog(),
        info: (message) => messages.push(message),
        ok: (message) => messages.push(message),
      },
      sleep: async () => {},
    },
  );

  assert.equal(result.status, "recovered");
  assert.equal(exec.calls.filter((call) => call.args[2] === "show-deleted").length, 1);
  assert.equal(exec.calls.filter((call) => call.args[2] === "recover").length, 1);
  assert.equal(exec.calls.some((call) =>
    requestedSecret(call.args) === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName), false);
  assert.equal(exec.calls.some((call) => ["set", "download"].includes(call.args[2])), false);
  assert.equal(messages.some((message) => /private-key-material|BEGIN .* PRIVATE KEY/.test(message)), false);
});

test("ensureRepoAppPrivateKeySecret polls canonical when recovery loses a missing or already-active race", async (t) => {
  for (const [name, recoveryError] of [
    ["recover reports SecretNotFound", "ERROR: (SecretNotFound) deleted secret was not found"],
    ["recover reports already active", "ERROR: secret has already been recovered and is already active"],
  ]) {
    await t.test(name, async () => {
      let activeChecks = 0;
      let sleepCalls = 0;
      const exec = fakeExec((_cmd, args) => {
        const operation = args[2];
        const secretName = requestedSecret(args);
        assert.equal(secretName, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
        if (operation === "show") {
          activeChecks += 1;
          return activeChecks < 3
            ? { stdout: "", stderr: "ERROR: (SecretNotFound) active secret was not found", code: 3 }
            : { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 };
        }
        if (operation === "show-deleted") {
          return { stdout: "https://kv/deletedsecrets/ghtok-repo-app-private-key", stderr: "", code: 0 };
        }
        if (operation === "recover") {
          return { stdout: "", stderr: recoveryError, code: 3 };
        }
        throw new Error(`Unexpected command: ${args.join(" ")}`);
      });

      const result = await ensureRepoAppPrivateKeySecret(
        { vaultName: "kv" },
        {
          exec,
          log: noopLog(),
          sleep: async () => {
            sleepCalls += 1;
          },
        },
      );

      assert.equal(result.status, "recovered");
      assert.equal(exec.calls.filter((call) => call.args[2] === "recover").length, 1);
      assert.equal(exec.calls.filter((call) => call.args[2] === "show").length, 3);
      assert.equal(sleepCalls, 1);
      assert.equal(exec.calls.some((call) =>
        requestedSecret(call.args) === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName), false);
      assert.equal(exec.calls.some((call) => ["set", "download"].includes(call.args[2])), false);
    });
  }
});

test("ensureRepoAppPrivateKeySecret fails closed when canonical recovery is inaccessible", async () => {
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    assert.equal(requestedSecret(args), REPO_APP_PRIVATE_KEY_SECRET.physicalName);
    if (operation === "show") {
      return { stdout: "", stderr: "ERROR: (SecretNotFound) active secret was not found", code: 3 };
    }
    if (operation === "show-deleted") {
      return { stdout: "https://kv/deletedsecrets/ghtok-repo-app-private-key", stderr: "", code: 0 };
    }
    if (operation === "recover") {
      return { stdout: "", stderr: "ERROR: (ForbiddenByRbac) caller cannot recover secrets", code: 1 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  await assert.rejects(
    ensureRepoAppPrivateKeySecret(
      { vaultName: "kv" },
      { exec, log: noopLog(), sleep: async () => {} },
    ),
    /failed to recover.*ForbiddenByRbac/i,
  );
  assert.equal(exec.calls.filter((call) => call.args[2] === "recover").length, 1);
  assert.equal(exec.calls.some((call) =>
    requestedSecret(call.args) === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName), false);
});

test("ensureRepoAppPrivateKeySecret recovers a soft-deleted canonical secret before configured-file import", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-recover-import-"));
  const sourceFile = path.join(scratchRoot, "repo-app.pem");
  const secretValue = "configured-private-key-material";
  fs.writeFileSync(sourceFile, secretValue);
  let setAttempts = 0;
  let showAttempts = 0;
  const messages = [];
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    if (operation === "set") {
      setAttempts += 1;
      assert.equal(args[args.indexOf("--file") + 1], sourceFile);
      return setAttempts === 1
        ? { stdout: "", stderr: "ERROR: (ObjectIsDeletedButRecoverable) Secret is deleted but recoverable.", code: 1 }
        : { stdout: "", stderr: "", code: 0 };
    }
    if (operation === "recover") {
      assert.equal(requestedSecret(args), REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      return { stdout: "", stderr: "", code: 0 };
    }
    if (operation === "show") {
      showAttempts += 1;
      return showAttempts === 1
        ? { stdout: "", stderr: "ERROR: (SecretNotFound) recovery is not visible yet", code: 3 }
        : { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  try {
    const result = await ensureRepoAppPrivateKeySecret(
      { vaultName: "kv", sourceFile },
      {
        exec,
        log: { ...noopLog(), info: (message) => messages.push(message) },
        sleep: async () => {},
      },
    );

    assert.equal(result.status, "imported");
    assert.equal(setAttempts, 2);
    assert.ok(exec.calls.some((call) => call.args[2] === "recover"));
    assert.equal(exec.calls.some((call) => call.args.includes(secretValue)), false);
    assert.equal(messages.some((message) => message.includes(secretValue)), false);
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});

test("ensureRepoAppPrivateKeySecret recovers a soft-deleted canonical secret during legacy migration", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-recover-migration-"));
  const secretValue = "legacy-private-key-material";
  let canonicalChecks = 0;
  let setAttempts = 0;
  const messages = [];
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    if (operation === "show-deleted") {
      assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      return { stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.physicalName) {
      canonicalChecks += 1;
      if (canonicalChecks === 1) {
        return { stdout: "", stderr: "ERROR: (SecretNotFound) canonical secret is absent", code: 3 };
      }
      if (canonicalChecks === 2) {
        return { stdout: "", stderr: "ERROR: (SecretNotFound) recovery is not visible yet", code: 3 };
      }
      return { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName) {
      return { stdout: "https://kv/secrets/repo-app-private-key/version", stderr: "", code: 0 };
    }
    if (operation === "download") {
      fs.writeFileSync(args[args.indexOf("--file") + 1], secretValue);
      return { stdout: "", stderr: "", code: 0 };
    }
    if (operation === "set") {
      setAttempts += 1;
      assert.equal(fs.readFileSync(args[args.indexOf("--file") + 1], "utf8"), secretValue);
      return setAttempts === 1
        ? { stdout: "", stderr: "ERROR: (ObjectIsDeletedButRecoverable) Secret is deleted but recoverable.", code: 1 }
        : { stdout: "", stderr: "", code: 0 };
    }
    if (operation === "recover") {
      assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      return { stdout: "", stderr: "", code: 0 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  try {
    const result = await ensureRepoAppPrivateKeySecret(
      { vaultName: "kv" },
      {
        exec,
        log: { ...noopLog(), info: (message) => messages.push(message) },
        scratchRoot,
        sleep: async () => {},
      },
    );

    assert.equal(result.status, "migrated");
    assert.equal(setAttempts, 2);
    assert.ok(exec.calls.some((call) => call.args[2] === "recover"));
    assert.equal(exec.calls.some((call) => call.args.includes(secretValue)), false);
    assert.equal(messages.some((message) => message.includes(secretValue)), false);
    assert.deepEqual(fs.readdirSync(scratchRoot), []);
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});
