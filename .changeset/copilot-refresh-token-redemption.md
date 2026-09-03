---
"agentweaver": patch
---

Redeem stored GitHub Copilot OAuth refresh tokens ahead of expiry instead of letting them go stale, fixing recurring forced re-authentication under Platform Settings even when a valid refresh token was already on file. A new `CopilotCredentialRefreshService` redeems the refresh token before the access token's lifetime elapses (redeem-ahead of `GitHubCapabilityBroker.MaximumCapabilityLifetime`), guarded by a concurrency-safe semaphore and ETag-conditional writes so concurrent redemption attempts across requests don't race or clobber each other's result.
