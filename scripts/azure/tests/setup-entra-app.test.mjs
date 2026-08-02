import test from "node:test";
import assert from "node:assert/strict";
import { DEFAULT_APP_ROLES, HELP_TEXT, mergeManagedAppRoles, normalizeRedirectUris, parseArgs, run } from "../setup-entra-app.mjs";

function noopLog() {
  const rec = () => () => {};
  return { info: rec(), section: rec(), field: rec(), ok: rec(), skip: rec(), warn: rec(), error: rec(), debug: rec(), command: rec(), banner: rec() };
}

function captureLog() {
  const entries = [];
  const push = (level) => (...args) => entries.push([level, ...args]);
  return {
    entries,
    banner: push("banner"),
    command: push("command"),
    debug: push("debug"),
    error: push("error"),
    field: push("field"),
    info: push("info"),
    ok: push("ok"),
    section: push("section"),
    skip: push("skip"),
    warn: push("warn"),
  };
}

test("parseArgs: recognizes app-name, app-id, repeated redirect-uri, and service-management-reference", () => {
  const parsed = parseArgs([
    "--app-name",
    "agentweaver-prod-authn",
    "--app-id=11111111-1111-1111-1111-111111111111",
    "--redirect-uri",
    "http://localhost:5000/auth/entra/callback",
    "--redirect-uri=https://agentweaver.example.com/auth/entra/callback",
    "--service-management-reference",
    "22222222-2222-2222-2222-222222222222",
  ]);
  assert.equal(parsed.flags.APP_NAME, "agentweaver-prod-authn");
  assert.equal(parsed.flags.APP_ID, "11111111-1111-1111-1111-111111111111");
  assert.deepEqual(parsed.flags.REDIRECT_URIS, [
    "http://localhost:5000/auth/entra/callback",
    "https://agentweaver.example.com/auth/entra/callback",
  ]);
  assert.equal(parsed.flags.SERVICE_MANAGEMENT_REFERENCE, "22222222-2222-2222-2222-222222222222");
});

test("parseArgs: -h/--help sets help", () => {
  assert.equal(parseArgs(["--help"]).help, true);
  assert.equal(parseArgs(["-h"]).help, true);
});

test("HELP_TEXT: mentions the key flags", () => {
  assert.match(HELP_TEXT, /--app-name/);
  assert.match(HELP_TEXT, /--redirect-uri/);
  assert.match(HELP_TEXT, /--service-management-reference/);
});

test("normalizeRedirectUris: trims, validates, and de-dupes case-insensitively", () => {
  assert.deepEqual(normalizeRedirectUris([
    " http://localhost:5000/auth/entra/callback ",
    "HTTP://LOCALHOST:5000/auth/entra/callback",
    "https://agentweaver.example.com/auth/entra/callback",
  ]), [
    "http://localhost:5000/auth/entra/callback",
    "https://agentweaver.example.com/auth/entra/callback",
  ]);
});

test("mergeManagedAppRoles: preserves unrelated roles while replacing managed Agentweaver roles", () => {
  const current = [
    {
      allowedMemberTypes: ["User"],
      description: "Unrelated role",
      displayName: "Other",
      id: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      isEnabled: true,
      value: "Other",
    },
    {
      allowedMemberTypes: ["User"],
      description: "Agentweaver: old placeholder role",
      displayName: "Old",
      id: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
      isEnabled: true,
      value: "Old",
      origin: "Application",
    },
  ];
  const { merged, changed } = mergeManagedAppRoles(current);
  assert.equal(changed, true);
  assert.equal(merged[0].value, "Other");
  assert.deepEqual(
    merged.slice(1).map((role) => role.value),
    DEFAULT_APP_ROLES.map((role) => role.value),
  );
});

test("DEFAULT_APP_ROLES: matches the Entra-mode platform role set", () => {
  assert.deepEqual(
    DEFAULT_APP_ROLES.map((role) => role.value),
    ["PlatformAdmin", "ProjectCreator", "Contributor", "Viewer"],
  );
});

test("run: creates app, patches roles, creates service principal, and returns config identifiers", async () => {
  const commands = [];
  let phase = "initial";
  const app = {
    appId: "11111111-1111-1111-1111-111111111111",
    appRoles: [],
    displayName: "agentweaver-authn",
    id: "33333333-3333-3333-3333-333333333333",
    signInAudience: "AzureADMyOrg",
    web: { redirectUris: ["http://localhost:5000/auth/entra/callback"] },
  };
  const appWithRoles = { ...app, appRoles: DEFAULT_APP_ROLES };
  const sp = {
    appId: app.appId,
    displayName: app.displayName,
    id: "44444444-4444-4444-4444-444444444444",
  };

  const exec = {
    async run(cmd, args) {
      commands.push([cmd, ...args]);
      if (args[0] === "rest") phase = "roles-patched";
      return { code: 0 };
    },
    async capture(cmd, args, opts = {}) {
      commands.push([cmd, ...args]);
      const joined = args.join(" ");
      if (joined.includes("account show")) return { code: 0, stdout: "72f988bf-86f1-41af-91ab-2d7cd011db47\n", stderr: "" };
      if (joined.includes("ad app list --display-name")) return { code: 0, stdout: "[]", stderr: "" };
      if (joined.includes("ad app create")) return { code: 0, stdout: JSON.stringify(app), stderr: "" };
      if (joined.includes("ad app show --id")) {
        return { code: 0, stdout: JSON.stringify(phase === "roles-patched" ? appWithRoles : app), stderr: "" };
      }
      if (joined.includes("ad sp show --id")) {
        if (opts.allowFailure) return { code: 1, stdout: "", stderr: "not found" };
      }
      if (joined.includes("ad sp create --id")) return { code: 0, stdout: JSON.stringify(sp), stderr: "" };
      throw new Error(`Unexpected capture: ${joined}`);
    },
  };

  const result = await run({ argv: [], exec, log: noopLog() });

  assert.equal(result.ok, true);
  assert.equal(result.appId, app.appId);
  assert.equal(result.tenantId, "72f988bf-86f1-41af-91ab-2d7cd011db47");
  assert.equal(result.servicePrincipalObjectId, sp.id);
  assert.deepEqual(result.redirectUris, ["http://localhost:5000/auth/entra/callback"]);
  assert.ok(commands.some((entry) => entry.includes("ad") && entry.includes("app") && entry.includes("create")));
  assert.ok(commands.some((entry) => entry.includes("rest") && entry.includes("PATCH")));
  assert.ok(commands.some((entry) => entry.includes("ad") && entry.includes("sp") && entry.includes("create")));
});

test("run: reusing an already-matching app is idempotent", async () => {
  const commands = [];
  const app = {
    appId: "11111111-1111-1111-1111-111111111111",
    appRoles: DEFAULT_APP_ROLES,
    displayName: "agentweaver-authn",
    id: "33333333-3333-3333-3333-333333333333",
    signInAudience: "AzureADMyOrg",
    web: {
      redirectUris: [
        "http://localhost:5000/auth/entra/callback",
        "https://agentweaver.example.com/auth/entra/callback",
      ],
    },
  };
  const sp = {
    appId: app.appId,
    displayName: app.displayName,
    id: "44444444-4444-4444-4444-444444444444",
  };

  const exec = {
    async run(cmd, args) {
      commands.push([cmd, ...args]);
      return { code: 0 };
    },
    async capture(cmd, args) {
      commands.push([cmd, ...args]);
      const joined = args.join(" ");
      if (joined.includes("account show")) return { code: 0, stdout: "72f988bf-86f1-41af-91ab-2d7cd011db47\n", stderr: "" };
      if (joined.includes("ad app list --display-name")) return { code: 0, stdout: JSON.stringify([app]), stderr: "" };
      if (joined.includes("ad app show --id")) return { code: 0, stdout: JSON.stringify(app), stderr: "" };
      if (joined.includes("ad sp show --id")) return { code: 0, stdout: JSON.stringify(sp), stderr: "" };
      throw new Error(`Unexpected capture: ${joined}`);
    },
  };

  const result = await run({
    argv: ["--redirect-uri", "https://agentweaver.example.com/auth/entra/callback"],
    exec,
    log: noopLog(),
  });

  assert.equal(result.ok, true);
  assert.ok(!commands.some((entry) => entry.includes("create") && entry.includes("app")));
  assert.ok(!commands.some((entry) => entry.includes("update") && entry.includes("web-redirect-uris")));
  assert.ok(!commands.some((entry) => entry.includes("rest") && entry.includes("PATCH")));
  assert.ok(!commands.some((entry) => entry.includes("sp") && entry.includes("create")));
});

test("run: summary explains PKCE-only fallback when client secrets are blocked", async () => {
  const log = captureLog();
  const app = {
    appId: "11111111-1111-1111-1111-111111111111",
    appRoles: DEFAULT_APP_ROLES,
    displayName: "agentweaver-authn",
    id: "33333333-3333-3333-3333-333333333333",
    signInAudience: "AzureADMyOrg",
    web: { redirectUris: ["http://localhost:5000/auth/entra/callback"] },
  };
  const sp = {
    appId: app.appId,
    displayName: app.displayName,
    id: "44444444-4444-4444-4444-444444444444",
  };

  const exec = {
    async run() {
      return { code: 0 };
    },
    async capture(cmd, args) {
      const joined = args.join(" ");
      if (joined.includes("account show")) return { code: 0, stdout: "72f988bf-86f1-41af-91ab-2d7cd011db47\n", stderr: "" };
      if (joined.includes("ad app list --display-name")) return { code: 0, stdout: JSON.stringify([app]), stderr: "" };
      if (joined.includes("ad app show --id")) return { code: 0, stdout: JSON.stringify(app), stderr: "" };
      if (joined.includes("ad sp show --id")) return { code: 0, stdout: JSON.stringify(sp), stderr: "" };
      throw new Error(`Unexpected capture: ${joined}`);
    },
  };

  const result = await run({ argv: [], exec, log });

  assert.equal(result.ok, true);
  const warning = log.entries.filter(([level]) => level === "warn").map(([, message]) => String(message)).join("\n");
  assert.match(warning, /Auth__Entra__ClientSecret is OPTIONAL/);
  assert.match(warning, /isFallbackPublicClient: true/);
  assert.match(warning, /PKCE only/);
});
