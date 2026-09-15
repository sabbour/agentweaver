// session-token-expiry.test.mjs -- The cached recording session token must
// never be reported as usable, or handed to a caller, once it has expired.
//
// Regression context: `demo:record status` printed "Recording authentication:
// ready" purely because the auth directory existed and was git-ignored. It
// never decoded the token, so it reported ready against a token that had
// expired 22 hours earlier, while every authenticated endpoint returned 401.
// `getSessionToken` had no expiry check either, so the API harness's
// recorder-session auth provider handed out a dead bearer indefinitely.

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  decodeJwtExpiry,
  getSessionToken,
  getSessionTokenStatus,
  SessionTokenExpiredError,
} from "../lib/auth.mjs";

function makeJwt(claims) {
  const b64 = (value) =>
    Buffer.from(JSON.stringify(value)).toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return `${b64({ alg: "none", typ: "JWT" })}.${b64(claims)}.signature`;
}

function writeSeed(token) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "session-token-"));
  const file = path.join(dir, "sessionStorage.json");
  fs.writeFileSync(file, JSON.stringify({ entries: { "agentweaver.sessionToken": token } }), "utf8");
  return { dir, file };
}

const secondsFromNow = (seconds) => Math.floor(Date.now() / 1000) + seconds;

test("decodeJwtExpiry reads exp and tolerates anything that is not a JWT", () => {
  const expires = secondsFromNow(3600);
  assert.equal(decodeJwtExpiry(makeJwt({ exp: expires })).getTime(), expires * 1000);
  assert.equal(decodeJwtExpiry(makeJwt({ sub: "no-exp" })), null);
  assert.equal(decodeJwtExpiry("not-a-jwt"), null);
  assert.equal(decodeJwtExpiry("a.b"), null);
  assert.equal(decodeJwtExpiry(undefined), null);
});

test("getSessionToken refuses an expired token instead of handing out a dead bearer", async () => {
  const { dir, file } = writeSeed(makeJwt({ exp: secondsFromNow(-3600) }));
  try {
    await assert.rejects(getSessionToken(file), (error) => {
      assert.ok(error instanceof SessionTokenExpiredError);
      assert.match(error.message, /expired/i);
      // The message must name the only command that fixes it.
      assert.match(error.message, /demo:record -- signin/);
      return true;
    });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("getSessionToken returns a live token and can be forced to return an expired one", async () => {
  const live = writeSeed(makeJwt({ exp: secondsFromNow(3600) }));
  try {
    assert.ok(await getSessionToken(live.file));
  } finally {
    fs.rmSync(live.dir, { recursive: true, force: true });
  }

  const dead = writeSeed(makeJwt({ exp: secondsFromNow(-60) }));
  try {
    assert.ok(await getSessionToken(dead.file, { allowExpired: true }));
  } finally {
    fs.rmSync(dead.dir, { recursive: true, force: true });
  }
});

test("getSessionToken only refuses provable expiry, so non-JWT fixtures still work", async () => {
  const { dir, file } = writeSeed("opaque-non-jwt-token");
  try {
    assert.equal(await getSessionToken(file), "opaque-non-jwt-token");
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("getSessionTokenStatus reports expiry without throwing", async () => {
  const expired = writeSeed(makeJwt({ exp: secondsFromNow(-600) }));
  try {
    const status = await getSessionTokenStatus(expired.file);
    assert.equal(status.present, true);
    assert.equal(status.expired, true);
    assert.ok(status.minutesRemaining < 0);
    assert.ok(status.expiresAt instanceof Date);
  } finally {
    fs.rmSync(expired.dir, { recursive: true, force: true });
  }

  const live = writeSeed(makeJwt({ exp: secondsFromNow(1800) }));
  try {
    const status = await getSessionTokenStatus(live.file);
    assert.equal(status.expired, false);
    assert.ok(status.minutesRemaining > 0);
  } finally {
    fs.rmSync(live.dir, { recursive: true, force: true });
  }

  const status = await getSessionTokenStatus(path.join(os.tmpdir(), "definitely-missing-seed.json"));
  assert.deepEqual(status, { present: false, expiresAt: null, expired: false, minutesRemaining: null });
});
