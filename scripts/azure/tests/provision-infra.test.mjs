import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { generateKeyPairSync } from "node:crypto";
import { parseArgs, run, runInteractiveInstaller } from "../provision-infra.mjs";
import { ensureRepoAppPrivateKeySecret } from "../lib/repo-app-secret.mjs";

function generateRsaPrivateKeyPem(type = "pkcs8") {
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

function noopLog() {
  return {
    banner() {},
    field() {},
    info() {},
    ok() {},
    section() {},
    skip() {},
    step() {},
    warn() {},
  };
}

test("parseArgs accepts the optional Entra enterprise app object ID flag", () => {
  const parsed = parseArgs([
    "--entra-client-id", "client-id",
    "--entra-tenant-id", "tenant-id",
    "--entra-enterprise-app-object-id", "enterprise-object-id",
  ]);

  assert.deepEqual(parsed, {
    flags: {
      ENTRA_CLIENT_ID: "client-id",
      ENTRA_TENANT_ID: "tenant-id",
      ENTRA_ENTERPRISE_APP_OBJECT_ID: "enterprise-object-id",
    },
    paramsFile: undefined,
    help: false,
  });
});

test("parseArgs accepts the Repo App private-key PEM file", () => {
  const parsed = parseArgs(["--repo-app-private-key-file", "C:\\secure\\repo-app.pem"]);

  assert.equal(parsed.flags.REPO_APP_PRIVATE_KEY_FILE, "C:\\secure\\repo-app.pem");
});

test("parseArgs accepts OAuth Key Vault certificate family overrides", () => {
  const parsed = parseArgs([
    "--oauth-signing-certificate-name", "oauth-signing-next",
    "--oauth-encryption-certificate-name=oauth-encryption-next",
  ]);
  assert.deepEqual(parsed.flags, {
    OAUTH_SIGNING_CERTIFICATE_NAME: "oauth-signing-next",
    OAUTH_ENCRYPTION_CERTIFICATE_NAME: "oauth-encryption-next",
  });
});

test("parseArgs accepts the explicit Repo App recovery operator flag", () => {
  const parsed = parseArgs(["--recover-repo-app-private-key"]);
  assert.equal(parsed.flags.RECOVER_REPO_APP_PRIVATE_KEY, true);
});

test("runInteractiveInstaller allows the optional Entra enterprise app object ID prompt to be left blank", async () => {
  const textAnswers = [
    "sub-id",
    "agentweaver-rg",
    "westus2",
    "agentweaver-aks",
    "agentweaverregistry",
    "Standard_D4s_v6",
    "agentweaver-kv",
    "westus2",
    "agentweaver-pg",
    "westus2",
    "ZoneRedundant",
    "private",
    "client-id",
    "tenant-id",
    "",
    "",
  ];
  const prompt = {
    select: async (_label, choices) => choices[0].value,
    text: async () => textAnswers.shift(),
  };
  const az = {
    listSubscriptions: async () => [],
    showAccount: async () => null,
    listResourceGroups: async () => [],
    listLocations: async () => [],
    setActiveSubscription: async () => {},
  };
  const log = {
    banner() {},
    info() {},
    section() {},
  };

  const collected = await runInteractiveInstaller({ prompt, az, log });

  assert.equal(collected.AUTH_MODE, "Entra");
  assert.equal(collected.ENTRA_CLIENT_ID, "client-id");
  assert.equal(collected.ENTRA_TENANT_ID, "tenant-id");
  assert.equal(collected.ENTRA_ENTERPRISE_APP_OBJECT_ID, "");
  assert.equal(collected.REPO_APP_PRIVATE_KEY_FILE, "");
});

test("run rejects every invalid Repo App key class before variable discovery or any Azure collaborator", async (t) => {
  const validPkcs8 = generateRsaPrivateKeyPem();
  const cases = [
    ["empty file", ""],
    ["malformed non-PEM input", "SENSITIVE-PRIVATE-KEY-MATERIAL"],
    ["malformed private-key PEM", "-----BEGIN PRIVATE KEY-----\nnot-valid-base64\n-----END PRIVATE KEY-----"],
    ["public-key-only PEM", generatePublicKeyPem()],
    ["non-RSA private key", generateEcPrivateKeyPem()],
    ["encrypted RSA private key", generateEncryptedPrivateKeyPem()],
    ["concatenated private keys", `${validPkcs8}${generateRsaPrivateKeyPem("pkcs1")}`],
    ["private key plus trailing content", `${validPkcs8}\nnot-allowed`],
  ];

  for (const [name, contents] of cases) {
    await t.test(name, async () => {
      const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "provision-invalid-repo-app-key-"));
      const sourceFile = path.join(scratchRoot, "repo-app.pem");
      fs.writeFileSync(sourceFile, contents);
      const azureCalls = [];
      let variablesResolved = false;
      let stepCalled = false;
      const failAzure = (name) => async (...args) => {
        azureCalls.push({ name, args });
        throw new Error(`Azure collaborator '${name}' must not be called.`);
      };
      const az = new Proxy({}, {
        get: (_target, property) => failAzure(String(property)),
      });
      const exec = {
        capture: failAzure("exec.capture"),
        run: failAzure("exec.run"),
      };
      const blockedStep = {
        run: async () => {
          stepCalled = true;
          throw new Error("Provisioning step must not be called.");
        },
      };

      try {
        await assert.rejects(
          run({
            argv: ["--repo-app-private-key-file", sourceFile],
            env: {},
            prompt: { isInteractive: () => false },
            az,
            exec,
            log: { info() {} },
            resolveVariables: async () => {
              variablesResolved = true;
              throw new Error("Variable discovery must not be called.");
            },
            steps: {
              createCluster: blockedStep,
              setupIdentity: blockedStep,
              provisionMonitoring: blockedStep,
              provisionPostgres: blockedStep,
              buildImages: blockedStep,
              verifyProvenance: blockedStep,
              genA2aMtlsCerts: blockedStep,
              deployStep: blockedStep,
              verifyStep: blockedStep,
            },
          }),
        );
        assert.equal(azureCalls.length, 0);
        assert.equal(variablesResolved, false);
        assert.equal(stepCalled, false);
      } finally {
        fs.rmSync(scratchRoot, { recursive: true, force: true });
      }
    });
  }

  await t.test("unreadable path", async () => {
    const azureCalls = [];
    let variablesResolved = false;
    await assert.rejects(
      run({
        argv: ["--repo-app-private-key-file", path.join(os.tmpdir(), "missing-repo-app-key.pem")],
        env: {},
        prompt: { isInteractive: () => false },
        az: {},
        exec: {
          capture: async (...args) => {
            azureCalls.push(args);
            throw new Error("Azure must not be called.");
          },
        },
        log: { info() {} },
        resolveVariables: async () => {
          variablesResolved = true;
          return {};
        },
      }),
      /could not be read/i,
    );
    assert.equal(azureCalls.length, 0);
    assert.equal(variablesResolved, false);
  });
});

test("run rejects a Repo App key reparse path before variable discovery or Azure calls", async (t) => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "provision-repo-app-symlink-"));
  const targetFile = path.join(scratchRoot, "target.pem");
  const sourceFile = path.join(scratchRoot, "source.pem");
  fs.writeFileSync(targetFile, generateRsaPrivateKeyPem());
  try {
    fs.symlinkSync(targetFile, sourceFile, "file");
  } catch {
    const targetDir = path.join(scratchRoot, "target-dir");
    fs.mkdirSync(targetDir);
    try {
      fs.symlinkSync(targetDir, sourceFile, "junction");
    } catch (error) {
      fs.rmSync(scratchRoot, { recursive: true, force: true });
      t.skip(`Reparse points are unavailable on this platform: ${error.code ?? "unknown error"}`);
      return;
    }
  }

  let variablesResolved = false;
  const azureCalls = [];
  try {
    await assert.rejects(
      run({
        argv: ["--repo-app-private-key-file", sourceFile],
        env: {},
        prompt: { isInteractive: () => false },
        az: {},
        exec: {
          capture: async (...args) => {
            azureCalls.push(args);
            throw new Error("Azure must not be called.");
          },
        },
        log: { info() {} },
        resolveVariables: async () => {
          variablesResolved = true;
          return {};
        },
      }),
      /must not be a symbolic link, junction, or reparse-point path/i,
    );
    assert.equal(azureCalls.length, 0);
    assert.equal(variablesResolved, false);
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});

test("run imports the staged Repo App key once and makes deploy verification-only", async () => {
  const scratchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "provision-repo-app-once-"));
  const sourceFile = path.join(scratchRoot, "source.pem");
  fs.writeFileSync(sourceFile, generateRsaPrivateKeyPem());
  let canonicalAvailable = false;
  let stagedFile;
  let setCalls = 0;
  const keyVaultExec = {
    async capture(_cmd, args) {
      const operation = args[2];
      if (operation === "set") {
        setCalls += 1;
        stagedFile = args[args.indexOf("--file") + 1];
        assert.equal(fs.existsSync(stagedFile), true);
        canonicalAvailable = true;
        return { code: 0, stdout: "", stderr: "" };
      }
      if (operation === "show") {
        return canonicalAvailable
          ? { code: 0, stdout: "https://kv/secrets/ghtok-repo-app-private-key/version", stderr: "" }
          : { code: 3, stdout: "", stderr: "ERROR: (SecretNotFound) secret was not found" };
      }
      if (operation === "show-deleted") {
        return { code: 3, stdout: "", stderr: "ERROR: (SecretNotFound) deleted secret was not found" };
      }
      throw new Error(`Unexpected Key Vault operation: ${args.join(" ")}`);
    },
  };
  const resolvedConfigs = [];
  const resolveVariables = async ({ env }) => {
    resolvedConfigs.push({ ...env });
    return {
      RESOURCE_GROUP: "rg",
      CLUSTER_NAME: "aks",
      ACR_NAME: "acr",
      ACR_LOGIN_SERVER: "acr.azurecr.io",
      LOCATION: "westus2",
      MONITORING_LOCATION: "westus2",
      NODE_VM_SIZE: "Standard_D4s_v6",
      KEYVAULT_NAME: "kv",
      PG_SERVER_NAME: "agentweaver-pg",
      PG_LOCATION: "westus2",
      PG_HA_MODE: "ZoneRedundant",
      PG_ACCESS_MODE: "private",
      NAMESPACE: "agentweaver",
      AUTH_MODE: "Entra",
      ENTRA_CLIENT_ID: "client-id",
      ENTRA_TENANT_ID: "tenant-id",
      OAUTH_SIGNING_CERTIFICATE_NAME: "signing",
      OAUTH_ENCRYPTION_CERTIFICATE_NAME: "encryption",
      REPO_APP_PRIVATE_KEY_FILE: env.REPO_APP_PRIVATE_KEY_FILE ?? "",
      IMAGE_TAG: "test",
      AGENTHOST_IMAGE_TAG: "test",
    };
  };
  const noOpStep = { run: async () => ({}) };

  try {
    const result = await run({
      argv: [
        "--skip-postgres",
        "--image-source", "acr-build",
        "--entra-client-id", "client-id",
        "--entra-tenant-id", "tenant-id",
        "--repo-app-private-key-file", sourceFile,
        "--recover-repo-app-private-key",
      ],
      env: {},
      prompt: { isInteractive: () => false },
      az: {},
      exec: {
        capture: async () => ({ code: 1, stdout: "", stderr: "" }),
        run: async () => ({ code: 0, stdout: "", stderr: "" }),
      },
      log: noopLog(),
      resolveVariables,
      steps: {
        createCluster: noOpStep,
        setupIdentity: {
          run: async (cfg) => {
            assert.notEqual(cfg.REPO_APP_PRIVATE_KEY_STAGED_FILE, "");
            assert.equal(cfg.RECOVER_REPO_APP_PRIVATE_KEY, true);
            await ensureRepoAppPrivateKeySecret(
              {
                vaultName: cfg.KEYVAULT_NAME,
                stagedSourceFile: cfg.REPO_APP_PRIVATE_KEY_STAGED_FILE,
                recoverDeleted: cfg.RECOVER_REPO_APP_PRIVATE_KEY,
              },
              { exec: keyVaultExec, log: noopLog() },
            );
          },
        },
        provisionMonitoring: noOpStep,
        provisionPostgres: noOpStep,
        buildImages: { run: async () => ({ expectedImageDigests: {} }) },
        verifyProvenance: noOpStep,
        genA2aMtlsCerts: noOpStep,
        deployStep: {
          run: async (cfg) => {
            assert.equal(cfg.REPO_APP_PRIVATE_KEY_FILE, "");
            assert.equal(cfg.REPO_APP_PRIVATE_KEY_STAGED_FILE, "");
            assert.equal(cfg.RECOVER_REPO_APP_PRIVATE_KEY, false);
            assert.equal(fs.existsSync(stagedFile), false);
            return ensureRepoAppPrivateKeySecret(
              {
                vaultName: cfg.KEYVAULT_NAME,
                sourceFile: cfg.REPO_APP_PRIVATE_KEY_FILE,
                stagedSourceFile: cfg.REPO_APP_PRIVATE_KEY_STAGED_FILE,
                recoverDeleted: cfg.RECOVER_REPO_APP_PRIVATE_KEY,
              },
              { exec: keyVaultExec, log: noopLog() },
            );
          },
        },
        verifyStep: { run: async () => ({ ok: true, pass: 1, fail: 0 }) },
      },
    });

    assert.equal(result.ok, true);
    assert.equal(setCalls, 1);
    assert.equal(resolvedConfigs.length, 2);
    assert.notEqual(resolvedConfigs[0].REPO_APP_PRIVATE_KEY_FILE, "");
    assert.equal(resolvedConfigs[1].REPO_APP_PRIVATE_KEY_FILE, "");
  } finally {
    fs.rmSync(scratchRoot, { recursive: true, force: true });
  }
});
