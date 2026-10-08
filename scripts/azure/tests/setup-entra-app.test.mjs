import test from "node:test";
import assert from "node:assert/strict";
import { DEFAULT_APP_ROLES, HELP_TEXT, mergeManagedAppRoles, normalizeRedirectUris, parseArgs, run } from "../setup-entra-app.mjs";
import {
  ACCESS_AS_USER_SCOPE_ID,
  ACCESS_AS_USER_SCOPE_VALUE,
  buildEntraAuthRegistrationPatch,
  inspectEntraAuthRegistration,
} from "../lib/entra-app-registration.mjs";

const APP_ID = "11111111-1111-1111-1111-111111111111";
const EXISTING_SCOPE_ID = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

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
  assert.match(HELP_TEXT, /Run before the first Entra installation/);
  assert.match(HELP_TEXT, /read-only/);
  assert.match(HELP_TEXT, /preauthorizes its own public client/);
  assert.match(HELP_TEXT, /second Graph PATCH/);
});

test("run: requires an explicit redirect URI", async () => {
  await assert.rejects(
    () => run({ argv: [], exec: {}, log: noopLog() }),
    /At least one --redirect-uri is required/,
  );
});

test("run: an unreadable explicit app ID fails without creating a replacement", async () => {
  const commands = [];
  const exec = {
    async run(cmd, args) {
      commands.push([cmd, ...args]);
      throw new Error("Unexpected registration mutation");
    },
    async capture(cmd, args) {
      commands.push([cmd, ...args]);
      if (args[0] === "account") {
        return { code: 0, stdout: "72f988bf-86f1-41af-91ab-2d7cd011db47\n", stderr: "" };
      }
      if (args[0] === "ad" && args[1] === "app" && args[2] === "show") {
        return { code: 1, stdout: "", stderr: "Application is unavailable" };
      }
      throw new Error("Unexpected replacement app creation");
    },
  };

  await assert.rejects(
    () => run({
      argv: [
        "--app-id", "11111111-1111-1111-1111-111111111111",
        "--redirect-uri", "https://agentweaver.example.com/auth/entra/callback",
      ],
      exec,
      log: noopLog(),
    }),
    /Command returned no JSON/,
  );
  assert.ok(!commands.some((command) => command.includes("create")));
});

test("run: invalid explicit app IDs fail before any Azure lookup", async () => {
  for (const appId of ["", " ", "not-a-client-id"]) {
    let calls = 0;
    const unexpected = async () => {
      calls += 1;
      throw new Error("Unexpected Azure operation");
    };
    await assert.rejects(
      () => run({
        argv: [
          `--app-id=${appId}`,
          "--redirect-uri", "https://agentweaver.example.com/auth/entra/callback",
        ],
        exec: { capture: unexpected, run: unexpected },
        log: noopLog(),
      }),
      /--app-id must be a valid Entra application/,
    );
    assert.equal(calls, 0);
  }
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

test("buildEntraAuthRegistrationPatch: configures a new delegated self-resource scope", () => {
  const { patch, changed, scopeId } = buildEntraAuthRegistrationPatch({ appId: APP_ID });

  assert.equal(changed, true);
  assert.equal(scopeId, ACCESS_AS_USER_SCOPE_ID);
  assert.deepEqual(patch.identifierUris, [`api://${APP_ID}`]);
  assert.equal(patch.api.requestedAccessTokenVersion, 2);
  assert.deepEqual(patch.api.oauth2PermissionScopes, [{
    adminConsentDescription: "Allow Agentweaver to sign in users through its own API.",
    adminConsentDisplayName: "Sign in to Agentweaver",
    type: "User",
    userConsentDescription: "Allow this application to sign you in to Agentweaver.",
    userConsentDisplayName: "Sign in to Agentweaver",
    value: ACCESS_AS_USER_SCOPE_VALUE,
    id: ACCESS_AS_USER_SCOPE_ID,
    isEnabled: true,
  }]);
  assert.deepEqual(patch.api.preAuthorizedApplications, [{
    appId: APP_ID,
    delegatedPermissionIds: [ACCESS_AS_USER_SCOPE_ID],
  }]);
  assert.deepEqual(patch.requiredResourceAccess, [{
    resourceAppId: APP_ID,
    resourceAccess: [{ id: ACCESS_AS_USER_SCOPE_ID, type: "Scope" }],
  }]);
  assert.deepEqual(inspectEntraAuthRegistration({
    appId: APP_ID,
    signInAudience: "AzureADMyOrg",
    isFallbackPublicClient: true,
    ...patch,
  }, APP_ID), []);
});

test("buildEntraAuthRegistrationPatch: reuses a valid matching scope and preserves unrelated settings", () => {
  const unrelatedScope = { id: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", value: "other_scope", isEnabled: true, type: "User" };
  const unrelatedResource = {
    resourceAppId: "cccccccc-cccc-cccc-cccc-cccccccccccc",
    resourceAccess: [{ id: "dddddddd-dddd-dddd-dddd-dddddddddddd", type: "Role" }],
  };
  const unrelatedClient = {
    appId: "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
    delegatedPermissionIds: ["ffffffff-ffff-ffff-ffff-ffffffffffff"],
  };
  const existing = {
    appId: APP_ID,
    identifierUris: ["https://api.agentweaver.example"],
    appRoles: DEFAULT_APP_ROLES,
    publicClient: { redirectUris: ["https://agentweaver.example/callback"] },
    web: { redirectUris: ["https://unrelated.example/callback"] },
    api: {
      customKey: "preserved",
      oauth2PermissionScopes: [
        unrelatedScope,
        {
          id: EXISTING_SCOPE_ID,
          value: ACCESS_AS_USER_SCOPE_VALUE,
          isEnabled: false,
          type: "User",
        },
      ],
      preAuthorizedApplications: [
        unrelatedClient,
        { appId: APP_ID, delegatedPermissionIds: ["12121212-1212-1212-1212-121212121212"] },
      ],
      requestedAccessTokenVersion: 1,
    },
    requiredResourceAccess: [
      unrelatedResource,
      {
        resourceAppId: APP_ID,
        resourceAccess: [{ id: "13131313-1313-1313-1313-131313131313", type: "Role" }],
      },
    ],
  };

  const { patch, changed, scopeId } = buildEntraAuthRegistrationPatch(existing);

  assert.equal(changed, true);
  assert.equal(scopeId, EXISTING_SCOPE_ID);
  assert.deepEqual(patch.identifierUris, ["https://api.agentweaver.example"]);
  assert.equal(patch.api.requestedAccessTokenVersion, 2);
  assert.equal(patch.api.customKey, "preserved");
  assert.deepEqual(patch.api.oauth2PermissionScopes[0], unrelatedScope);
  assert.equal(patch.api.oauth2PermissionScopes[1].id, EXISTING_SCOPE_ID);
  assert.equal(patch.api.oauth2PermissionScopes[1].isEnabled, true);
  assert.deepEqual(patch.api.preAuthorizedApplications[0], unrelatedClient);
  assert.deepEqual(patch.api.preAuthorizedApplications[1].delegatedPermissionIds, [
    "12121212-1212-1212-1212-121212121212",
    EXISTING_SCOPE_ID,
  ]);
  assert.deepEqual(patch.requiredResourceAccess[0], unrelatedResource);
  assert.deepEqual(patch.requiredResourceAccess[1].resourceAccess, [
    { id: "13131313-1313-1313-1313-131313131313", type: "Role" },
    { id: EXISTING_SCOPE_ID, type: "Scope" },
  ]);

  const configured = { ...existing, ...patch };
  assert.equal(buildEntraAuthRegistrationPatch(configured).changed, false);
});

test("buildEntraAuthRegistrationPatch: preserves existing Admin scope consent policy and metadata", () => {
  const existingScope = {
    id: EXISTING_SCOPE_ID,
    value: ACCESS_AS_USER_SCOPE_VALUE,
    isEnabled: true,
    type: "Admin",
    adminConsentDescription: "Operator-approved description",
    adminConsentDisplayName: "Operator-approved name",
    userConsentDescription: "Existing user description",
    userConsentDisplayName: "Existing user name",
  };
  const existing = {
    appId: APP_ID,
    identifierUris: [`api://${APP_ID}`],
    api: {
      oauth2PermissionScopes: [existingScope],
      requestedAccessTokenVersion: 2,
      preAuthorizedApplications: [],
    },
    requiredResourceAccess: [{
      resourceAppId: APP_ID,
      resourceAccess: [{ id: EXISTING_SCOPE_ID, type: "Scope" }],
    }],
  };

  const { patch, changed, scopeId } = buildEntraAuthRegistrationPatch(existing);

  assert.equal(changed, true);
  assert.equal(scopeId, EXISTING_SCOPE_ID);
  assert.deepEqual(patch.api.oauth2PermissionScopes, [existingScope]);
  assert.deepEqual(patch.api.preAuthorizedApplications, [{
    appId: APP_ID,
    delegatedPermissionIds: [EXISTING_SCOPE_ID],
  }]);
  assert.deepEqual(
    buildEntraAuthRegistrationPatch({ ...existing, ...patch }).patch.api.oauth2PermissionScopes,
    [existingScope],
  );
  assert.equal(buildEntraAuthRegistrationPatch({ ...existing, ...patch }).changed, false);
  assert.deepEqual(inspectEntraAuthRegistration({
    ...existing,
    ...patch,
    signInAudience: "AzureADMyOrg",
    isFallbackPublicClient: true,
  }, APP_ID), []);
});

test("buildEntraAuthRegistrationPatch: re-enables a disabled Admin scope without redefining consent", () => {
  const existingScope = {
    id: EXISTING_SCOPE_ID,
    value: ACCESS_AS_USER_SCOPE_VALUE,
    isEnabled: false,
    type: "Admin",
    adminConsentDescription: "Operator-approved description",
    userConsentDescription: "Existing user description",
  };
  const { patch } = buildEntraAuthRegistrationPatch({
    appId: APP_ID,
    api: { oauth2PermissionScopes: [existingScope] },
  });

  assert.deepEqual(patch.api.oauth2PermissionScopes, [{
    ...existingScope,
    isEnabled: true,
  }]);
});

test("buildEntraAuthRegistrationPatch: rejects invalid or ambiguous access_as_user definitions", () => {
  assert.throws(
    () => buildEntraAuthRegistrationPatch({
      appId: APP_ID,
      api: { oauth2PermissionScopes: [{ id: "invalid", value: ACCESS_AS_USER_SCOPE_VALUE, type: "User" }] },
    }),
    /invalid ID or is not a delegated scope/,
  );
  assert.throws(
    () => buildEntraAuthRegistrationPatch({
      appId: APP_ID,
      api: {
        oauth2PermissionScopes: [
          { id: EXISTING_SCOPE_ID, value: ACCESS_AS_USER_SCOPE_VALUE, type: "User", isEnabled: true },
          { id: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", value: ACCESS_AS_USER_SCOPE_VALUE, type: "User", isEnabled: true },
        ],
      },
    }),
    /multiple access_as_user scope definitions/,
  );
});

test("buildEntraAuthRegistrationPatch: rejects malformed permission arrays rather than dropping existing entries", () => {
  assert.throws(
    () => buildEntraAuthRegistrationPatch({
      appId: APP_ID,
      api: { oauth2PermissionScopes: [], preAuthorizedApplications: "invalid" },
    }),
    /invalid preAuthorizedApplications value/,
  );
  assert.throws(
    () => buildEntraAuthRegistrationPatch({
      appId: APP_ID,
      identifierUris: "invalid",
    }),
    /invalid identifierUris value/,
  );
  assert.throws(
    () => buildEntraAuthRegistrationPatch({
      appId: APP_ID,
      api: { preAuthorizedApplications: [{ appId: APP_ID, delegatedPermissionIds: "invalid" }] },
    }),
    /invalid self-client preauthorization permission list/,
  );
  assert.throws(
    () => buildEntraAuthRegistrationPatch({
      appId: APP_ID,
      requiredResourceAccess: [{ resourceAppId: APP_ID, resourceAccess: "invalid" }],
    }),
    /invalid self-resource permission list/,
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
  let currentApp;
  let staleScopeReads = 1;
  const app = {
    appId: APP_ID,
    appRoles: [],
    displayName: "agentweaver-authn",
    id: "33333333-3333-3333-3333-333333333333",
    isFallbackPublicClient: true,
    signInAudience: "AzureADMyOrg",
    publicClient: { redirectUris: ["https://agentweaver.example.com/auth/entra/callback"] },
    web: { redirectUris: [] },
  };
  const sp = {
    appId: app.appId,
    displayName: app.displayName,
    id: "44444444-4444-4444-4444-444444444444",
  };

  const exec = {
    async run(cmd, args) {
      commands.push([cmd, ...args]);
      if (args[0] === "rest") {
        const body = args[args.indexOf("--body") + 1];
        const currentScopes = currentApp?.api?.oauth2PermissionScopes ?? [];
        const preauthorizesUnpersistedScope = body.api?.preAuthorizedApplications?.some((client) =>
          client.appId === APP_ID &&
          client.delegatedPermissionIds?.some((id) =>
            !currentScopes.some((scope) => scope.id === id)));
        if (preauthorizesUnpersistedScope) {
          throw new Error("InvalidValue: delegatedPermissionIds has a Permission Id that cannot be found in the AppPermissions sets.");
        }
        const patch = JSON.parse(body);
        currentApp = {
          ...currentApp,
          ...patch,
          ...(patch.api ? { api: { ...currentApp.api, ...patch.api } } : {}),
        };
      }
      return { code: 0 };
    },
    async capture(cmd, args, opts = {}) {
      commands.push([cmd, ...args]);
      const joined = args.join(" ");
      if (joined.includes("account show")) return { code: 0, stdout: "72f988bf-86f1-41af-91ab-2d7cd011db47\n", stderr: "" };
      if (joined.includes("ad app list --display-name")) return { code: 0, stdout: "[]", stderr: "" };
      if (joined.includes("ad app create")) {
        currentApp = app;
        return { code: 0, stdout: JSON.stringify(currentApp), stderr: "" };
      }
      if (joined.includes("ad app show --id")) {
        const scopeHasBeenWritten = currentApp?.api?.oauth2PermissionScopes?.some(
          (scope) => scope.value === ACCESS_AS_USER_SCOPE_VALUE,
        );
        if (staleScopeReads > 0 && scopeHasBeenWritten) {
          staleScopeReads -= 1;
          return { code: 0, stdout: JSON.stringify(app), stderr: "" };
        }
        return { code: 0, stdout: JSON.stringify(currentApp), stderr: "" };
      }
      if (joined.includes("ad sp show --id")) {
        if (opts.allowFailure) return { code: 1, stdout: "", stderr: "not found" };
      }
      if (joined.includes("ad sp create --id")) return { code: 0, stdout: JSON.stringify(sp), stderr: "" };
      throw new Error(`Unexpected capture: ${joined}`);
    },
  };

  const result = await run({
    argv: ["--redirect-uri", "https://agentweaver.example.com/auth/entra/callback"],
    exec,
    log: noopLog(),
  });

  assert.equal(result.ok, true);
  assert.equal(result.appId, app.appId);
  assert.equal(result.delegatedScopeId, ACCESS_AS_USER_SCOPE_ID);
  assert.deepEqual(result.identifierUris, [`api://${APP_ID}`]);
  assert.equal(result.tenantId, "72f988bf-86f1-41af-91ab-2d7cd011db47");
  assert.equal(result.servicePrincipalObjectId, sp.id);
  assert.deepEqual(result.redirectUris, ["https://agentweaver.example.com/auth/entra/callback"]);
  assert.ok(commands.some((entry) => entry.includes("ad") && entry.includes("app") && entry.includes("create")));
  assert.ok(commands.some((entry) => entry.includes("rest") && entry.includes("PATCH")));
  const patchBodies = commands
    .filter((entry) => entry.includes("rest") && entry.includes("PATCH"))
    .map((entry) => JSON.parse(entry[entry.indexOf("--body") + 1]));
  const authPatches = patchBodies.filter((body) => body.api);
  assert.equal(authPatches.length, 2);
  const persistedScopeId = authPatches[0].api.oauth2PermissionScopes
    .find((scope) => scope.value === ACCESS_AS_USER_SCOPE_VALUE).id;
  assert.equal(authPatches[0].api.requestedAccessTokenVersion, 2);
  assert.ok(authPatches[0].identifierUris?.includes(`api://${APP_ID}`));
  assert.ok(authPatches[0].requiredResourceAccess?.some((resource) => resource.resourceAppId === APP_ID));
  assert.equal(
    authPatches[0].api.preAuthorizedApplications?.some((client) =>
      client.delegatedPermissionIds?.includes(persistedScopeId)),
    undefined,
  );
  assert.ok(authPatches[1].api.preAuthorizedApplications?.some((client) =>
    client.appId === APP_ID && client.delegatedPermissionIds?.includes(persistedScopeId)));
  assert.deepEqual(Object.keys(authPatches[1].api), ["preAuthorizedApplications"]);
  const firstAuthPatchIndex = commands.findIndex((entry) =>
    entry.includes("rest") && entry.includes("PATCH") &&
    JSON.parse(entry[entry.indexOf("--body") + 1]).api);
  const secondAuthPatchIndex = commands.findIndex((entry, index) =>
    index > firstAuthPatchIndex && entry.includes("rest") && entry.includes("PATCH") &&
    JSON.parse(entry[entry.indexOf("--body") + 1]).api);
  const readsBetweenPhases = commands.slice(firstAuthPatchIndex + 1, secondAuthPatchIndex)
    .filter((entry) => entry.includes("app") && entry.includes("show"));
  assert.ok(readsBetweenPhases.length >= 2, "scope must be re-read until Graph exposes it before preauthorization");
  assert.ok(patchBodies.some((body) => Array.isArray(body.appRoles)));
  assert.ok(commands.some((entry) => entry.includes("ad") && entry.includes("sp") && entry.includes("create")));
});

test("run: reusing an app with an operator-defined Admin scope is idempotent", async () => {
  const commands = [];
  const operatorScope = {
    id: EXISTING_SCOPE_ID,
    value: ACCESS_AS_USER_SCOPE_VALUE,
    isEnabled: true,
    type: "Admin",
    adminConsentDescription: "Existing operator-approved description",
    adminConsentDisplayName: "Existing operator-approved name",
    userConsentDescription: "Existing user description",
    userConsentDisplayName: "Existing user name",
  };
  const app = {
    appId: APP_ID,
    identifierUris: [`api://${APP_ID}`],
    api: {
      oauth2PermissionScopes: [operatorScope],
      preAuthorizedApplications: [{
        appId: APP_ID,
        delegatedPermissionIds: [EXISTING_SCOPE_ID],
      }],
      requestedAccessTokenVersion: 2,
    },
    requiredResourceAccess: [{
      resourceAppId: APP_ID,
      resourceAccess: [{ id: EXISTING_SCOPE_ID, type: "Scope" }],
    }],
    appRoles: DEFAULT_APP_ROLES,
    displayName: "agentweaver-authn",
    id: "33333333-3333-3333-3333-333333333333",
    isFallbackPublicClient: true,
    signInAudience: "AzureADMyOrg",
    publicClient: {
      redirectUris: [
        "http://localhost:5000/auth/entra/callback",
        "https://agentweaver.example.com/auth/entra/callback",
      ],
    },
    web: { redirectUris: [] },
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
  assert.equal(result.delegatedScopeId, EXISTING_SCOPE_ID);
  assert.ok(!commands.some((entry) => entry.includes("create") && entry.includes("app")));
  assert.ok(!commands.some((entry) => entry.includes("update") && entry.includes("public-client-redirect-uris")));
  assert.ok(!commands.some((entry) => entry.includes("update") && entry.includes("web-redirect-uris")));
  assert.ok(!commands.some((entry) => entry.includes("update") && entry.includes("is-fallback-public-client")));
  assert.ok(!commands.some((entry) => entry.includes("rest") && entry.includes("PATCH")));
  assert.ok(!commands.some((entry) => entry.includes("sp") && entry.includes("create")));
});

test("run: summary explains the publicClient-only PKCE fix and prints role-grant guidance", async () => {
  const log = captureLog();
  const app = {
    appId: APP_ID,
    ...buildEntraAuthRegistrationPatch({ appId: APP_ID }).patch,
    appRoles: DEFAULT_APP_ROLES,
    displayName: "agentweaver-authn",
    id: "33333333-3333-3333-3333-333333333333",
    isFallbackPublicClient: true,
    signInAudience: "AzureADMyOrg",
    publicClient: { redirectUris: ["https://agentweaver.example.com/auth/entra/callback"] },
    web: { redirectUris: [] },
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

  const result = await run({
    argv: ["--redirect-uri", "https://agentweaver.example.com/auth/entra/callback"],
    exec,
    log,
  });

  assert.equal(result.ok, true);
  const warning = log.entries.filter(([level]) => level === "warn").map(([, message]) => String(message)).join("\n");
  assert.match(warning, /Auth__Entra__ClientSecret must stay unset/);
  assert.match(warning, /publicClient/);
  assert.match(warning, /platform App Role/);
  assert.match(warning, /does not need a project membership/);

  const info = log.entries.filter(([level]) => level === "info").map(([, message]) => String(message)).join("\n");
  assert.match(info, /appRoleAssignedTo/);
  assert.match(info, new RegExp(sp.id));
  assert.match(info, /85fd3442-8291-4d52-a76f-ee962e711d7f/);
});
