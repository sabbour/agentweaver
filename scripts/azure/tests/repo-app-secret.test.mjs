import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { createHash, generateKeyPairSync } from "node:crypto";
import {
  ensureRepoAppPrivateKeySecret,
  REPO_APP_PRIVATE_KEY_SECRET,
  setSecretFileWithRetry,
  stageRepoAppPrivateKeyFile,
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

function secretHash(value) {
  return createHash("sha256").update(value).digest("hex");
}

function assertSameSecretBytes(actual, expected) {
  assert.equal(secretHash(actual), secretHash(expected));
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
  let stagedFile;
  const exec = fakeExec((_cmd, args) => {
    if (args[2] === "set") {
      assert.equal(requestedSecret(args), REPO_APP_PRIVATE_KEY_SECRET.physicalName);
      stagedFile = args[args.indexOf("--file") + 1];
      assert.notEqual(stagedFile, sourceFile);
      assertSameSecretBytes(fs.readFileSync(stagedFile), fs.readFileSync(sourceFile));
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
    assert.equal(fs.existsSync(stagedFile), false);
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

test("setSecretFileWithRetry always removes the staged file after an Azure failure", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-cleanup-"));
  const sourceFile = path.join(scratchRoot, "repo-app.pem");
  fs.writeFileSync(sourceFile, generatePrivateKeyPem());
  let stagedFile;
  const exec = fakeExec((_cmd, args) => {
    stagedFile = args[args.indexOf("--file") + 1];
    assert.notEqual(stagedFile, sourceFile);
    return { stdout: "", stderr: "write failed", code: 1 };
  });

  try {
    await assert.rejects(
      setSecretFileWithRetry("kv", "secret", sourceFile, {
        exec,
        log: noopLog(),
        maxAttempts: 1,
      }),
      /failed to set key vault secret/i,
    );
    assert.equal(fs.existsSync(stagedFile), false);
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});

test("setSecretFileWithRetry rejects invalid private-key files before any Key Vault write", async (t) => {
  const cases = [
    {
      name: "malformed non-PEM input",
      contents: "SENSITIVE-PRIVATE-KEY-MATERIAL",
      expected: /exactly one unencrypted.*private key pem block/i,
    },
    {
      name: "malformed private-key PEM",
      contents: "-----BEGIN PRIVATE KEY-----\nnot-valid-base64\n-----END PRIVATE KEY-----",
      expected: /valid PEM-encoded RSA private key/i,
    },
    {
      name: "public-key-only PEM",
      contents: generatePublicKeyPem(),
      expected: /exactly one unencrypted.*private key pem block/i,
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
    {
      name: "two concatenated PKCS8 private keys",
      contents: `${generatePrivateKeyPem()}${generatePrivateKeyPem()}`,
      expected: /exactly one unencrypted.*private key pem block/i,
    },
    {
      name: "concatenated PKCS1 and PKCS8 private keys",
      contents: `${generatePrivateKeyPem("pkcs1")}${generatePrivateKeyPem("pkcs8")}`,
      expected: /exactly one unencrypted.*private key pem block/i,
    },
    {
      name: "private key plus trailing non-PEM content",
      contents: `${generatePrivateKeyPem()}\nnot-allowed`,
      expected: /exactly one unencrypted.*private key pem block/i,
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

test("stageRepoAppPrivateKeyFile accepts the two .NET RSA.ImportFromPem-compatible private-key encodings", async (t) => {
      for (const type of ["pkcs1", "pkcs8"]) {
        await t.test(type, () => {
          const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-compatible-"));
          const sourceFile = path.join(scratchRoot, "source.pem");
          const sourceBytes = generatePrivateKeyPem(type);
          fs.writeFileSync(sourceFile, sourceBytes);
          const staged = stageRepoAppPrivateKeyFile(sourceFile, { scratchDir: scratchRoot });
          try {
            assert.notEqual(staged.filePath, sourceFile);
            assertSameSecretBytes(fs.readFileSync(staged.filePath), sourceBytes);
            if (process.platform !== "win32") {
              assert.equal(fs.statSync(staged.filePath).mode & 0o777, 0o600);
            }
          } finally {
            staged.cleanup();
            assert.equal(fs.existsSync(staged.filePath), false);
            fs.rmSync(scratchRoot, { recursive: true, force: true });
          }
        });
      }
});

    test("stageRepoAppPrivateKeyFile rejects source symlinks before staging", (t) => {
      const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-symlink-"));
      const targetFile = path.join(scratchRoot, "target.pem");
      const sourceFile = path.join(scratchRoot, "source.pem");
      fs.writeFileSync(targetFile, generatePrivateKeyPem());
      try {
        fs.symlinkSync(targetFile, sourceFile, "file");
      } catch (error) {
        fs.rmSync(scratchRoot, { recursive: true, force: true });
        t.skip(`File symlinks are unavailable on this platform: ${error.code ?? "unknown error"}`);
        return;
      }

      try {
        assert.throws(
          () => stageRepoAppPrivateKeyFile(sourceFile, { scratchDir: scratchRoot }),
          /must not be a symbolic link, junction, or reparse-point path/i,
        );
      } finally {
        fs.rmSync(scratchRoot, { recursive: true, force: true });
      }
    });

    test("stageRepoAppPrivateKeyFile rejects paths traversing a directory symlink or junction", (t) => {
      const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-junction-"));
      const targetDir = path.join(scratchRoot, "target");
      const linkedDir = path.join(scratchRoot, "linked");
      const targetFile = path.join(targetDir, "source.pem");
      fs.mkdirSync(targetDir);
      fs.writeFileSync(targetFile, generatePrivateKeyPem());
      try {
        fs.symlinkSync(targetDir, linkedDir, "junction");
      } catch (error) {
        fs.rmSync(scratchRoot, { recursive: true, force: true });
        t.skip(`Directory junctions are unavailable on this platform: ${error.code ?? "unknown error"}`);
        return;
      }

      try {
        assert.throws(
          () => stageRepoAppPrivateKeyFile(path.join(linkedDir, "source.pem"), { scratchDir: scratchRoot }),
          /must not traverse a symbolic link, junction, or reparse-point path/i,
        );
      } finally {
        fs.rmSync(scratchRoot, { recursive: true, force: true });
      }
    });

    test("ensureRepoAppPrivateKeySecret sends the already-staged bytes even if the source file changes", async () => {
      const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-toctou-"));
      const sourceFile = path.join(scratchRoot, "source.pem");
      const originalBytes = generatePrivateKeyPem("pkcs1");
      fs.writeFileSync(sourceFile, originalBytes);
      const staged = stageRepoAppPrivateKeyFile(sourceFile, { scratchDir: scratchRoot });
      fs.writeFileSync(sourceFile, generatePrivateKeyPem("pkcs8"));
      let azureFile;
      const exec = fakeExec((_cmd, args) => {
        if (args[2] === "set") {
          azureFile = args[args.indexOf("--file") + 1];
          assert.equal(azureFile, staged.filePath);
          assertSameSecretBytes(fs.readFileSync(azureFile), originalBytes);
        }
        return { stdout: "verified", stderr: "", code: 0 };
      });

      try {
        const result = await ensureRepoAppPrivateKeySecret(
          { vaultName: "kv", stagedSourceFile: staged.filePath },
          { exec, log: noopLog() },
        );
        assert.equal(result.status, "imported");
        assert.equal(fs.existsSync(azureFile), true);
      } finally {
        staged.cleanup();
        assert.equal(fs.existsSync(staged.filePath), false);
        fs.rmSync(scratchRoot, { recursive: true, force: true });
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

test("ensureRepoAppPrivateKeySecret refuses soft-deleted canonical recovery without the operator flag", async () => {
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    if (operation === "show") {
      return { stdout: "", stderr: "ERROR: (SecretNotFound) active secret was not found", code: 3 };
    }
    if (operation === "show-deleted") {
      return { stdout: "https://kv/deletedsecrets/ghtok-repo-app-private-key", stderr: "", code: 0 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  await assert.rejects(
    ensureRepoAppPrivateKeySecret(
      { vaultName: "kv" },
      { exec, log: noopLog() },
    ),
    /normal deployment will not reactivate old credentials.*--recover-repo-app-private-key/is,
  );
  assert.equal(exec.calls.some((call) => call.args[2] === "recover"), false);
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
    { vaultName: "kv", recoverDeleted: true },
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
        { vaultName: "kv", recoverDeleted: true },
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
      { vaultName: "kv", recoverDeleted: true },
      { exec, log: noopLog(), sleep: async () => {} },
    ),
    /failed to recover.*ForbiddenByRbac/i,
  );
  assert.equal(exec.calls.filter((call) => call.args[2] === "recover").length, 1);
  assert.equal(exec.calls.some((call) =>
    requestedSecret(call.args) === REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName), false);
});

test("ensureRepoAppPrivateKeySecret will not recover a soft-deleted canonical secret during normal replacement", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-no-recover-import-"));
  const sourceFile = path.join(scratchRoot, "repo-app.pem");
  fs.writeFileSync(sourceFile, generatePrivateKeyPem());
  const exec = fakeExec((_cmd, args) => {
    if (args[2] === "set") {
      return { stdout: "", stderr: "ERROR: (ObjectIsDeletedButRecoverable) Secret is deleted but recoverable.", code: 1 };
    }
    throw new Error(`Unexpected command: ${args.join(" ")}`);
  });

  try {
    await assert.rejects(
      ensureRepoAppPrivateKeySecret(
        { vaultName: "kv", sourceFile },
        { exec, log: noopLog() },
      ),
      /normal deployment will not reactivate old credentials.*--recover-repo-app-private-key/is,
    );
    assert.equal(exec.calls.some((call) => call.args[2] === "recover"), false);
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});

test("ensureRepoAppPrivateKeySecret recovers a soft-deleted canonical secret before configured-file import", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repo-app-secret-recover-import-"));
  const sourceFile = path.join(scratchRoot, "repo-app.pem");
  const secretValue = generatePrivateKeyPem();
  fs.writeFileSync(sourceFile, secretValue);
  let setAttempts = 0;
  let showAttempts = 0;
  const messages = [];
  let stagedFile;
  const exec = fakeExec((_cmd, args) => {
    const operation = args[2];
    if (operation === "set") {
      setAttempts += 1;
      stagedFile = args[args.indexOf("--file") + 1];
      assert.notEqual(stagedFile, sourceFile);
      assertSameSecretBytes(fs.readFileSync(stagedFile), secretValue);
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
      { vaultName: "kv", sourceFile, recoverDeleted: true },
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
    assert.equal(fs.existsSync(stagedFile), false);
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
    { vaultName: "kv", recoverDeleted: true },
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
    assertSameSecretBytes(canonicalValue, configuredPrivateKey);
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
