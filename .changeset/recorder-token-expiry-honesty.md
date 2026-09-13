---
"agentweaver": patch
---

Stop the demo recorder reporting an expired session token as ready. `demo:record status` printed "Recording authentication: ready" based only on the auth directory existing and being git-ignored — it never decoded the cached token, so it reported ready against a token that had expired hours earlier while every authenticated endpoint returned 401. `getSessionToken` had no expiry check either, so the API harness's `recorder-session` auth provider handed out a dead bearer indefinitely.

`status` now prints the token's actual expiry (or how long ago it expired, with the `demo:record -- signin` command that fixes it), and no longer claims "ready" for an expired token. `getSessionToken` refuses a provably expired token rather than returning it; `{ allowExpired: true }` opts out, and tokens without a decodable `exp` are unaffected.
