import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { generateKeyPairSync } from "node:crypto";
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

function deferred() {
  let resolve;
  const promise = new Promise((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

function generatePrivateKeyPem(type = "pkcs8") {
  return generateKeyPairSync("rsa", {
    modulusLength: 2048,
    privateKeyEncoding: { type, format: "pem" },
    publicKeyEncoding: { type: "spki", format: "pem" },
  }).privateKey;
}

function generateEncryptedPrivateKeyPem() {
  return generateKeyPairSync("rsa", {
    modulusLength: 2048,
    privateKeyEncoding: {
      type: "pkcs8",
      format: "pem",
      cipher: "aes-256-cbc",
      passphrase: "test-only-passphrase",
    },
    publicKeyEncoding: { type: "spki", format: "pem" },
  }).privateKey;
}

function generatePublicKeyPem() {
  return generateKeyPairSync("rsa", {
    modulusLength: 2048,
    privateKeyEncoding: { type: "pkcs8", format: "pem" },
    publicKeyEncoding: { type: "spki", format: "pem" },
  }).publicKey;
}

function generateEcPrivateKeyPem() {
  return generateKeyPairSync("ec", {
    namedCurve: "prime256v1",
    privateKeyEncoding: { type: "pkcs8", format: "pem" },
    publicKeyEncoding: { type: "spki", format: "pem" },
  }).privateKey;
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

test("ensureRepoAppPrivateKeySecret refuses automatic legacy migration without reading or writing secret values", async () => {
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    if (operation === "show-deleted") {
      assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      return { stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.physicalName) {
      return { stdout: "", stderr: "ERROR: (SecretNotFound) secret was not found in this key vault", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName) {
      return { stdout: "https://kv/secrets/repo-app-private-key/version", stderr: "", code: 0 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  await assert.rejects(
    ensureRepoAppPrivateKeySecret(
      { vaultName: "kv" },
      { exec, log: noopLog() },
    ),
    (error) => {
      assert.match(error.message, /automatic legacy migration is disabled/i);
      assert.match(error.message, /az keyvault secret download/);
      assert.match(error.message, /--repo-app-private-key-file/);
      assert.match(error.message, /one-shot/i);
      assert.match(error.message, /unset REPO_APP_PRIVATE_KEY_FILE/);
      assert.match(error.message, /remove it from every params file/i);
      return true;
    },
  );
  assert.equal(exec.calls.some((call) => ["set", "download", "recover"].includes(call.args[2])), false);
});

test("ensureRepoAppPrivateKeySecret uses canonical when it appears after legacy inspection", async () => {
  let canonicalChecks = 0;
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    if (operation === "show-deleted") {
      assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      return { stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.physicalName) {
      canonicalChecks += 1;
      return canonicalChecks < 3
        ? { stdout: "", stderr: "ERROR: (SecretNotFound) canonical secret is absent", code: 3 }
        : { stdout: "https://kv/secrets/ghtok-repo-app-private-key/concurrent", stderr: "", code: 0 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName) {
      return { stdout: "https://kv/secrets/repo-app-private-key/version", stderr: "", code: 0 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  const result = await ensureRepoAppPrivateKeySecret(
    { vaultName: "kv" },
    { exec, log: noopLog() },
  );

  assert.equal(result.status, "available");
  assert.equal(exec.calls.some((call) => ["set", "download", "recover"].includes(call.args[2])), false);
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
  fs.writeFileSync(sourceFile, generatePrivateKeyPem());
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
  fs.writeFileSync(sourceFile, generatePrivateKeyPem("pkcs1"));
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

test("setSecretFileWithRetry rejects invalid private-key files before any Key Vault write", async (t) => {
  const cases = [
    {
      name: "malformed non-PEM input",
      contents: "SENSITIVE-PRIVATE-KEY-MATERIAL",
      expected: /valid PEM-encoded RSA private key/i,
    },
    {
      name: "malformed private-key PEM",
      contents: "-----BEGIN PRIVATE KEY-----\nnot-valid-base64\n-----END PRIVATE KEY-----",
      expected: /valid PEM-encoded RSA private key/i,
    },
    {
      name: "public-key-only PEM",
      contents: generatePublicKeyPem(),
      expected: /valid PEM-encoded RSA private key/i,
    },
    {
      name: "non-RSA private key",
      contents: generateEcPrivateKeyPem(),
      expected: /must contain an RSA private key/i,
    },
    {
      name: "encrypted RSA private key",
      contents: generateEncryptedPrivateKeyPem(),
      expected: /encrypted.*not supported.*cannot prompt/i,
    },
  ];

  for (const testCase of cases) {
    await t.test(testCase.name, async () => {
      const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-invalid-"));
      const sourceFile = path.join(scratchRoot, "repo-app.pem");
      fs.writeFileSync(sourceFile, testCase.contents);
      const exec = fakeExec(() => {
        throw new Error("Key Vault must not be called for invalid key material.");
      });

      try {
        await assert.rejects(
          setSecretFileWithRetry("kv", "secret", sourceFile, {
            exec,
            log: noopLog(),
          }),
          (error) => {
            assert.match(error.message, testCase.expected);
            assert.doesNotMatch(error.message, /SENSITIVE-PRIVATE-KEY-MATERIAL|BEGIN .* KEY/);
            return true;
          },
        );
        assert.equal(exec.calls.length, 0);
      } finally {
        fs.rmSync(scratchRoot, { recursive: true, force: true });
      }
    });
  }
});

test("setSecretFileWithRetry rejects empty and unreadable inputs before any Key Vault write", async (t) => {
  await t.test("empty file", async () => {
    const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-empty-"));
    const sourceFile = path.join(scratchRoot, "repo-app.pem");
    fs.writeFileSync(sourceFile, "");
    const exec = fakeExec(() => {
      throw new Error("Key Vault must not be called for empty key material.");
    });

    try {
      await assert.rejects(
        setSecretFileWithRetry("kv", "secret", sourceFile, { exec, log: noopLog() }),
        /must be a non-empty file/i,
      );
      assert.equal(exec.calls.length, 0);
    } finally {
      fs.rmSync(scratchRoot, { recursive: true, force: true });
    }
  });

  await t.test("unreadable path", async () => {
    const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-unreadable-"));
    const sourceFile = path.join(scratchRoot, "missing.pem");
    const exec = fakeExec(() => {
      throw new Error("Key Vault must not be called for unreadable key material.");
    });

    try {
      await assert.rejects(
        setSecretFileWithRetry("kv", "secret", sourceFile, { exec, log: noopLog() }),
        /could not be read/i,
      );
      assert.equal(exec.calls.length, 0);
    } finally {
      fs.rmSync(scratchRoot, { recursive: true, force: true });
    }
  });
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
  const secretValue = generatePrivateKeyPem();
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

test("ensureRepoAppPrivateKeySecret recovers canonical after legacy inspection without writing the legacy value", async () => {
  let canonicalChecks = 0;
  let deletedChecks = 0;
  const messages = [];
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    if (operation === "show-deleted") {
      assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      deletedChecks += 1;
      return deletedChecks === 1
        ? { stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found", code: 3 }
        : { stdout: "https://kv/deletedsecrets/ghtok-repo-app-private-key", stderr: "", code: 0 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.physicalName) {
      canonicalChecks += 1;
      if (canonicalChecks < 4) {
        return { stdout: "", stderr: "ERROR: (SecretNotFound) canonical secret is absent", code: 3 };
      }
      return { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName) {
      return { stdout: "https://kv/secrets/repo-app-private-key/version", stderr: "", code: 0 };
    }
    if (operation === "recover") {
      assert.equal(name, REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      return { stdout: "", stderr: "", code: 0 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  const result = await ensureRepoAppPrivateKeySecret(
    { vaultName: "kv" },
    {
      exec,
      log: { ...noopLog(), info: (message) => messages.push(message) },
      sleep: async () => {},
    },
  );

  assert.equal(result.status, "recovered");
  assert.equal(exec.calls.filter((call) => call.args[2] === "recover").length, 1);
  assert.equal(exec.calls.some((call) => ["set", "download"].includes(call.args[2])), false);
  assert.equal(messages.some((message) => /private-key-material|BEGIN .* PRIVATE KEY/.test(message)), false);
});

test("ensureRepoAppPrivateKeySecret fails closed when canonical access becomes ambiguous after legacy inspection", async () => {
  let canonicalChecks = 0;
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    if (operation === "show-deleted") {
      return { stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.physicalName) {
      canonicalChecks += 1;
      return canonicalChecks < 3
        ? { stdout: "", stderr: "ERROR: (SecretNotFound) canonical secret is absent", code: 3 }
        : { stdout: "", stderr: "ERROR: (ForbiddenByRbac) canonical inspection denied", code: 1 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName) {
      return { stdout: "https://kv/secrets/repo-app-private-key/version", stderr: "", code: 0 };
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
  assert.equal(exec.calls.some((call) => ["set", "download", "recover"].includes(call.args[2])), false);
});

test("ensureRepoAppPrivateKeySecret cannot overwrite a configured-file import from another runner", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-writer-race-"));
  const configuredFile = path.join(scratchRoot, "configured.pem");
  const configuredPrivateKey = generatePrivateKeyPem();
  fs.writeFileSync(configuredFile, configuredPrivateKey);
  const legacyInspectionReached = deferred();
  const allowLegacyInspectionToReturn = deferred();
  let canonicalAvailable = false;
  let canonicalValue = "";

  const migrationExec = fakeExec(async (_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    if (operation === "show-deleted") {
      return { stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.physicalName) {
      if (canonicalAvailable) {
        return { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 };
      }
      return { stdout: "", stderr: "ERROR: (SecretNotFound) canonical secret is absent", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName) {
      legacyInspectionReached.resolve();
      await allowLegacyInspectionToReturn.promise;
      return { stdout: "https://kv/secrets/repo-app-private-key/version", stderr: "", code: 0 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });
  const writerExec = fakeExec((_cmd, args) => {
    const operation = args[2];
    if (operation === "set") {
      canonicalAvailable = true;
      canonicalValue = fs.readFileSync(args[args.indexOf("--file") + 1], "utf8");
      return { stdout: "", stderr: "", code: 0 };
    }
    if (operation === "show") {
      return canonicalAvailable
        ? { stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "", code: 0 }
        : { stdout: "", stderr: "ERROR: (SecretNotFound) canonical secret is absent", code: 3 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  try {
    const migration = ensureRepoAppPrivateKeySecret(
      { vaultName: "kv" },
      { exec: migrationExec, log: noopLog() },
    );
    await legacyInspectionReached.promise;
    const writerResult = await ensureRepoAppPrivateKeySecret(
      { vaultName: "kv", sourceFile: configuredFile },
      { exec: writerExec, log: noopLog() },
    );
    allowLegacyInspectionToReturn.resolve();
    const migrationResult = await migration;

    assert.equal(migrationResult.status, "available");
    assert.equal(writerResult.status, "imported");
    assert.equal(canonicalValue, configuredPrivateKey);
    assert.equal(migrationExec.calls.some((call) =>
      ["set", "download", "recover"].includes(call.args[2])), false);
    assert.equal(writerExec.calls.filter((call) => call.args[2] === "set").length, 1);
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});

test("ensureRepoAppPrivateKeySecret fails closed when deleted-secret access is lost after legacy inspection", async () => {
  let canonicalChecks = 0;
  let deletedChecks = 0;
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    const name = requestedSecret(args);
    if (operation === "show-deleted") {
      deletedChecks += 1;
      return deletedChecks === 1
        ? { stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found", code: 3 }
        : { stdout: "", stderr: "ERROR: (ForbiddenByRbac) deleted-secret inspection denied", code: 1 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.physicalName) {
      canonicalChecks += 1;
      return { stdout: "", stderr: "ERROR: (SecretNotFound) canonical secret is absent", code: 3 };
    }
    if (operation === "show" && name === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName) {
      return { stdout: "https://kv/secrets/repo-app-private-key/version", stderr: "", code: 0 };
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
  assert.equal(canonicalChecks, 3);
  assert.equal(exec.calls.some((call) => ["set", "download", "recover"].includes(call.args[2])), false);
});
