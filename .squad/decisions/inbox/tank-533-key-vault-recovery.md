# Tank decision: recover and rotate preview credential tombstones

- Context: preview-runner credential names are deterministic per run, while terminal cleanup
  soft-deletes the Key Vault secret. An immediate retry therefore receives
  `ObjectIsDeletedButRecoverable`.
- Decision: preserve soft delete and purge protection. On that provider-specific conflict, recover
  the deterministic key, poll its active state for at most 30 seconds, then replace the recovered
  value with the retry's fresh credential. Treat a recovery `409` as a concurrent creator and join
  the same bounded poll.
- Rationale: purging would weaken the vault's retention policy, reusing the recovered credential
  would violate the fresh-on-launch policy, and randomizing the key would break replica-safe reads
  and deterministic cleanup. The recovery path requires no new role beyond the API identity's
  existing Key Vault Secrets Officer assignment and never includes secret values in logs or errors.
