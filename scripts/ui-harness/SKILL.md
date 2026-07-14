# UI harness CLI contract

Use this harness to capture browser evidence for a persona against Agentweaver staging.
Run `npm --prefix scripts/ui-harness test` for fixture tests. Run `tools.mjs login`
once for manual headful authentication, then `init`, `goto`/`click`/`type-coordinator`,
and `finish` with the returned session id. `finish` prints driver P0 facts and
normalized evidence for `scripts/harness-judge/`; it does not certify UX quality.

Production targets require both `--allow-prod` and `--confirm-production`.
